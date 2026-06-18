using POE2_AutoMate.Core.Game;

namespace POE2_AutoMate.Automation;

public sealed class CombatBot
{
    private readonly GameInputController _inputController = new();
    private readonly RoutePlanner _routePlanner = new();
    private DateTime _lastStatusUtc = DateTime.MinValue;
    private DateTime _lastFlaskUtc = DateTime.MinValue;
    private DateTime _lastHealthFlaskUtc = DateTime.MinValue;
    private DateTime _lastManaFlaskUtc = DateTime.MinValue;
    private DateTime _lastAbilityUtc = DateTime.MinValue;
    private DateTime _lastComboUtc = DateTime.MinValue;
    private DateTime _lastTargetClickUtc = DateTime.MinValue;
    private DateTime _lastMoveUtc = DateTime.MinValue;
    private DateTime _lastLootUtc = DateTime.MinValue;
    private DateTime _lastExploreSwitchUtc = DateTime.MinValue;
    private DateTime _lastKiteUtc = DateTime.MinValue;
    private DateTime _lastBossPhaseDodgeUtc = DateTime.MinValue;
    private int _abilityIndex;
    private int _exploreDirectionIndex = -1;
    private int _routeWaypointIndex;
    private int _exploreRouteWaypointIndex;
    private int _bossRouteWaypointIndex;
    private RoutePoint? _exploreWaypoint;
    private IReadOnlyList<RoutePoint> _exploreRoute = [];
    private IReadOnlyList<RoutePoint> _bossRoute = [];
    private string _bossRouteKey = string.Empty;
    private long _currentArea;
    private uint _targetId;
    private float _mapEntryX;
    private float _mapEntryY;
    private bool _mapEntryKnown;
    private IReadOnlyList<RoutePoint> _route = [];
    private readonly Random _random = new();
    private readonly Dictionary<uint, LootAttemptState> _lootAttempts = [];
    private readonly Dictionary<uint, EncounterAttemptState> _encounterAttempts = [];
    private readonly Dictionary<uint, TargetAttemptState> _targetAttempts = [];
    private readonly HashSet<uint> _comboOpenedTargets = [];
    private readonly HashSet<string> _seenEncounterMetadata = [];
    private readonly HashSet<long> _visitedExploreCells = [];
    private readonly HashSet<long> _failedExploreCells = [];
    private bool _bossSeenThisArea;
    private bool _bossSeenAliveThisArea;
    private bool _bossKilledThisArea;

    public CombatConfig Config { get; private set; }
    public Func<GameSnapshot, EntityData, (double X, double Y)?>? LootLabelLocator { get; set; }
    public bool IsRunning { get; private set; }
    public CombatReport LastReport { get; private set; } = CombatReport.Idle;
    public event EventHandler<string>? StatusChanged;

    public CombatBot(CombatConfig config)
    {
        Config = config;
    }

    public void UpdateConfig(CombatConfig config)
    {
        Config = config;
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        LastReport = CombatReport.Waiting;
        StatusChanged?.Invoke(this, "Combat bot enabled. Full map loop mode.");
    }

    public void Stop()
    {
        StopMovement();
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        LastReport = CombatReport.Idle;
        StatusChanged?.Invoke(this, "Combat bot disabled.");
    }

    public CombatReport UpdateSnapshot(GameSnapshot? snapshot)
    {
        if (!IsRunning)
        {
            return LastReport;
        }

        if (snapshot?.Player is not { } player)
        {
            StopMovement();
            LastReport = CombatReport.Waiting;
            ThrottledStatus("Combat bot waiting for player data.");
            return LastReport;
        }

        if (_currentArea != snapshot.AreaInstanceAddress)
        {
            _currentArea = snapshot.AreaInstanceAddress;
            _targetId = 0;
            _routeWaypointIndex = 0;
            _exploreRouteWaypointIndex = 0;
            _bossRouteWaypointIndex = 0;
            _exploreWaypoint = null;
            _exploreRoute = [];
            _bossRoute = [];
            _bossRouteKey = string.Empty;
            _lootAttempts.Clear();
            _encounterAttempts.Clear();
            _targetAttempts.Clear();
            _comboOpenedTargets.Clear();
            _seenEncounterMetadata.Clear();
            _visitedExploreCells.Clear();
            _failedExploreCells.Clear();
            _bossSeenThisArea = false;
            _bossSeenAliveThisArea = false;
            _bossKilledThisArea = false;
            _mapEntryKnown = false;
            _route = [];
            StatusChanged?.Invoke(this, $"Combat bot entered area 0x{_currentArea:X}; map-enter step skipped.");
        }

        // Record the map entry position once the player resolves — boss rush biases exploration AWAY from
        // here (bosses sit at the deep end of the map), turning aimless wandering into a directed boss seek.
        if (!_mapEntryKnown && player.HasPosition)
        {
            _mapEntryX = player.X;
            _mapEntryY = player.Y;
            _mapEntryKnown = true;
        }

        int healthPercent = Percent(player.Health, player.MaxHealth);
        int effectiveHealthPercent = EffectiveHealthPercent(player);
        int manaPercent = Percent(player.Mana, player.MaxMana);
        int visibleMonsterCount = snapshot.Entities.Count(IsCombatMonster);
        int monsterCount = snapshot.MapMonsterCount ?? visibleMonsterCount;
        UpdateExplorationMemory(snapshot, player);
        UpdateBossState(snapshot);
        int nearbyMonsters = player.HasPosition
            ? snapshot.Entities.Count(entity => IsCombatMonster(entity) && Distance(player, entity) <= Config.CastRange)
            : 0;

        string action = TryUseLifeFlask(player, healthPercent, manaPercent);
        string state;
        string advice;
        LogSeenEncounters(snapshot, player);
        EntityData? target = SelectTarget(snapshot, player);
        BotDebugTarget? debugTarget = null;
        RoutePoint? nextWaypoint = null;
        RoutePoint? intendedClickPoint = null;

        if (!player.HasPosition)
        {
            StopMovement();
            state = "No Position";
            advice = "Render position is not resolved.";
        }
        else if (Config.PrioritizeBossTargets && _bossKilledThisArea)
        {
            // Boss rush: killing the boss completes the map. Remaining trash monsters are irrelevant.
            StopMovement();
            state = "Map Done";
            advice = "Boss rush: boss killed; map complete (remaining monsters ignored).";
        }
        else if (monsterCount <= Config.FinishMonsterCount && (!Config.RequireBossKillBeforeFinish || _bossKilledThisArea))
        {
            StopMovement();
            state = "Map Done";
            advice = Config.RequireBossKillBeforeFinish
                ? $"Monster count {monsterCount} <= finish threshold and boss is killed."
                : $"Monster count {monsterCount} <= finish threshold {Config.FinishMonsterCount}.";
        }
        else if (monsterCount <= Config.FinishMonsterCount && Config.RequireBossKillBeforeFinish && !_bossKilledThisArea)
        {
            state = "Boss Hunt";
            nextWaypoint = PickBossLandmarkWaypoint(snapshot, player) ?? PickExploreWaypoint(snapshot, player);
            intendedClickPoint = nextWaypoint;
            action = TryExploreMove(player, nextWaypoint);
            advice = _bossSeenThisArea
                ? "Boss was seen but kill was not confirmed; continuing boss hunt before leaving map."
                : "Monster threshold reached, but boss was not found yet; following boss landmark/explore route.";
        }
        else if (TryKite(snapshot, player, effectiveHealthPercent, out string kiteAction))
        {
            state = "Kite";
            // Drink the life flask while kiting: the kite threshold (effective Life+ES+Ward) can trip while
            // raw life% is still above the flask threshold, so the normal flask check wouldn't fire.
            string kiteFlask = KiteFlask();
            action = string.IsNullOrWhiteSpace(kiteFlask) ? kiteAction : $"{kiteAction}; {kiteFlask}";
            advice = $"Effective Life+ES+Ward is {effectiveHealthPercent}% <= kite threshold {Config.KiteEffectiveHealthPercent}%.";
        }
        else if (TryLoot(snapshot, player, out string lootAction))
        {
            StopMovement();
            state = "Loot";
            action = lootAction;
            advice = "Lootable item detected near player.";
        }
        else if (target is null)
        {
            state = "Explore";
            nextWaypoint = Config.PrioritizeBossTargets
                ? PickBossLandmarkWaypoint(snapshot, player) ?? PickExploreWaypoint(snapshot, player)
                : PickExploreWaypoint(snapshot, player);
            intendedClickPoint = nextWaypoint;
            action = TryExploreMove(player, nextWaypoint);
            advice = Config.PrioritizeBossTargets
                ? "No visible target; following decoded boss landmark route while clearing path threats."
                : "No visible target, but map monster count is above finish threshold; exploring.";
        }
        else
        {
            double targetDistance = Distance(player, target);
            debugTarget = new BotDebugTarget(target.Id, target.Category, target.Metadata, target.X, target.Y, targetDistance);
            if (_targetId != target.Id)
            {
                _targetId = target.Id;
                _routeWaypointIndex = 0;
                _route = _routePlanner.BuildRoute(snapshot, target, Config.RouteSearchRadiusCells);
                StatusChanged?.Invoke(this, $"Combat bot route planned to {target.Category} #{target.Id}: {_route.Count} waypoint(s).");
            }

            EncounterKind encounterKind = target.Category == "Encounter"
                ? GetEncounterKind(target.Metadata)
                : EncounterKind.None;
            bool isEncounter = encounterKind != EncounterKind.None;
            bool hasLineOfSight = !Config.AvoidCastingThroughWalls || isEncounter || HasTerrainLineOfSight(snapshot.Terrain, player, target);
            double actionRange = isEncounter ? EncounterActionRange(encounterKind) : Config.CastRange;
            TargetAttemptState targetState = TrackTargetAttempt(target, targetDistance <= actionRange && hasLineOfSight);
            if (targetDistance > actionRange || !hasLineOfSight)
            {
                state = "Approach";
                nextWaypoint = PickNextWaypoint(player) ?? new RoutePoint(target.X, target.Y);
                intendedClickPoint = nextWaypoint;
                action = TryMoveAlongRoute(snapshot, player, target);
                advice = !hasLineOfSight
                    ? $"Terrain blocks line of sight to {target.Category}; following route instead of casting through wall."
                    : $"Moving to {target.Category} at {targetDistance:0} world units; action range {actionRange:0}.";
            }
            else if (isEncounter)
            {
                StopMovement();
                state = "Encounter";
                intendedClickPoint = new RoutePoint(target.X, target.Y);
                action = TryHandleEncounter(snapshot, player, target, encounterKind);
                advice = $"{encounterKind} encounter in action range: {targetDistance:0} world units.";
            }
            else if (ShouldApproachForProximity(target, targetState, targetDistance))
            {
                state = "Proximity";
                nextWaypoint = PickNextWaypoint(player) ?? new RoutePoint(target.X, target.Y);
                intendedClickPoint = nextWaypoint;
                action = TryMoveAlongRoute(snapshot, player, target);
                advice = $"No HP progress yet; treating target as proximity-gated and moving closer ({targetDistance:0} > {Config.ProximityDamageRange:0}).";
            }
            else if (TryHandleBossInvulnerabilityPhase(snapshot, player, target, targetState, out string bossPhaseAction))
            {
                state = "Boss Phase";
                action = bossPhaseAction;
                advice = "Boss appears immune/no-progress; dodging and temporarily retargeting nearby adds.";
            }
            else if (ShouldIgnoreUnproductiveTarget(target, targetState, targetDistance, out string ignoreReason))
            {
                StopMovement();
                state = "Retarget";
                _targetId = 0;
                _routeWaypointIndex = 0;
                _route = [];
                action = ignoreReason;
                advice = "Target appears inactive/invulnerable; skipping it for now.";
            }
            else
            {
                if (!IsRunning)
                {
                    return LastReport;
                }

                StopMovement();
                state = "Fight";
                intendedClickPoint = new RoutePoint(target.X, target.Y);
                action = TryUseAbilities(snapshot, player, target);
                advice = $"Target in cast range: {target.Category} at {targetDistance:0} world units.";
            }
        }

        LastReport = new CombatReport(
            state,
            player.HasVitals,
            healthPercent,
            manaPercent,
            nearbyMonsters,
            monsterCount,
            _route.Count,
            advice,
            action,
            debugTarget,
            _route,
            nextWaypoint,
            intendedClickPoint);

        string monsterSource = snapshot.MapMonsterCount is null ? "entities" : $"map UI '{snapshot.MapMonsterCountText}'";
        ThrottledStatus($"Combat bot: {state}. Monsters {monsterCount} ({monsterSource}), nearby {nearbyMonsters}, route {_route.Count}. {action}");
        return LastReport;
    }

    private EntityData? SelectTarget(GameSnapshot snapshot, PlayerData player)
    {
        if (!player.HasPosition)
        {
            return null;
        }

        EntityData? nearestMonster = null;
        EntityData? nearestEncounter = null;
        EntityData? nearestBoss = null;
        EntityData? nearestPathThreat = null;
        EntityData? nearestValuable = null;
        double monsterDistance = double.MaxValue;
        double encounterDistance = double.MaxValue;
        double bossDistance = double.MaxValue;
        double valuableDistance = double.MaxValue;
        double pathThreatRadius = Config.PrioritizeBossTargets
            ? Math.Min(Config.PathClearMonsterRadius, Math.Max(Config.CastRange * 1.25, Config.NearbyMonsterRadius * 0.8))
            : Config.PathClearMonsterRadius;

        foreach (EntityData entity in snapshot.Entities)
        {
            if (ShouldIgnoreTargetMetadata(entity.Metadata))
            {
                continue;
            }

            if (IsTargetTemporarilyIgnored(entity.Id))
            {
                continue;
            }

            if (entity.Category == "Monster" && !IsCombatMonster(entity))
            {
                continue;
            }

            if (Config.PrioritizeBossTargets && entity.Category == "Monster" && ShouldSkipBossRushMonster(entity))
            {
                continue;
            }

            if (entity.Category == "Encounter" && (!Config.TargetEncounters || ShouldSkipEncounter(entity)))
            {
                continue;
            }

            if (entity.Category != "Monster" && entity.Category != "Encounter")
            {
                continue;
            }

            double distance = Distance(player, entity);
            if (entity.Category == "Monster" && IsBossMonster(entity) && distance < bossDistance)
            {
                nearestBoss = entity;
                bossDistance = distance;
            }

            // "Valuable" = a target worth prioritising regardless of nearest-trash: Rare/Unique/Boss
            // monsters and league encounters. Among these the NEAREST wins (so a unique closer than a
            // rare is hit first), per the requested Rare/Encounter/Unique/Boss priority.
            bool isValuable = (entity.Category == "Monster" && (entity.Rarity >= 2 || IsBossMonster(entity))) ||
                              entity.Category == "Encounter";
            if (isValuable && distance < valuableDistance)
            {
                nearestValuable = entity;
                valuableDistance = distance;
            }

            if (entity.Category == "Monster" && distance < monsterDistance)
            {
                nearestMonster = entity;
                monsterDistance = distance;
                if (distance <= pathThreatRadius)
                {
                    nearestPathThreat = entity;
                }
            }
            else if (entity.Category == "Encounter" && distance < encounterDistance)
            {
                nearestEncounter = entity;
                encounterDistance = distance;
            }
        }

        if (Config.PrioritizeBossTargets)
        {
            if (nearestPathThreat is not null)
            {
                return nearestPathThreat;
            }

            if (nearestBoss is not null)
            {
                return nearestBoss;
            }

            return null;
        }

        // Survival first: anything right next to the player gets cleared so we are not swarmed while
        // walking to a far target.
        if (nearestPathThreat is not null)
        {
            return nearestPathThreat;
        }

        // Then the nearest VALUABLE target (Rare/Unique/Boss/Encounter), nearest-wins across rarities.
        if (nearestValuable is not null)
        {
            return nearestValuable;
        }

        // Otherwise just clear the nearest trash monster (fall back to an encounter if no monster).
        return nearestMonster ?? nearestEncounter;
    }

    private bool ShouldSkipEncounter(EntityData entity)
    {
        if (ShouldIgnoreTargetMetadata(entity.Metadata) ||
            entity.Metadata.Contains("Expedition", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return GetEncounterKind(entity.Metadata) switch
        {
            EncounterKind.Ritual => true,
            EncounterKind.Strongbox => true,
            EncounterKind.Expedition => true,
            _ => false
        };
    }

    private double EncounterActionRange(EncounterKind kind)
    {
        return kind switch
        {
            EncounterKind.Breach => Config.EncounterEnterRange,
            EncounterKind.Delirium => Config.EncounterEnterRange,
            EncounterKind.Essence => Config.EncounterInteractRange,
            EncounterKind.Shrine => Config.EncounterInteractRange,
            _ => Config.EncounterInteractRange
        };
    }

    private static EncounterKind GetEncounterKind(string metadata)
    {
        if (metadata.Contains("Expedition", StringComparison.OrdinalIgnoreCase))
        {
            return EncounterKind.Expedition;
        }

        if (metadata.Contains("Metadata/Shrines/", StringComparison.OrdinalIgnoreCase))
        {
            return EncounterKind.Shrine;
        }

        if (metadata.Contains("StrongBoxes", StringComparison.OrdinalIgnoreCase))
        {
            return EncounterKind.Strongbox;
        }

        if (metadata.Contains("Breach", StringComparison.OrdinalIgnoreCase))
        {
            return EncounterKind.Breach;
        }

        if (metadata.Contains("Ritual", StringComparison.OrdinalIgnoreCase))
        {
            return EncounterKind.Ritual;
        }

        if (metadata.Contains("Delirium", StringComparison.OrdinalIgnoreCase))
        {
            return EncounterKind.Delirium;
        }

        if (metadata.Contains("Essence", StringComparison.OrdinalIgnoreCase))
        {
            return EncounterKind.Essence;
        }

        return EncounterKind.Unknown;
    }

    private void LogSeenEncounters(GameSnapshot snapshot, PlayerData player)
    {
        if (!Config.TargetEncounters || !player.HasPosition)
        {
            return;
        }

        foreach (EntityData encounter in snapshot.Entities.Where(entity => entity.Category == "Encounter"))
        {
            string key = ShortMetadata(encounter.Metadata);
            if (!_seenEncounterMetadata.Add(key))
            {
                continue;
            }

            StatusChanged?.Invoke(
                this,
                $"Seen encounter: id={encounter.Id}, distance={Distance(player, encounter):0}, metadata={encounter.Metadata}");
        }
    }

    private TargetAttemptState TrackTargetAttempt(EntityData target, bool inActionRange)
    {
        DateTime now = DateTime.UtcNow;
        if (!_targetAttempts.TryGetValue(target.Id, out TargetAttemptState? state))
        {
            state = new TargetAttemptState
            {
                FirstSeenUtc = now,
                LastProgressUtc = now,
                LastHealth = target.EffectiveHealth,
                LastMaxHealth = target.MaxHealth
            };
            _targetAttempts[target.Id] = state;
        }

        // Progress = the EFFECTIVE pool (life + energy shield) dropping. An ES target that loses shield but
        // not life is still being damaged, so it must NOT be flagged "invulnerable/unproductive".
        if (target.HasLife && (target.EffectiveHealth < state.LastHealth || target.MaxHealth != state.LastMaxHealth))
        {
            state.LastProgressUtc = now;
            state.LastHealth = target.EffectiveHealth;
            state.LastMaxHealth = target.MaxHealth;
        }

        if (inActionRange)
        {
            if (state.InActionRangeUtc == DateTime.MinValue)
            {
                state.InActionRangeUtc = now;
            }
        }
        else
        {
            state.InActionRangeUtc = DateTime.MinValue;
        }

        return state;
    }

    private void UpdateBossState(GameSnapshot snapshot)
    {
        foreach (EntityData entity in snapshot.Entities.Where(IsMapBoss))
        {
            _bossSeenThisArea = true;

            // Must observe the boss ALIVE first. Bosses can transiently read Health<=0 (intro animation,
            // energy-shield phase, a one-tick misread), which previously triggered a FALSE kill confirm —
            // and with "boss killed = map done" that made the bot abandon the fight. Only a seen-alive boss
            // that then reads dead (valid Max, Current<=0) counts as a real kill, and we log it once.
            if (entity.HasLife && entity.MaxHealth > 0 && entity.Health > 0)
            {
                _bossSeenAliveThisArea = true;
            }
            else if (_bossSeenAliveThisArea && !_bossKilledThisArea &&
                     entity.HasLife && entity.MaxHealth > 0 && entity.Health <= 0)
            {
                _bossKilledThisArea = true;
                StatusChanged?.Invoke(this, $"Boss kill confirmed from entity #{entity.Id}: {ShortMetadata(entity.Metadata)}");
            }
        }
    }

    private void UpdateExplorationMemory(GameSnapshot snapshot, PlayerData player)
    {
        if (snapshot.Terrain is null || !player.HasPosition)
        {
            return;
        }

        int centerX = ToGrid(player.X);
        int centerY = ToGrid(player.Y);
        const int revealRadius = 18;
        for (int dy = -revealRadius; dy <= revealRadius; dy++)
        {
            for (int dx = -revealRadius; dx <= revealRadius; dx++)
            {
                if ((dx * dx) + (dy * dy) > revealRadius * revealRadius)
                {
                    continue;
                }

                int x = centerX + dx;
                int y = centerY + dy;
                if (IsWalkable(snapshot.Terrain, x, y))
                {
                    _visitedExploreCells.Add(GridKey(x, y));
                }
            }
        }
    }

    private bool ShouldApproachForProximity(EntityData target, TargetAttemptState state, double targetDistance)
    {
        if ((target.HasLife && target.Health <= 0) || state.InActionRangeUtc == DateTime.MinValue)
        {
            return false;
        }

        TimeSpan noProgress = DateTime.UtcNow - state.LastProgressUtc;
        return noProgress >= TimeSpan.FromMilliseconds(Config.UnproductiveTargetTimeoutMs) &&
               targetDistance > Config.ProximityDamageRange;
    }

    private bool ShouldIgnoreUnproductiveTarget(EntityData target, TargetAttemptState state, double targetDistance, out string reason)
    {
        reason = string.Empty;
        if ((target.HasLife && target.Health <= 0) || state.InActionRangeUtc == DateTime.MinValue)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        TimeSpan noProgress = now - state.LastProgressUtc;
        TimeSpan inRange = now - state.InActionRangeUtc;
        TimeSpan timeout = TimeSpan.FromMilliseconds(Config.UnproductiveTargetTimeoutMs);
        if (noProgress < timeout || inRange < timeout)
        {
            return false;
        }

        if (targetDistance > Config.ProximityDamageRange)
        {
            return false;
        }

        state.IgnoreUntilUtc = now.AddMilliseconds(Config.UnproductiveTargetIgnoreMs);
        reason = $"Ignoring target #{target.Id}: no HP progress for {noProgress.TotalSeconds:0.0}s even at proximity range {targetDistance:0}.";
        StatusChanged?.Invoke(this, reason);
        return true;
    }

    private bool TryHandleBossInvulnerabilityPhase(
        GameSnapshot snapshot,
        PlayerData player,
        EntityData target,
        TargetAttemptState state,
        out string action)
    {
        action = string.Empty;
        if (!IsBossMonster(target) ||
            state.InActionRangeUtc == DateTime.MinValue ||
            DateTime.UtcNow - state.LastProgressUtc < TimeSpan.FromMilliseconds(Config.UnproductiveTargetTimeoutMs))
        {
            return false;
        }

        state.IgnoreUntilUtc = DateTime.UtcNow.AddMilliseconds(Config.BossPhaseRetargetMs);
        EntityData? add = snapshot.Entities
            .Where(IsCombatMonster)
            .Where(entity => !IsBossMonster(entity))
            .OrderBy(entity => Distance(player, entity))
            .FirstOrDefault(entity => Distance(player, entity) <= Config.NearbyMonsterRadius);
        if (add is not null)
        {
            _targetId = 0;
            _routeWaypointIndex = 0;
            _route = [];
            action = $"Boss no-progress: retargeting nearby add #{add.Id} while boss phase resolves.";
            return true;
        }

        if (DateTime.UtcNow - _lastBossPhaseDodgeUtc < TimeSpan.FromMilliseconds(Config.BossPhaseDodgeCooldownMs))
        {
            action = "Boss no-progress: dodge cooling down; boss temporarily ignored.";
            return true;
        }

        _lastBossPhaseDodgeUtc = DateTime.UtcNow;
        IReadOnlyList<string> keys = RandomMovementKeys();
        bool moved = _inputController.HoldKeys(Config.ProcessName, keys, Config.KiteMoveHoldMs);
        bool dodged = _inputController.PressKey(Config.ProcessName, Config.KiteDodgeKey);
        action = moved || dodged
            ? $"Boss no-progress: evasive move {string.Join("+", keys)} + {Config.KiteDodgeKey}."
            : $"Boss no-progress: evasive input failed: {_inputController.LastError}";
        return true;
    }

    private IReadOnlyList<string> RandomMovementKeys()
    {
        return _random.Next(0, 8) switch
        {
            0 => [Config.MoveUpKey],
            1 => [Config.MoveDownKey],
            2 => [Config.MoveLeftKey],
            3 => [Config.MoveRightKey],
            4 => [Config.MoveUpKey, Config.MoveLeftKey],
            5 => [Config.MoveUpKey, Config.MoveRightKey],
            6 => [Config.MoveDownKey, Config.MoveLeftKey],
            _ => [Config.MoveDownKey, Config.MoveRightKey]
        };
    }

    private bool IsTargetTemporarilyIgnored(uint targetId)
    {
        if (!_targetAttempts.TryGetValue(targetId, out TargetAttemptState? state))
        {
            return false;
        }

        if (state.IgnoreUntilUtc <= DateTime.UtcNow)
        {
            return false;
        }

        return true;
    }

    private bool TryKite(GameSnapshot snapshot, PlayerData player, int effectiveHealthPercent, out string action)
    {
        action = "No kite.";
        if (!IsRunning)
        {
            return false;
        }

        if (!Config.ActiveInputEnabled || !Config.KitingEnabled || !player.HasVitals || player.Health <= 0)
        {
            return false;
        }

        if (effectiveHealthPercent > Config.KiteEffectiveHealthPercent)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (now - _lastKiteUtc < TimeSpan.FromMilliseconds(Config.KiteCooldownMs))
        {
            action = "Kite cooling down.";
            return false;
        }

        _lastKiteUtc = now;
        IReadOnlyList<string> keys = RandomKiteKeys(snapshot, player);
        if (!_inputController.SetHeldKeys(Config.ProcessName, keys))
        {
            action = $"Kite move failed: {_inputController.LastError}";
            return true;
        }

        Thread.Sleep(20);
        bool dodged = _inputController.PressKey(Config.ProcessName, Config.KiteDodgeKey);
        Thread.Sleep(Math.Clamp(Config.KiteMoveHoldMs, 20, 500));
        _inputController.ReleaseHeldKeys(Config.ProcessName);
        action = dodged
            ? $"Kite dodge: held {string.Join("+", keys)} and pressed {Config.KiteDodgeKey} at effective HP {effectiveHealthPercent}%."
            : $"Kite dodge failed after holding {string.Join("+", keys)}: {_inputController.LastError}";
        return true;
    }

    private IReadOnlyList<string> RandomKiteKeys(GameSnapshot snapshot, PlayerData player)
    {
        double awayX = 0;
        double awayY = 0;
        foreach (EntityData monster in snapshot.Entities.Where(IsCombatMonster))
        {
            double distance = Math.Max(1, Distance(player, monster));
            if (distance > Config.NearbyMonsterRadius)
            {
                continue;
            }

            awayX += (player.X - monster.X) / distance;
            awayY += (player.Y - monster.Y) / distance;
        }

        if (Math.Abs(awayX) + Math.Abs(awayY) < 0.001)
        {
            double angle = _random.NextDouble() * Math.PI * 2.0;
            awayX = Math.Cos(angle);
            awayY = Math.Sin(angle);
        }
        else
        {
            double angleJitter = (_random.NextDouble() - 0.5) * 1.1;
            double cos = Math.Cos(angleJitter);
            double sin = Math.Sin(angleJitter);
            (awayX, awayY) = ((awayX * cos) - (awayY * sin), (awayX * sin) + (awayY * cos));
        }

        (double inputDx, double inputDy) = MovementInputDirection(awayX, awayY);
        return KeysForDirection(inputDx, inputDy);
    }

    private string TryMoveAlongRoute(GameSnapshot snapshot, PlayerData player, EntityData fallbackTarget)
    {
        if (!Config.ActiveInputEnabled || !Config.AutoMoveEnabled)
        {
            StopMovement();
            return Config.ActiveInputEnabled ? "Auto move disabled." : "Active input disabled.";
        }

        if (DateTime.UtcNow - _lastMoveUtc < TimeSpan.FromMilliseconds(Config.MoveCooldownMs))
        {
            return Config.MovementMode.Equals("WASD", StringComparison.OrdinalIgnoreCase)
                ? "Continuing held movement."
                : "Move cooling down.";
        }

        RoutePoint? nextWaypoint = PickNextWaypoint(player);
        if (nextWaypoint is null && snapshot.Terrain is not null)
        {
            StopMovement();
            return $"No walkable route to {fallbackTarget.Category}; refusing direct wall path.";
        }

        RoutePoint next = nextWaypoint ?? new RoutePoint(fallbackTarget.X, fallbackTarget.Y);
        return TryMoveTowardPoint(player, next, "route waypoint");
    }

    private string TryExploreMove(PlayerData player, RoutePoint? nextWaypoint)
    {
        if (nextWaypoint is null)
        {
            StopMovement();
            return "No exploration waypoint.";
        }

        if (!Config.ExploreWhenNoTarget)
        {
            StopMovement();
            return "Exploration disabled.";
        }

        return TryMoveTowardPoint(player, nextWaypoint.Value, "explore waypoint");
    }

    private string TryMoveTowardPoint(PlayerData player, RoutePoint next, string label)
    {
        if (!IsRunning)
        {
            return "Combat stopped.";
        }

        if (!Config.ActiveInputEnabled || !Config.AutoMoveEnabled)
        {
            StopMovement();
            return Config.ActiveInputEnabled ? "Auto move disabled." : "Active input disabled.";
        }

        if (DateTime.UtcNow - _lastMoveUtc < TimeSpan.FromMilliseconds(Config.MoveCooldownMs))
        {
            return Config.MovementMode.Equals("WASD", StringComparison.OrdinalIgnoreCase)
                ? "Continuing held movement."
                : "Move cooling down.";
        }

        double dx = next.X - player.X;
        double dy = next.Y - player.Y;
        (double inputDx, double inputDy) = MovementInputDirection(dx, dy);
        _lastMoveUtc = DateTime.UtcNow;

        if (Config.MovementMode.Equals("WASD", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> movementKeys = KeysForDirection(inputDx, inputDy);
            if (_inputController.SetHeldKeys(Config.ProcessName, movementKeys))
            {
                return $"Holding {string.Join("+", movementKeys)} toward {label} ({next.X:0},{next.Y:0}); input=({inputDx:0.00},{inputDy:0.00}).";
            }

            StatusChanged?.Invoke(this, $"Combat bot WASD move failed: {_inputController.LastError}");
            return $"WASD move failed: {_inputController.LastError}";
        }

        if (_inputController.ClickDirection(Config.ProcessName, inputDx, inputDy, Config.MoveClickPixels))
        {
            return $"Clicked move toward {label} ({next.X:0},{next.Y:0}).";
        }

        StatusChanged?.Invoke(this, $"Combat bot move click failed: {_inputController.LastError}");
        return $"Move failed: {_inputController.LastError}";
    }

    private RoutePoint? PickNextWaypoint(PlayerData player)
    {
        if (_route.Count == 0)
        {
            return null;
        }

        _routeWaypointIndex = Math.Clamp(_routeWaypointIndex, 0, _route.Count - 1);

        int closestIndex = _routeWaypointIndex;
        double closestDistance = double.MaxValue;
        for (int i = _routeWaypointIndex; i < _route.Count; i++)
        {
            double distance = Distance(player, _route[i]);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestIndex = i;
            }
        }

        if (closestIndex > _routeWaypointIndex)
        {
            _routeWaypointIndex = closestIndex;
        }

        while (_routeWaypointIndex < _route.Count &&
               Distance(player, _route[_routeWaypointIndex]) <= Config.WaypointReachDistance)
        {
            _routeWaypointIndex++;
        }

        return _routeWaypointIndex < _route.Count ? _route[_routeWaypointIndex] : null;
    }

    private RoutePoint? PickExploreWaypoint(GameSnapshot snapshot, PlayerData player)
    {
        if (!player.HasPosition)
        {
            return null;
        }

        DateTime now = DateTime.UtcNow;
        bool needsNewWaypoint = _exploreWaypoint is null ||
                                Distance(player, _exploreWaypoint.Value) <= Config.WaypointReachDistance * 1.35 ||
                                _lastExploreSwitchUtc == DateTime.MinValue ||
                                now - _lastExploreSwitchUtc >= TimeSpan.FromMilliseconds(Config.ExploreDirectionMs);
        if (!needsNewWaypoint)
        {
            return PickNextExploreWaypoint(player);
        }

        _lastExploreSwitchUtc = now;
        _exploreWaypoint = snapshot.Terrain is { } terrain
            ? PickGlobalExploreWaypoint(snapshot, terrain, player)
            : PickRandomExploreWaypoint(player);

        return PickNextExploreWaypoint(player);
    }

    private RoutePoint? PickBossLandmarkWaypoint(GameSnapshot snapshot, PlayerData player)
    {
        if (!Config.PrioritizeBossTargets || snapshot.Landmarks.Count == 0 || !player.HasPosition)
        {
            return null;
        }

        LandmarkData? landmark = snapshot.Landmarks
            .Where(IsBossLandmark)
            .OrderBy(item => Distance(player.X, player.Y, item.X, item.Y))
            .FirstOrDefault();
        if (landmark is null)
        {
            return null;
        }

        string key = $"{landmark.Label}|{landmark.X:0}|{landmark.Y:0}";
        if (!key.Equals(_bossRouteKey, StringComparison.Ordinal) || _bossRoute.Count == 0)
        {
            _bossRouteKey = key;
            _bossRouteWaypointIndex = 0;
            _bossRoute = _routePlanner.BuildRouteToPoint(snapshot, landmark.X, landmark.Y, Config.ExploreRouteSearchRadiusCells);
            StatusChanged?.Invoke(this, $"Boss landmark route planned to {landmark.Label} at ({landmark.X:0},{landmark.Y:0}): {_bossRoute.Count} waypoint(s).");
        }

        while (_bossRouteWaypointIndex < _bossRoute.Count &&
               Distance(player, _bossRoute[_bossRouteWaypointIndex]) <= Config.WaypointReachDistance)
        {
            _bossRouteWaypointIndex++;
        }

        if (_bossRouteWaypointIndex < _bossRoute.Count)
        {
            return _bossRoute[_bossRouteWaypointIndex];
        }

        return Distance(player.X, player.Y, landmark.X, landmark.Y) <= Config.WaypointReachDistance * 2
            ? null
            : PickExploreWaypoint(snapshot, player);
    }

    private RoutePoint PickRandomExploreWaypoint(PlayerData player)
    {
        _exploreDirectionIndex = (_exploreDirectionIndex + 1 + _random.Next(0, 3)) % 8;
        double angle = (_exploreDirectionIndex / 8.0 * Math.PI * 2.0) + (_random.NextDouble() - 0.5) * 0.55;
        double dx = Math.Cos(angle);
        double dy = Math.Sin(angle);
        double length = Math.Max(0.001, Math.Sqrt((dx * dx) + (dy * dy)));
        double step = Math.Max(100, Config.ExploreStepWorldUnits);
        return new RoutePoint(
            (float)(player.X + (dx / length * step)),
            (float)(player.Y + (dy / length * step)));
    }

    private RoutePoint PickWalkableExploreWaypoint(TerrainData terrain, PlayerData player)
    {
        int originX = ToGrid(player.X);
        int originY = ToGrid(player.Y);
        int minRadius = Math.Max(8, (int)Math.Round(Config.WaypointReachDistance / Poe2Offsets.WorldToGridRatio));
        int maxRadius = Math.Max(minRadius + 4, (int)Math.Round(Config.ExploreStepWorldUnits / Poe2Offsets.WorldToGridRatio));
        double phase = _random.NextDouble() * Math.PI * 2.0;

        RoutePoint? best = null;
        double bestScore = double.MinValue;
        for (int radius = minRadius; radius <= maxRadius; radius += 6)
        {
            int samples = Math.Clamp(radius / 2, 16, 48);
            for (int i = 0; i < samples; i++)
            {
                double angle = phase + (Math.PI * 2.0 * i / samples);
                int x = originX + (int)Math.Round(Math.Cos(angle) * radius);
                int y = originY + (int)Math.Round(Math.Sin(angle) * radius);
                if (!IsWalkable(terrain, x, y))
                {
                    continue;
                }

                RoutePoint candidate = new(
                    (float)(x * Poe2Offsets.WorldToGridRatio),
                    (float)(y * Poe2Offsets.WorldToGridRatio));
                double distance = Distance(player, candidate);
                double score = distance + (_random.NextDouble() * Config.ExploreStepWorldUnits * 0.25);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
        }

        return best ?? PickRandomExploreWaypoint(player);
    }

    private RoutePoint PickGlobalExploreWaypoint(GameSnapshot snapshot, TerrainData terrain, PlayerData player)
    {
        int originX = ToGrid(player.X);
        int originY = ToGrid(player.Y);
        int stride = Math.Clamp((int)Math.Round(Config.ExploreFrontierStrideCells), 6, 32);
        int minDistance = Math.Max(24, (int)Math.Round(Config.ExploreStepWorldUnits / Poe2Offsets.WorldToGridRatio * 0.65));

        // In boss rush, score frontiers by how DEEP they are (distance from the map entry) so the bot drives
        // toward the boss end of the map instead of circling near spawn. Otherwise score by distance from the
        // player (normal spread-out exploration).
        bool bossSeek = Config.PrioritizeBossTargets && _mapEntryKnown;
        int anchorX = bossSeek ? ToGrid(_mapEntryX) : originX;
        int anchorY = bossSeek ? ToGrid(_mapEntryY) : originY;

        int phaseX = _random.Next(0, stride);
        int phaseY = _random.Next(0, stride);

        // Collect scored frontier candidates (unvisited walkable cells, far enough, away from walls).
        List<(double Score, int X, int Y)> candidates = [];
        for (int y = phaseY; y < terrain.Height; y += stride)
        {
            for (int x = phaseX; x < terrain.Width; x += stride)
            {
                if (!IsWalkable(terrain, x, y))
                {
                    continue;
                }

                long key = GridKey(x, y);
                if (_visitedExploreCells.Contains(key) || _failedExploreCells.Contains(key))
                {
                    continue;
                }

                int dxp = x - originX;
                int dyp = y - originY;
                double distFromPlayer = Math.Sqrt((dxp * dxp) + (dyp * dyp));
                if (distFromPlayer < minDistance)
                {
                    continue;
                }

                int dxa = x - anchorX;
                int dya = y - anchorY;
                double distFromAnchor = Math.Sqrt((dxa * dxa) + (dya * dya));
                double wallPenalty = CountNearbyBlocked(terrain, x, y);
                // bossSeek: anchor = entry → favour the deepest cells. Normal: anchor = player → spread out.
                double score = distFromAnchor - (wallPenalty * 4.0) + _random.NextDouble() * 12.0;
                candidates.Add((score, x, y));
            }
        }

        // Commit to the highest-scoring frontier that is ACTUALLY REACHABLE by a route. Unreachable cells
        // are marked failed so we never straight-line into a wall or re-pick them (kills the wall-ramming
        // and the circling). Only the route is followed for movement.
        foreach ((double _, int x, int y) in candidates.OrderByDescending(c => c.Score).Take(Math.Max(1, Config.ExploreReachableAttempts)))
        {
            RoutePoint target = new((float)(x * Poe2Offsets.WorldToGridRatio), (float)(y * Poe2Offsets.WorldToGridRatio));
            IReadOnlyList<RoutePoint> route = _routePlanner.BuildRouteToPoint(snapshot, target.X, target.Y, Config.ExploreRouteSearchRadiusCells);
            if (route.Count > 1)
            {
                _exploreRoute = route;
                _exploreRouteWaypointIndex = 0;
                StatusChanged?.Invoke(this, $"Explore frontier ({target.X:0},{target.Y:0}); route {route.Count} wp, visited {_visitedExploreCells.Count} cells.");
                return target;
            }

            _failedExploreCells.Add(GridKey(x, y));
        }

        // No reachable frontier among the tried candidates: reset visited memory and fall back to a NEAR
        // walkable ring point that we can route to.
        _visitedExploreCells.Clear();
        RoutePoint near = PickWalkableExploreWaypoint(terrain, player);
        _exploreRoute = _routePlanner.BuildRouteToPoint(snapshot, near.X, near.Y, Config.ExploreRouteSearchRadiusCells);
        _exploreRouteWaypointIndex = 0;
        return near;
    }

    private RoutePoint? PickNextExploreWaypoint(PlayerData player)
    {
        // No reachable route -> return null so the caller STOPS instead of straight-lining into a wall.
        if (_exploreRoute.Count == 0)
        {
            return null;
        }

        _exploreRouteWaypointIndex = Math.Clamp(_exploreRouteWaypointIndex, 0, _exploreRoute.Count - 1);
        while (_exploreRouteWaypointIndex < _exploreRoute.Count &&
               Distance(player, _exploreRoute[_exploreRouteWaypointIndex]) <= Config.WaypointReachDistance)
        {
            _exploreRouteWaypointIndex++;
        }

        return _exploreRouteWaypointIndex < _exploreRoute.Count
            ? _exploreRoute[_exploreRouteWaypointIndex]
            : null;
    }

    private static int CountNearbyBlocked(TerrainData terrain, int x, int y)
    {
        int blocked = 0;
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                if (!IsWalkable(terrain, x + dx, y + dy))
                {
                    blocked++;
                }
            }
        }

        return blocked;
    }

    private IReadOnlyList<string> KeysForDirection(double dx, double dy)
    {
        List<string> keys = [];
        double absX = Math.Abs(dx);
        double absY = Math.Abs(dy);
        const double diagonalBias = 0.35;

        if (absY >= absX * diagonalBias)
        {
            keys.Add(dy < 0 ? Config.MoveUpKey : Config.MoveDownKey);
        }

        if (absX >= absY * diagonalBias)
        {
            keys.Add(dx < 0 ? Config.MoveLeftKey : Config.MoveRightKey);
        }

        return keys.Count == 0 ? [Config.MoveUpKey] : keys;
    }

    private string TryUseAbilities(GameSnapshot snapshot, PlayerData player, EntityData target)
    {
        if (!IsRunning)
        {
            return "Combat stopped.";
        }

        if (!Config.ActiveInputEnabled)
        {
            return "Active input disabled.";
        }

        if (!Config.AutoAttackEnabled)
        {
            return "Auto attack disabled.";
        }

        string targetClick = TryClickTarget(snapshot, player, target);
        string[] comboKeys = ComboKeysFor(target);
        bool isUnique = target.Rarity == 3 || target.Metadata.Contains("Unique", StringComparison.OrdinalIgnoreCase);
        bool comboAlreadyOpened = _comboOpenedTargets.Contains(target.Id);
        bool canRepeatCombo = !isUnique || Config.UniqueComboRepeatEnabled;
        int comboCooldownMs = isUnique && comboAlreadyOpened
            ? Config.UniqueComboRecastMs
            : Config.ComboCooldownMs;
        if (comboKeys.Length > 0 &&
            (!comboAlreadyOpened || canRepeatCombo) &&
            DateTime.UtcNow - _lastComboUtc >= TimeSpan.FromMilliseconds(comboCooldownMs))
        {
            _lastComboUtc = DateTime.UtcNow;
            _comboOpenedTargets.Add(target.Id);
            string combo = PressSequence(comboKeys);
            string mode = comboAlreadyOpened ? "repeat combo" : "opener combo";
            return $"{targetClick} {mode}: {combo}".Trim();
        }

        string[] abilityKeys = Config.AbilityKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToArray();
        if (abilityKeys.Length == 0)
        {
            return "No ability keys configured.";
        }

        if (DateTime.UtcNow - _lastAbilityUtc < TimeSpan.FromMilliseconds(Config.AbilityCooldownMs))
        {
            return "Ability cooling down.";
        }

        _lastAbilityUtc = DateTime.UtcNow;
        if (Config.PressAllAbilities)
        {
            return $"{targetClick} {PressSequence(abilityKeys)}".Trim();
        }

        string ability = abilityKeys[_abilityIndex % abilityKeys.Length];
        _abilityIndex++;
        if (_inputController.PressKey(Config.ProcessName, ability))
        {
            return $"{targetClick} Pressed ability {ability}.".Trim();
        }

        StatusChanged?.Invoke(this, $"Combat bot ability input failed: {_inputController.LastError}");
        return $"Ability input failed: {_inputController.LastError}";
    }

    private string TryHandleEncounter(GameSnapshot snapshot, PlayerData player, EntityData encounter, EncounterKind kind)
    {
        if (!IsRunning)
        {
            return "Combat stopped.";
        }

        if (!Config.ActiveInputEnabled)
        {
            return "Active input disabled.";
        }

        if (DateTime.UtcNow - _lastTargetClickUtc < TimeSpan.FromMilliseconds(Config.EncounterInteractCooldownMs))
        {
            return "Encounter interact cooling down.";
        }

        _lastTargetClickUtc = DateTime.UtcNow;
        return kind switch
        {
            EncounterKind.Breach or EncounterKind.Delirium => TryEnterEncounterPoint(player, encounter, kind),
            EncounterKind.Essence => TryClickEncounterRepeated(snapshot, encounter, Config.EssenceClickCount, "essence"),
            EncounterKind.Shrine => TryClickEncounterOnce(snapshot, encounter, "shrine"),
            EncounterKind.Expedition => "Expedition skipped: reward value planner is not implemented yet.",
            EncounterKind.Ritual => "Ritual skipped.",
            EncounterKind.Strongbox => "Strongbox skipped.",
            _ => TryClickEncounterOnce(snapshot, encounter, "encounter")
        };
    }

    private string TryEnterEncounterPoint(PlayerData player, EntityData encounter, EncounterKind kind)
    {
        double dx = encounter.X - player.X;
        double dy = encounter.Y - player.Y;
        (double inputDx, double inputDy) = MovementInputDirection(dx, dy);
        if (Config.MovementMode.Equals("WASD", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> movementKeys = KeysForDirection(inputDx, inputDy);
            return _inputController.SetHeldKeys(Config.ProcessName, movementKeys)
                ? $"Entering {kind}: holding {string.Join("+", movementKeys)} into marker."
                : $"{kind} enter failed: {_inputController.LastError}";
        }

        return _inputController.ClickDirection(Config.ProcessName, inputDx, inputDy, Config.MoveClickPixels)
            ? $"Entering {kind}: clicked through marker."
            : $"{kind} enter failed: {_inputController.LastError}";
    }

    private string TryClickEncounterRepeated(GameSnapshot snapshot, EntityData encounter, int clickCount, string label)
    {
        if (!_encounterAttempts.TryGetValue(encounter.Id, out EncounterAttemptState? state))
        {
            state = new EncounterAttemptState();
            _encounterAttempts[encounter.Id] = state;
        }

        if (state.Clicks >= clickCount)
        {
            return $"{label} already clicked {state.Clicks}/{clickCount}.";
        }

        int clickedNow = 0;
        while (state.Clicks < clickCount)
        {
            if (!_inputController.ClickWorldPoint(Config.ProcessName, encounter.X, encounter.Y, encounter.Z, snapshot.CameraMatrix))
            {
                return clickedNow > 0
                    ? $"Clicked {label} #{encounter.Id}: {state.Clicks}/{clickCount}; last click failed: {_inputController.LastError}"
                    : $"{label} click failed: {_inputController.LastError}";
            }

            clickedNow++;
            state.Clicks++;
            state.LastAttemptUtc = DateTime.UtcNow;
            Thread.Sleep(Config.EncounterMultiClickDelayMs);
        }

        return $"Clicked {label} #{encounter.Id}: {state.Clicks}/{clickCount}.";
    }

    private string TryClickEncounterOnce(GameSnapshot snapshot, EntityData encounter, string label)
    {
        if (_inputController.ClickWorldPoint(Config.ProcessName, encounter.X, encounter.Y, encounter.Z, snapshot.CameraMatrix))
        {
            return $"Clicked {label} #{encounter.Id}.";
        }

        if (_inputController.PressKey(Config.ProcessName, Config.EncounterInteractKey))
        {
            return $"Pressed {label} key {Config.EncounterInteractKey}.";
        }

        return $"{label} click failed: {_inputController.LastError}";
    }

    private string TryClickTarget(GameSnapshot snapshot, PlayerData player, EntityData target)
    {
        if (!Config.ClickTargetBeforeAttack)
        {
            return string.Empty;
        }

        if (DateTime.UtcNow - _lastTargetClickUtc < TimeSpan.FromMilliseconds(Config.TargetClickCooldownMs))
        {
            return string.Empty;
        }

        _lastTargetClickUtc = DateTime.UtcNow;
        if (_inputController.ClickWorldPoint(Config.ProcessName, target.X, target.Y, target.Z, snapshot.CameraMatrix))
        {
            return $"Clicked target #{target.Id} screen position.";
        }

        double dx = target.X - player.X;
        double dy = target.Y - player.Y;
        (double inputDx, double inputDy) = MovementInputDirection(dx, dy);
        double distancePixels = Math.Clamp(
            Math.Sqrt((dx * dx) + (dy * dy)) / Math.Max(1, Config.AttackWorldUnitsPerPixel),
            30,
            Config.AttackClickMaxPixels);

        return _inputController.ClickDirection(Config.ProcessName, inputDx, inputDy, distancePixels)
            ? $"Clicked target #{target.Id}."
            : $"Target click failed: {_inputController.LastError}";
    }

    private string[] ComboKeysFor(EntityData target)
    {
        if ((target.Rarity == 3 || target.Metadata.Contains("Unique", StringComparison.OrdinalIgnoreCase)) && Config.UniqueComboKeys.Length > 0)
        {
            return Config.UniqueComboKeys;
        }

        if ((target.Rarity == 2 || target.Metadata.Contains("Rare", StringComparison.OrdinalIgnoreCase)) && Config.RareComboKeys.Length > 0)
        {
            return Config.RareComboKeys;
        }

        return [];
    }

    private string PressSequence(IReadOnlyList<string> keys)
    {
        List<string> pressed = [];
        foreach (string key in keys.Where(key => !string.IsNullOrWhiteSpace(key)))
        {
            if (_inputController.PressKey(Config.ProcessName, key))
            {
                pressed.Add(key);
            }
        }

        return pressed.Count == 0
            ? $"Input failed: {_inputController.LastError}"
            : $"Pressed: {string.Join(", ", pressed)}.";
    }

    private bool TryLoot(GameSnapshot snapshot, PlayerData player, out string action)
    {
        action = "No loot.";
        if (!IsRunning)
        {
            return false;
        }

        if (!Config.ActiveInputEnabled || !Config.AutoLootEnabled || !player.HasPosition)
        {
            return false;
        }

        EntityData? blockingMonster = snapshot.Entities
            .Where(IsCombatMonster)
            .OrderBy(entity => Distance(player, entity))
            .FirstOrDefault();
        if (blockingMonster is not null)
        {
            double monsterDistance = Distance(player, blockingMonster);
            if (monsterDistance <= Config.LootSafeMonsterRadius)
            {
                action = $"Loot blocked: monster #{blockingMonster.Id} at {monsterDistance:0}.";
                return false;
            }
        }

        PruneLootBlacklist();
        EntityData? loot = snapshot.Entities
            .Where(entity => entity.Category == "Loot")
            .Where(IsLootAllowed)
            .Where(entity => !IsLootBlacklisted(entity.Id))
            .OrderBy(entity => Distance(player, entity))
            .FirstOrDefault();

        if (loot is null || Distance(player, loot) > Config.LootRange)
        {
            return false;
        }

        if (DateTime.UtcNow - _lastLootUtc < TimeSpan.FromMilliseconds(Config.LootCooldownMs))
        {
            action = "Loot cooling down.";
            return true;
        }

        _lastLootUtc = DateTime.UtcNow;
        if (TryClickLootLabel(snapshot, loot, out string clickAction))
        {
            TrackLootAttempt(loot.Id);
            action = clickAction;
            return true;
        }

        double dx = loot.X - player.X;
        double dy = loot.Y - player.Y;
        (double inputDx, double inputDy) = MovementInputDirection(dx, dy);
        double distancePixels = Math.Clamp(
            Math.Sqrt((dx * dx) + (dy * dy)) / Math.Max(1, Config.AttackWorldUnitsPerPixel),
            25,
            Config.AttackClickMaxPixels);

        if (_inputController.ClickDirection(Config.ProcessName, inputDx, inputDy, distancePixels))
        {
            TrackLootAttempt(loot.Id);
            action = $"Clicked loot entity #{loot.Id}.";
            return true;
        }

        action = $"Loot click failed: {_inputController.LastError}";
        return true;
    }

    private bool TryClickLootLabel(GameSnapshot snapshot, EntityData loot, out string action)
    {
        action = "Loot label click failed.";
        if (LootLabelLocator?.Invoke(snapshot, loot) is { } label &&
            _inputController.ClickWindowPixelPoint(Config.ProcessName, label.X, label.Y))
        {
            action = $"Clicked loot entity #{loot.Id} visible label ({label.X:0},{label.Y:0})px.";
            return true;
        }

        double baseOffset = Config.LootLabelYOffsetPixels;
        double spread = Math.Max(4, Config.LootLabelSearchPixels);
        double[] xOffsets = Config.LootLabelXOffsetPixels == 0
            ? [0, -spread, spread]
            : [Config.LootLabelXOffsetPixels, Config.LootLabelXOffsetPixels - spread, Config.LootLabelXOffsetPixels + spread, 0];
        double[] yOffsets = baseOffset == 0
            ? [0, -spread, -spread * 2, -spread * 3]
            : [baseOffset, baseOffset - spread, baseOffset + spread, baseOffset - (spread * 2), 0];

        int attempts = 0;
        foreach (double yOffset in yOffsets.Distinct())
        {
            foreach (double xOffset in xOffsets.Distinct())
            {
                if (++attempts > Config.LootLabelClickAttempts)
                {
                    return false;
                }

                if (_inputController.ClickWorldPoint(Config.ProcessName, loot.X, loot.Y, loot.Z, snapshot.CameraMatrix, xOffset, yOffset))
                {
                    action = $"Clicked loot entity #{loot.Id} label offset ({xOffset:0},{yOffset:0})px.";
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsLootAllowed(EntityData entity)
    {
        if (LootClassification.IsCurrencyMetadata(entity.Metadata))
        {
            return Config.LootCurrencyEnabled;
        }

        if (LootClassification.IsGearMetadata(entity.Metadata))
        {
            return Config.LootGearEnabled;
        }

        return Config.LootOtherEnabled;
    }

    private void TrackLootAttempt(uint lootId)
    {
        if (!_lootAttempts.TryGetValue(lootId, out LootAttemptState? state))
        {
            state = new LootAttemptState();
            _lootAttempts[lootId] = state;
        }

        state.Attempts++;
        state.LastAttemptUtc = DateTime.UtcNow;
        if (state.Attempts >= Config.LootMaxAttemptsPerItem)
        {
            state.IgnoreUntilUtc = DateTime.UtcNow.AddSeconds(Config.LootFailedBlacklistSeconds);
            StatusChanged?.Invoke(this, $"Loot #{lootId} ignored for {Config.LootFailedBlacklistSeconds:0}s after {state.Attempts} failed pickup attempts.");
        }
    }

    private bool IsLootBlacklisted(uint lootId)
    {
        return _lootAttempts.TryGetValue(lootId, out LootAttemptState? state) &&
               state.IgnoreUntilUtc > DateTime.UtcNow;
    }

    private void PruneLootBlacklist()
    {
        DateTime cutoff = DateTime.UtcNow.AddMinutes(-5);
        foreach (uint lootId in _lootAttempts
                     .Where(pair => pair.Value.IgnoreUntilUtc <= DateTime.UtcNow && pair.Value.LastAttemptUtc < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _lootAttempts.Remove(lootId);
        }
    }

    private (double X, double Y) MovementInputDirection(double dx, double dy)
    {
        if (Config.ProjectMovementToScreen)
        {
            (dx, dy) = MapProjection.GridDeltaToMapDelta(
                dx / Poe2Offsets.WorldToGridRatio,
                dy / Poe2Offsets.WorldToGridRatio,
                1);
        }

        return (
            Config.InvertInputX ? -dx : dx,
            Config.InvertInputY ? -dy : dy);
    }

    private void StopMovement()
    {
        _inputController.ReleaseHeldKeys(Config.ProcessName);
    }

    private string TryUseLifeFlask(PlayerData player, int healthPercent, int manaPercent)
    {
        if (!player.HasVitals)
        {
            return "Vitals unavailable: no flask.";
        }

        bool lowHealth = healthPercent <= Config.MinHealthPercent;
        bool lowMana = manaPercent <= Config.MinManaPercent;
        if (!lowHealth && !lowMana)
        {
            return "No defensive action.";
        }

        if (!Config.ActiveInputEnabled)
        {
            return "Low vitals, active input disabled.";
        }

        DateTime now = DateTime.UtcNow;
        List<string> actions = [];
        if (lowHealth)
        {
            actions.Add(TryPressFlaskGroup(
                Config.HealthFlaskKeys.Length == 0 ? Config.FlaskKeys : Config.HealthFlaskKeys,
                "HP",
                now,
                ref _lastHealthFlaskUtc));
        }

        if (lowMana)
        {
            actions.Add(TryPressFlaskGroup(
                Config.ManaFlaskKeys.Length == 0 ? Config.FlaskKeys : Config.ManaFlaskKeys,
                "mana",
                now,
                ref _lastManaFlaskUtc));
        }

        return string.Join(" ", actions.Where(action => !string.IsNullOrWhiteSpace(action)));
    }

    /// <summary>Press the life flask while kiting (respects the flask cooldown), regardless of the raw
    /// life% threshold — kiting is itself a "take damage / get low" signal.</summary>
    private string KiteFlask()
    {
        if (!Config.ActiveInputEnabled)
        {
            return string.Empty;
        }

        return TryPressFlaskGroup(
            Config.HealthFlaskKeys.Length == 0 ? Config.FlaskKeys : Config.HealthFlaskKeys,
            "HP",
            DateTime.UtcNow,
            ref _lastHealthFlaskUtc);
    }

    private string TryPressFlaskGroup(IReadOnlyList<string> keys, string label, DateTime now, ref DateTime lastPressedUtc)
    {
        string? flaskKey = keys.FirstOrDefault(key => !string.IsNullOrWhiteSpace(key));
        if (string.IsNullOrWhiteSpace(flaskKey))
        {
            return $"No {label} flask key configured.";
        }

        if (now - lastPressedUtc < TimeSpan.FromMilliseconds(Config.FlaskCooldownMs))
        {
            return $"{label} flask cooling down.";
        }

        lastPressedUtc = now;
        Thread.Sleep(Math.Clamp(Config.FlaskInputDelayMs, 0, 120));
        return _inputController.PressKey(Config.ProcessName, flaskKey)
            ? $"Pressed {label} flask {flaskKey}."
            : $"{label} flask failed: {_inputController.LastError}";
    }

    private void ThrottledStatus(string message)
    {
        if (DateTime.UtcNow - _lastStatusUtc < TimeSpan.FromSeconds(4))
        {
            return;
        }

        _lastStatusUtc = DateTime.UtcNow;
        StatusChanged?.Invoke(this, message);
    }

    private static int Percent(int current, int max)
    {
        return max <= 0 ? 0 : Math.Clamp((int)Math.Round(current / (double)max * 100), 0, 999);
    }

    private static int EffectiveHealthPercent(PlayerData player)
    {
        int current = Math.Max(0, player.Health) +
                      Math.Max(0, player.EnergyShield) +
                      Math.Max(0, player.Ward);
        int max = Math.Max(0, player.MaxHealth) +
                  Math.Max(0, player.MaxEnergyShield) +
                  Math.Max(0, player.MaxWard);
        return max <= 0 ? Percent(player.Health, player.MaxHealth) : Math.Clamp((int)Math.Round(current / (double)max * 100), 0, 999);
    }

    private static double Distance(PlayerData player, EntityData entity)
    {
        double dx = entity.X - player.X;
        double dy = entity.Y - player.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double Distance(PlayerData player, RoutePoint point)
    {
        double dx = point.X - player.X;
        double dy = point.Y - player.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2;
        double dy = y1 - y2;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static int ToGrid(float world) => (int)Math.Round(world / Poe2Offsets.WorldToGridRatio);

    private static string ShortMetadata(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return string.Empty;
        }

        return metadata.Length <= 180 ? metadata : metadata[^180..];
    }

    private static bool IsWalkable(TerrainData terrain, int x, int y)
    {
        return x >= 0 &&
               y >= 0 &&
               x < terrain.Width &&
               y < terrain.Height &&
               terrain.Walkable[(y * terrain.Width) + x] != 0;
    }

    private static long GridKey(int x, int y) => ((long)y << 32) | (uint)x;

    private static bool ShouldIgnoreTargetMetadata(string metadata) =>
        metadata.Contains("Expedition", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Azmeri", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Azmiri", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Barrel", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Barricade", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Breakable", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Crate", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Destructible", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Destroyable", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Fence", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Palisade", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Pottery", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Urn", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Vase", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("GroundEffect", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("AreaOfEffect", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Explosion", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Impact", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Nova", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Beam", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Daemon", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Decoy", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Hazard", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Invisible", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Telegraph", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Triggered", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Volatile", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Summoned", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Minion", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Illusion", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Mirage", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Clone", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Projectile", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("SkillEffect", StringComparison.OrdinalIgnoreCase);

    private static bool HasTerrainLineOfSight(TerrainData? terrain, PlayerData player, EntityData target)
    {
        if (terrain is null || !player.HasPosition)
        {
            return true;
        }

        int x0 = ToGrid(player.X);
        int y0 = ToGrid(player.Y);
        int x1 = ToGrid(target.X);
        int y1 = ToGrid(target.Y);
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int blocked = 0;

        while (true)
        {
            if (!IsWalkable(terrain, x0, y0) && ++blocked > 2)
            {
                return false;
            }

            if (x0 == x1 && y0 == y1)
            {
                return true;
            }

            int e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x0 += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private static bool IsCombatMonster(EntityData entity)
    {
        bool friendly = entity.Reaction >= 0 && (entity.Reaction & 0x7F) == 1;
        // Require a readable, ALIVE life component. Event-locked / dormant mobs (e.g. an arena's adds that
        // stay frozen until the boss dies) decode as monsters but expose NO HP, so they must be ignored for
        // targeting, monster counts, nearby-threat and the loot gate until they actually activate (expose
        // HP). Active monsters read HasLife reliably (validated offsets), so real threats are not skipped.
        return entity.Category == "Monster" &&
               !friendly &&
               !ShouldIgnoreTargetMetadata(entity.Metadata) &&
               entity.HasLife &&
               entity.IsAlive;
    }

    private static bool IsBossMonster(EntityData entity)
    {
        if (entity.Category != "Monster" || ShouldIgnoreTargetMetadata(entity.Metadata))
        {
            return false;
        }

        return entity.Rarity == 3 ||
               entity.Metadata.Contains("Boss", StringComparison.OrdinalIgnoreCase) ||
               entity.Metadata.Contains("MapBoss", StringComparison.OrdinalIgnoreCase) ||
               entity.Metadata.Contains("Unique", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The actual MAP BOSS (for completion), NOT just any unique. Rogue Exiles / unique packs are
    /// Rarity==Unique but must not count as "the boss killed = map done" — require boss metadata.</summary>
    private static bool IsMapBoss(EntityData entity) =>
        entity.Category == "Monster" &&
        !ShouldIgnoreTargetMetadata(entity.Metadata) &&
        (entity.Metadata.Contains("MapBoss", StringComparison.OrdinalIgnoreCase) ||
         entity.Metadata.Contains("Boss", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldSkipBossRushMonster(EntityData entity)
    {
        if (IsBossMonster(entity))
        {
            return false;
        }

        // Dormant ambush packs, scenery creatures, boss attack helpers and some destructibles often decode
        // as monsters but have no readable life component yet. Boss Rush should not spend opener shots
        // probing those; once they activate and expose HP, they become valid path-clear threats.
        return !entity.HasLife || !entity.IsAlive;
    }

    private static bool IsBossLandmark(LandmarkData landmark)
    {
        string text = $"{landmark.Label} {landmark.Name} {landmark.TilePath}";
        return text.Contains("Boss", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Arena", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Final", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CombatConfig
{
    public string ProcessName { get; set; } = "PathOfExileSteam";
    public string AttackSkillKey { get; set; } = "1";
    public string[] AbilityKeys { get; set; } = ["1"];
    public string[] RareComboKeys { get; set; } = [];
    public string[] UniqueComboKeys { get; set; } = [];
    public string[] FlaskKeys { get; set; } = ["2", "3", "4", "5"];
    public string[] HealthFlaskKeys { get; set; } = ["1"];
    public string[] ManaFlaskKeys { get; set; } = ["2"];
    public string LootKey { get; set; } = "F";
    public string EncounterInteractKey { get; set; } = "F";
    public int MinHealthPercent { get; set; } = 50;
    public int MinManaPercent { get; set; } = 35;
    public double CombatRange { get; set; } = 550;
    public double CastRange { get; set; } = 420;
    public double EncounterInteractRange { get; set; } = 260;
    public double EncounterEnterRange { get; set; } = 45;
    public double NearbyMonsterRadius { get; set; } = 650;
    public double PathClearMonsterRadius { get; set; } = 950;
    public int DangerMonsterCount { get; set; } = 6;
    public int FinishMonsterCount { get; set; } = 10;
    public bool ActiveInputEnabled { get; set; }
    public bool AutoMoveEnabled { get; set; }
    public bool AutoAttackEnabled { get; set; }
    public bool PressAllAbilities { get; set; }
    public bool AutoLootEnabled { get; set; }
    public bool LootCurrencyEnabled { get; set; } = true;
    public bool LootGearEnabled { get; set; } = true;
    public bool LootOtherEnabled { get; set; }
    public bool TargetEncounters { get; set; }
    public bool RequireBossKillBeforeFinish { get; set; } = true;
    public bool PrioritizeBossTargets { get; set; }
    public bool UniqueComboRepeatEnabled { get; set; }
    public bool ClickTargetBeforeAttack { get; set; } = true;
    public bool ProjectMovementToScreen { get; set; } = true;
    public bool InvertInputX { get; set; }
    public bool InvertInputY { get; set; }
    public int AbilityCooldownMs { get; set; } = 650;
    public int AbilityDelayMs { get; set; } = 90;
    public int ComboCooldownMs { get; set; } = 2200;
    public int UniqueComboRecastMs { get; set; } = 4000;
    public int TargetClickCooldownMs { get; set; } = 650;
    public int AttackCooldownMs { get; set; } = 900;
    public int FlaskCooldownMs { get; set; } = 3200;
    public int FlaskInputDelayMs { get; set; } = 20;
    public bool KitingEnabled { get; set; } = true;
    public int KiteEffectiveHealthPercent { get; set; } = 55;
    public int KiteCooldownMs { get; set; } = 1400;
    public int KiteMoveHoldMs { get; set; } = 130;
    public string KiteDodgeKey { get; set; } = "SPACE";
    public int UnproductiveTargetTimeoutMs { get; set; } = 2600;
    public int UnproductiveTargetIgnoreMs { get; set; } = 12000;
    public double ProximityDamageRange { get; set; } = 155;
    public int BossPhaseRetargetMs { get; set; } = 2200;
    public int BossPhaseDodgeCooldownMs { get; set; } = 750;
    public int MoveCooldownMs { get; set; } = 160;
    public string MovementMode { get; set; } = "MouseClick";
    public string MoveUpKey { get; set; } = "W";
    public string MoveLeftKey { get; set; } = "A";
    public string MoveDownKey { get; set; } = "S";
    public string MoveRightKey { get; set; } = "D";
    public int WasdHoldMs { get; set; } = 260;
    public double MoveClickPixels { get; set; } = 230;
    public double AttackWorldUnitsPerPixel { get; set; } = 12;
    public double AttackClickMaxPixels { get; set; } = 430;
    public double WaypointReachDistance { get; set; } = 170;
    public int RouteSearchRadiusCells { get; set; } = 180;
    public double LootRange { get; set; } = 260;
    public int LootCooldownMs { get; set; } = 900;
    public double LootLabelXOffsetPixels { get; set; }
    public double LootLabelYOffsetPixels { get; set; } = -54;
    public double LootLabelSearchPixels { get; set; } = 34;
    public int LootLabelClickAttempts { get; set; } = 6;
    public int LootMaxAttemptsPerItem { get; set; } = 3;
    public double LootFailedBlacklistSeconds { get; set; } = 120;
    public double LootSafeMonsterRadius { get; set; } = 850;
    public bool ExploreWhenNoTarget { get; set; } = true;
    public double ExploreStepWorldUnits { get; set; } = 900;
    public int ExploreDirectionMs { get; set; } = 2500;
    public double ExploreFrontierStrideCells { get; set; } = 12;
    public int ExploreRouteSearchRadiusCells { get; set; } = 180;
    public int ExploreReachableAttempts { get; set; } = 6;
    public int EncounterInteractCooldownMs { get; set; } = 1200;
    public int EssenceClickCount { get; set; } = 4;
    public int EncounterMultiClickDelayMs { get; set; } = 110;
    public bool AvoidCastingThroughWalls { get; set; } = true;
    public bool Enabled { get; set; }
}

public sealed class LootAttemptState
{
    public int Attempts { get; set; }
    public DateTime LastAttemptUtc { get; set; }
    public DateTime IgnoreUntilUtc { get; set; }
}

public sealed class EncounterAttemptState
{
    public int Clicks { get; set; }
    public DateTime LastAttemptUtc { get; set; }
}

public sealed class TargetAttemptState
{
    public DateTime FirstSeenUtc { get; set; }
    public DateTime InActionRangeUtc { get; set; }
    public DateTime LastProgressUtc { get; set; }
    public DateTime IgnoreUntilUtc { get; set; }
    public int LastHealth { get; set; }
    public int LastMaxHealth { get; set; }
}

public enum EncounterKind
{
    None,
    Unknown,
    Breach,
    Ritual,
    Expedition,
    Delirium,
    Essence,
    Strongbox,
    Shrine
}

public sealed record CombatReport(
    string State,
    bool HasVitals,
    int HealthPercent,
    int ManaPercent,
    int NearbyMonsterCount,
    int RemainingMonsterCount,
    int RouteWaypointCount,
    string Advice,
    string LastAction,
    BotDebugTarget? Target,
    IReadOnlyList<RoutePoint> Route,
    RoutePoint? NextWaypoint,
    RoutePoint? IntendedClickPoint)
{
    public static CombatReport Idle { get; } = new("Idle", false, 0, 0, 0, 0, 0, "Monitor disabled.", "n/a", null, [], null, null);
    public static CombatReport Waiting { get; } = new("Waiting", false, 0, 0, 0, 0, 0, "Waiting for live player data.", "n/a", null, [], null, null);
}

public sealed record BotDebugTarget(
    uint Id,
    string Category,
    string Metadata,
    float X,
    float Y,
    double Distance);
