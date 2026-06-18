using System.Text.RegularExpressions;
using POE2_AutoMate.Core.Game;

namespace POE2_AutoMate.Automation;

public sealed class AutoMapper
{
    private readonly GameInputController _inputController = new();
    private AutoMapperState _state = AutoMapperState.Idle;
    private DateTime _stateEnteredUtc = DateTime.MinValue;
    private DateTime _lastActionUtc = DateTime.MinValue;
    private DateTime _lastStatusUtc = DateTime.MinValue;
    private int _currentRun;
    private string _selectedMap = "n/a";
    private string _lastError = string.Empty;
    private float _lastAtlasX;
    private float _lastAtlasY;
    private int _lastAtlasGridX;
    private int _lastAtlasGridY;
    private long _hoveredWaystoneAddress;
    private DateTime _waystoneHoverUtc = DateTime.MinValue;
    private readonly HashSet<long> _skippedWaystones = [];
    private int _waystoneExaltAttempts;
    private bool _alchemyAttempted;
    private bool _portalClicked;
    private long _portalAreaInstance;
    private bool _portalDiagnosed;
    private int _portalClickAttempts;
    private DateTime _mapReadySinceUtc = DateTime.MinValue;
    private int _hideoutStableTicks;
    private long _returnPortalAreaInstance;
    private bool _bossSeenThisMap;
    private bool _bossSeenAliveThisMap;
    private bool _bossKilledThisMap;
    private float _lastMapDeviceX;
    private float _lastMapDeviceY;
    private float _lastMapDeviceZ;
    private bool _waystoneInventoryOpened;
    private readonly Dictionary<string, int> _lootCounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex RevivesRegex = new(@"revives?\s+available\s*:?\s*(?<count>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TierRegex = new(@"\b(?:tier|t)\s*(?<tier>\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WaystoneClipboardTierRegex = new(@"\bWaystone\s*\(\s*Tier\s*(?<tier>\d{1,2})\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RarityRegex = new(@"(?:^|\r?\n)\s*Rarity\s*:\s*(?<rarity>[^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AutoMapperConfig Config { get; private set; } = new();

    /// <summary>
    /// Supplies the live atlas nodes (read from memory) + the player's current node + window size, used by
    /// <see cref="SelectMap"/> to pick a real map node by data and click its exact projected position.
    /// Wired by the host; null falls back to a failure with diagnostics.
    /// </summary>
    public Func<AtlasSelection?>? AtlasNodeProvider { get; set; }

    /// <summary>
    /// Locates a visible UI button by text (e.g. "Traverse") in the full UI tree from memory and returns
    /// its window-local pixel center. Used by <see cref="ActivateMap"/> to click the map-device activation
    /// button at its true screen position. Wired by the host.
    /// </summary>
    public Func<string[], (float X, float Y)?>? UiButtonLocator { get; set; }

    public bool IsRunning { get; private set; }
    public AutoMapperReport LastReport { get; private set; } = AutoMapperReport.Idle;
    public event EventHandler<string>? StatusChanged;

    public void UpdateConfig(AutoMapperConfig config)
    {
        Config = config;
        _currentRun = Math.Clamp(config.CurrentRun, 0, Math.Max(0, config.RunsTarget));
        _lastAtlasX = config.LastAtlasNodeX;
        _lastAtlasY = config.LastAtlasNodeY;
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        _currentRun = Math.Clamp(Config.CurrentRun, 0, Math.Max(0, Config.RunsTarget));
        Enter(AutoMapperState.DetectLocation, "AutoStart enabled.");
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        Enter(AutoMapperState.Idle, "AutoStart stopped.");
    }

    public AutoMapperReport UpdateSnapshot(GameSnapshot? snapshot)
    {
        if (!IsRunning)
        {
            LastReport = AutoMapperReport.Idle;
            return LastReport;
        }

        if (snapshot is null)
        {
            LastReport = BuildReport("Waiting for resolved game snapshot.");
            return LastReport;
        }

        string action = _state switch
        {
            AutoMapperState.DetectLocation => DetectLocation(snapshot),
            AutoMapperState.ReturnToHideout => ReturnToHideout(snapshot),
            AutoMapperState.FindStash => FindAndClickEntity(snapshot, "Stash", AutoMapperState.TakeSupplies, "stash"),
            AutoMapperState.TakeSupplies => TakeSupplies(snapshot),
            AutoMapperState.FindMapDevice => FindAndClickEntity(snapshot, "MapDevice", AutoMapperState.OpenAtlas, "map device"),
            AutoMapperState.OpenAtlas => OpenAtlas(snapshot),
            AutoMapperState.SelectMap => SelectMap(snapshot),
            AutoMapperState.PrepareWaystone => PrepareWaystone(snapshot),
            AutoMapperState.ActivateMap => ActivateMap(snapshot),
            AutoMapperState.EnterPortal => EnterPortal(snapshot),
            AutoMapperState.WaitForMap => WaitForMap(snapshot),
            AutoMapperState.ClearMap => ClearMap(snapshot),
            AutoMapperState.ReturnPortal => ReturnPortal(snapshot),
            AutoMapperState.WaitForHideout => WaitForHideout(snapshot),
            AutoMapperState.DumpInventory => DumpInventory(snapshot),
            AutoMapperState.NextRun => NextRun(),
            AutoMapperState.Completed => "Run target reached.",
            AutoMapperState.Failed => $"Stopped: {_lastError}",
            _ => "Idle."
        };

        LastReport = BuildReport(action, snapshot);
        ThrottledStatus($"AutoMapper: {_state}. {action}");
        return LastReport;
    }

    private string DetectLocation(GameSnapshot snapshot)
    {
        if (Config.RunsTarget > 0 && _currentRun >= Config.RunsTarget)
        {
            Enter(AutoMapperState.Completed, "Run target reached.");
            return "Run target reached.";
        }

        if (IsHideout(snapshot))
        {
            EnterPreparation("Hideout anchors detected.");
            return "Hideout anchors detected.";
        }

        if (IsActiveMap(snapshot))
        {
            Enter(AutoMapperState.ClearMap, "Already in active map; skipping preparation.");
            return "Already in active map; clear loop delegated to combat bot.";
        }

        Enter(AutoMapperState.ReturnToHideout, "Not in map and no hideout anchors detected.");
        return "Need hideout: return key planned.";
    }

    private string ReturnToHideout(GameSnapshot snapshot)
    {
        if (IsHideout(snapshot))
        {
            EnterPreparation("Hideout detected after return.");
            return "Hideout detected.";
        }

        if (!ReadyForAction(Config.ReturnActionCooldownMs))
        {
            return "Waiting after return action.";
        }

        _lastActionUtc = DateTime.UtcNow;
        if (Config.DryRun)
        {
            return $"Dry-run: would press {Config.ReturnToHideoutKey} to return hideout.";
        }

        if (!Config.ActiveInputEnabled)
        {
            Fail("Return to hideout needs active input or dry-run.");
            return _lastError;
        }

        return _inputController.PressKey(Config.ProcessName, Config.ReturnToHideoutKey)
            ? $"Pressed {Config.ReturnToHideoutKey}; waiting for hideout."
            : FailWithInput("Return to hideout key failed");
    }

    private string FindAndClickEntity(GameSnapshot snapshot, string category, AutoMapperState nextState, string label)
    {
        EntityData? entity = Nearest(snapshot, category);
        if (entity is null)
        {
            if (Config.DryRun)
            {
                Enter(nextState, $"Dry-run skipped missing {label} entity.");
                return $"Dry-run: would click {label}, but no decoded {label} entity is visible in this snapshot.";
            }

            Fail($"{label} was not found in memory entities.");
            return _lastError;
        }

        if (!ReadyForAction(Config.EntityInteractCooldownMs))
        {
            return $"Waiting before interacting with {label}.";
        }

        _lastActionUtc = DateTime.UtcNow;
        if (Config.DryRun)
        {
            Enter(nextState, $"Dry-run clicked {label}.");
            return $"Dry-run: would click {label} #{entity.Id}.";
        }

        if (!Config.ActiveInputEnabled)
        {
            Fail($"Clicking {label} needs active input or dry-run.");
            return _lastError;
        }

        if (_inputController.ClickWorldPoint(Config.ProcessName, entity.X, entity.Y, entity.Z, snapshot.CameraMatrix))
        {
            if (category.Equals("MapDevice", StringComparison.OrdinalIgnoreCase))
            {
                _lastMapDeviceX = entity.X;
                _lastMapDeviceY = entity.Y;
                _lastMapDeviceZ = entity.Z;
            }

            Enter(nextState, $"Clicked {label}.");
            return $"Clicked {label} #{entity.Id}.";
        }

        return FailWithInput($"{label} click failed");
    }

    private string TakeSupplies(GameSnapshot snapshot)
    {
        int selectedTabletCount = Config.PreferredTabletNames.Count(name => !string.IsNullOrWhiteSpace(name));
        if (Config.DryRun)
        {
            Enter(AutoMapperState.PrepareWaystone, "Dry-run supplies planned.");
            string alchemy = Config.AlchemyWaystones
                ? $" plus {Config.AlchemyTakeCount} Orb of Alchemy from '{Config.CurrencyStashTabName}'"
                : string.Empty;
            string tablets = selectedTabletCount > 0
                ? string.Join(", ", Config.PreferredTabletNames.Where(name => !string.IsNullOrWhiteSpace(name)))
                : $"{Config.TabletTakeCount} generic tablet(s)";
            return $"Dry-run: would take {Config.WaystoneTakeCount} waystone(s) from '{Config.WaystoneStashTabName}', tablets [{tablets}] from '{Config.TabletStashTabName}'{alchemy}.";
        }

        InventorySnapshot? inventory = snapshot.Inventory;
        if (inventory is null)
        {
            return WaitForInventoryScan(snapshot, "take supplies");
        }

        int requiredCells = Math.Max(1, Config.WaystoneTakeCount) +
                            Math.Max(0, selectedTabletCount == 0 ? Config.TabletTakeCount : selectedTabletCount) +
                            (Config.AlchemyWaystones ? Math.Max(1, Config.AlchemyTakeCount) : 0);
        if (inventory.FreeCellCount < requiredCells)
        {
            Fail($"Not enough free inventory cells for supplies: free={inventory.FreeCellCount}, required={requiredCells}.");
            return _lastError;
        }

        if (!ReadyForAction(Config.StashActionCooldownMs))
        {
            return "Waiting for stash UI.";
        }

        _lastActionUtc = DateTime.UtcNow;
        if (!Config.ActiveInputEnabled)
        {
            Fail("Taking supplies needs active input or dry-run.");
            return _lastError;
        }

        Enter(AutoMapperState.PrepareWaystone, "Supply transfer requested.");
        return Config.AlchemyWaystones
            ? $"Supply transfer requires stash UI calibration; expected Orb of Alchemy from '{Config.CurrencyStashTabName}'."
            : "Supply transfer requires stash UI calibration; continuing to map device.";
    }

    private string WaitForInventoryScan(GameSnapshot snapshot, string purpose)
    {
        TimeSpan elapsed = DateTime.UtcNow - _stateEnteredUtc;
        if (elapsed >= TimeSpan.FromMilliseconds(Config.InventoryScanTimeoutMs))
        {
            Fail($"Inventory scan failed after {elapsed.TotalSeconds:0.0}s while trying to {purpose}. Open inventory once and check Debug inventory diagnostics.");
            return _lastError;
        }

        bool uiAlreadyOpen = FindUiText(snapshot.Ui, "Inventory", "Stash") is not null;
        if (!Config.ActiveInputEnabled)
        {
            return uiAlreadyOpen
                ? $"Waiting for inventory memory scan before {purpose}; UI is visible, active input is off."
                : $"Waiting for inventory memory scan before {purpose}; open inventory/stash manually or enable active input.";
        }

        if (!uiAlreadyOpen && ReadyForAction(Config.InventoryOpenRetryMs))
        {
            _lastActionUtc = DateTime.UtcNow;
            if (_inputController.PressKey(Config.ProcessName, Config.OpenInventoryKey))
            {
                return $"Inventory scan not ready; pressed {Config.OpenInventoryKey} to open inventory for {purpose}.";
            }

            return $"Inventory scan not ready; inventory key failed: {_inputController.LastError}.";
        }

        return uiAlreadyOpen
            ? $"Waiting for inventory memory scan before {purpose}; inventory/stash UI is visible."
            : $"Waiting for inventory memory scan before {purpose}; inventory UI not visible yet.";
    }

    private string OpenAtlas(GameSnapshot snapshot)
    {
        if (!ReadyForAction(Config.AtlasOpenCooldownMs))
        {
            return "Waiting for atlas UI.";
        }

        Enter(AutoMapperState.SelectMap, "Atlas open step complete.");
        return "Atlas open step complete.";
    }

    private string SelectMap(GameSnapshot snapshot)
    {
        AtlasSelection? read = AtlasNodeProvider?.Invoke();
        if (read is not { } atlas || atlas.Nodes.Count == 0)
        {
            Fail("Atlas nodes not read from memory. Open the atlas; check Atlas -> Dump atlas nodes.");
            return _lastError;
        }

        // Reference = the player's current atlas node. Primary source is the in-memory "you are here"
        // marker (pan-independent). The atlas does NOT auto-centre — it opens at the last pan position — so
        // a screen-centre proxy is wrong; when the marker is missing fall back to the node the bot itself
        // last traversed into (that IS the player's current map in the continuous loop).
        (int X, int Y) reference = atlas.Current ?? (_lastAtlasGridX, _lastAtlasGridY);

        // Boss rush routes toward the boss; if it can't pick a step (no boss node, no current node, no
        // progress) fall back to the nearest clean map instead of hard-stopping the whole bot on the atlas.
        AtlasNodeData? pick = IsBossRush
            ? (SelectBossRushRouteStep(atlas, reference, out _) ?? SelectFullClearMap(atlas, reference))
            : SelectFullClearMap(atlas, reference);

        if (pick is null)
        {
            int runnable = atlas.Nodes.Count(node => IsRunnableMapCode(node.MapCode));
            Fail($"No reachable clean map near node ({reference.X},{reference.Y}). {atlas.Nodes.Count} nodes, {runnable} runnable; relax atlas filters/blacklist.");
            return _lastError;
        }

        _selectedMap = string.IsNullOrWhiteSpace(pick.MapName) ? pick.MapCode : pick.MapName;
        _lastAtlasGridX = pick.GridX;
        _lastAtlasGridY = pick.GridY;
        _lastAtlasX = pick.CenterX;
        _lastAtlasY = pick.CenterY;

        if (!ReadyForAction(Config.AtlasClickCooldownMs))
        {
            return $"Selected {_selectedMap} at grid ({pick.GridX},{pick.GridY}); waiting before click.";
        }

        _lastActionUtc = DateTime.UtcNow;
        if (Config.DryRun)
        {
            Enter(AutoMapperState.ActivateMap, $"Dry-run selected {_selectedMap}.");
            return $"Dry-run: would click '{_selectedMap}' [grid {pick.GridX},{pick.GridY}] at screen ({pick.CenterX:0},{pick.CenterY:0}).";
        }

        if (!Config.ActiveInputEnabled)
        {
            Fail("Selecting atlas map needs active input or dry-run.");
            return _lastError;
        }

        if (_inputController.ClickWindowPixelPoint(Config.ProcessName, pick.CenterX, pick.CenterY))
        {
            string clean = pick.Tags.Count == 0 ? "clean" : string.Join("/", pick.Tags);
            Enter(AutoMapperState.ActivateMap, $"Clicked atlas map {_selectedMap}.");
            return $"Clicked '{_selectedMap}' [grid {pick.GridX},{pick.GridY}] at ({pick.CenterX:0},{pick.CenterY:0}) ({clean}).";
        }

        return FailWithInput("Atlas map click failed");
    }

    private bool IsBossRush => Config.StrategyPreset.Equals("BossRush", StringComparison.OrdinalIgnoreCase);

    private AtlasNodeData? SelectFullClearMap(AtlasSelection atlas, (int X, int Y) reference) =>
        BaseRunnableCandidates(atlas, reference)
            .Where(node => !Config.AvoidDecoratedAtlasNodes || node.Tags.Count == 0)
            .OrderBy(node => GridDistanceSq(node.GridX, node.GridY, reference.X, reference.Y))
            .FirstOrDefault();

    private AtlasNodeData? SelectBossRushRouteStep(AtlasSelection atlas, (int X, int Y) reference, out string reason)
    {
        AtlasNodeData? bossTarget = SelectBossInterestTarget(atlas, reference);

        if (bossTarget is null)
        {
            reason = "No Deadly Map Boss / boss-interest atlas node decoded; falling back to nearest clean runnable map.";
            return BaseRunnableCandidates(atlas, reference)
                .Where(node => !IsBossInterestNode(node))
                .Where(node => !Config.AvoidDecoratedAtlasNodes || node.Tags.Count == 0)
                .OrderBy(node => GridDistanceSq(node.GridX, node.GridY, reference.X, reference.Y))
                .FirstOrDefault();
        }

        reason = $"Routing toward boss-interest node '{NodeLabel(bossTarget)}' at grid ({bossTarget.GridX},{bossTarget.GridY}); final boss node is not selected.";
        StatusChanged?.Invoke(this, reason);
        int currentToBoss = GridDistanceSq(reference.X, reference.Y, bossTarget.GridX, bossTarget.GridY);
        return BaseRunnableCandidates(atlas, reference)
            .Where(node => !IsBossInterestNode(node))
            .Where(node => !Config.AvoidDecoratedAtlasNodes || node.Tags.Count == 0)
            .Select(node => new
            {
                Node = node,
                DistanceToBoss = GridDistanceSq(node.GridX, node.GridY, bossTarget.GridX, bossTarget.GridY),
                DistanceFromReference = GridDistanceSq(node.GridX, node.GridY, reference.X, reference.Y),
                Progress = currentToBoss - GridDistanceSq(node.GridX, node.GridY, bossTarget.GridX, bossTarget.GridY)
            })
            .Where(item => item.Progress > 0 || item.DistanceFromReference <= 2)
            .OrderByDescending(item => item.Progress)
            .ThenBy(item => item.DistanceFromReference)
            .ThenBy(item => item.DistanceToBoss)
            .Select(item => item.Node)
            .FirstOrDefault();
    }

    private static AtlasNodeData? SelectBossInterestTarget(AtlasSelection atlas, (int X, int Y) reference)
    {
        AtlasNodeData? primary = atlas.Nodes
            .Where(IsPrimaryBossInterestNode)
            .OrderBy(node => GridDistanceSq(node.GridX, node.GridY, reference.X, reference.Y))
            .FirstOrDefault();
        if (primary is not null)
        {
            return primary;
        }

        return atlas.Nodes
            .Where(IsBossInterestNode)
            .OrderBy(node => GridDistanceSq(node.GridX, node.GridY, reference.X, reference.Y))
            .FirstOrDefault();
    }

    private IEnumerable<AtlasNodeData> BaseRunnableCandidates(AtlasSelection atlas, (int X, int Y) reference)
    {
        List<AtlasNodeData> all = atlas.Nodes
            .Where(node => IsRunnableMapCode(node.MapCode))
            .Where(node => node.Unlocked && !node.Visited && node.Visible)
            .Where(node => IsOnScreen(node, atlas.WinW, atlas.WinH))
            .Where(node => atlas.Current is not { } c || node.GridX != c.X || node.GridY != c.Y)
            .Where(node => !IsMapBlacklisted(node))
            .ToList();

        // You can only traverse to a node CONNECTED to the current one in the atlas graph, so prefer the
        // direct neighbours of the reference node. Fall back to all candidates when the graph or this node's
        // neighbours aren't available, so selection never hard-stops.
        if (atlas.Connections.TryGetValue(reference, out IReadOnlyList<(int X, int Y)>? neighbours) && neighbours.Count > 0)
        {
            HashSet<(int X, int Y)> neighbourSet = [.. neighbours];
            List<AtlasNodeData> adjacent = all.Where(node => neighbourSet.Contains((node.GridX, node.GridY))).ToList();
            if (adjacent.Count > 0)
            {
                return adjacent;
            }
        }

        return all;
    }

    private static bool IsPrimaryBossInterestNode(AtlasNodeData node) =>
        node.Tags.Any(tag => tag.Contains("Deadly Map Boss", StringComparison.OrdinalIgnoreCase)) ||
        node.MapName.Contains("Deadly Map Boss", StringComparison.OrdinalIgnoreCase) ||
        node.MapCode.Contains("UberBoss", StringComparison.OrdinalIgnoreCase);

    private static bool IsBossInterestNode(AtlasNodeData node) =>
        IsPrimaryBossInterestNode(node) ||
        node.Tags.Any(tag => tag.Contains("Boss", StringComparison.OrdinalIgnoreCase) ||
                             tag.Contains("Unique", StringComparison.OrdinalIgnoreCase)) ||
        node.MapCode.Contains("Unique", StringComparison.OrdinalIgnoreCase);

    private static string NodeLabel(AtlasNodeData node) =>
        string.IsNullOrWhiteSpace(node.MapName) ? node.MapCode : node.MapName;

    /// <summary>A runnable endgame map: a "Map…" code that is not a hideout, tower, citadel, boss, unique
    /// or merchant special node (those should never be auto-selected for an AFK clear loop).</summary>
    private static bool IsRunnableMapCode(string code)
    {
        if (!code.StartsWith("Map", StringComparison.Ordinal))
        {
            return false;
        }

        string[] skip = ["Hideout", "Tower", "Citadel", "UberBoss", "Unique", "Merchant", "Campsite", "Wildwood"];
        return !skip.Any(part => code.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsMapBlacklisted(AtlasNodeData node) =>
        Config.MapBlacklist.Any(blocked =>
            !string.IsNullOrWhiteSpace(blocked) &&
            (node.MapName.Contains(blocked, StringComparison.OrdinalIgnoreCase) ||
             node.MapCode.Contains(blocked, StringComparison.OrdinalIgnoreCase)));

    private static bool IsOnScreen(AtlasNodeData node, float winW, float winH)
    {
        const float margin = 24f;
        return node.CenterX >= margin && node.CenterX <= winW - margin &&
               node.CenterY >= margin && node.CenterY <= winH - margin;
    }

    private static int GridDistanceSq(int ax, int ay, int bx, int by) =>
        ((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by));

    private string PrepareWaystone(GameSnapshot snapshot)
    {
        if (Config.DryRun)
        {
            Enter(AutoMapperState.FindMapDevice, "Dry-run waystone preparation planned.");
            string tierPolicy = Config.AllowUnknownWaystoneTier
                ? $"up to T{Config.MaxWaystoneTier}, unknown tiers allowed"
                : $"up to T{Config.MaxWaystoneTier}";
            return $"Dry-run: would inspect waystone via inventory memory/clipboard, require revives {Config.RequiredWaystoneRevives}, allow {tierPolicy}.";
        }

        InventorySnapshot? inventory = snapshot.Inventory;
        if (inventory is null)
        {
            return WaitForInventoryScan(snapshot, "prepare waystone");
        }

        InventoryItemData? waystone = inventory.Items
            .Where(item => IsWaystone(item.Metadata))
            .Where(item => !_skippedWaystones.Contains(item.ItemAddress))
            .OrderBy(item => item.StartY)
            .ThenBy(item => item.StartX)
            .FirstOrDefault();
        if (waystone is null)
        {
            int waystoneCount = inventory.Items.Count(item => IsWaystone(item.Metadata));
            string dump = inventory.Items.Count == 0
                ? "inventory empty"
                : string.Join("; ", inventory.Items
                    .OrderBy(item => item.StartY)
                    .ThenBy(item => item.StartX)
                    .Take(20)
                    .Select(item => $"[{item.StartX},{item.StartY}] r{item.Rarity} {(string.IsNullOrWhiteSpace(item.Metadata) ? "<no-meta>" : item.Metadata)}"));
            string reason = waystoneCount == 0
                ? $"no waystone item in main inventory ({inventory.Items.Count} items)"
                : $"all {waystoneCount} waystone(s) skipped after revives/tier checks";
            Fail($"No usable waystone: {reason}. Items: {dump}");
            return _lastError;
        }

        if (_hoveredWaystoneAddress != waystone.ItemAddress)
        {
            _hoveredWaystoneAddress = waystone.ItemAddress;
            _waystoneHoverUtc = DateTime.UtcNow;
            _waystoneExaltAttempts = 0;
            _alchemyAttempted = false;
            (double x, double y) = InventoryCellCenter(waystone);
            if (Config.DryRun || _inputController.MoveWindowRelativePoint(Config.ProcessName, x, y))
            {
                return $"Hovering waystone [{waystone.StartX},{waystone.StartY}] to read Revives Available.";
            }

            Fail($"Waystone hover failed: {_inputController.LastError}");
            return _lastError;
        }

        if (DateTime.UtcNow - _waystoneHoverUtc < TimeSpan.FromMilliseconds(Config.TooltipReadDelayMs))
        {
            return "Waiting for waystone tooltip.";
        }

        WaystoneInspection inspection = InspectHoveredWaystone(snapshot.Ui);
        if (!inspection.IsWaystone)
        {
            if (DateTime.UtcNow - _waystoneHoverUtc < TimeSpan.FromMilliseconds(Config.TooltipReadTimeoutMs))
            {
                return $"Waiting for waystone clipboard text; got {inspection.Status}.";
            }

            // Self-heal: when the backpack is not open (e.g. stash step skipped) the hover lands on nothing
            // and Ctrl+C reads empty. Open the inventory once and re-hover before giving up.
            if (!_waystoneInventoryOpened && Config.ActiveInputEnabled)
            {
                _waystoneInventoryOpened = true;
                _hoveredWaystoneAddress = 0;
                return _inputController.PressKey(Config.ProcessName, Config.OpenInventoryKey)
                    ? $"Could not read waystone; opened inventory ({Config.OpenInventoryKey}) and retrying."
                    : $"Could not read waystone; inventory open key failed: {_inputController.LastError}.";
            }

            Fail($"Hovered item text is not a waystone ({inspection.Status}). Check inventory grid calibration.");
            return _lastError;
        }

        int? revives = inspection.Revives;
        int? tier = inspection.Tier;
        if (tier is not null && tier.Value > Config.MaxWaystoneTier)
        {
            _skippedWaystones.Add(waystone.ItemAddress);
            _hoveredWaystoneAddress = 0;
            return $"Skipped waystone: tier {tier.Value} > max {Config.MaxWaystoneTier} ({inspection.Source}).";
        }

        if (tier is null && !Config.AllowUnknownWaystoneTier)
        {
            if (DateTime.UtcNow - _waystoneHoverUtc < TimeSpan.FromMilliseconds(Config.TooltipReadTimeoutMs))
            {
                return $"Waiting for waystone tier text ({inspection.Source}).";
            }

            _skippedWaystones.Add(waystone.ItemAddress);
            _hoveredWaystoneAddress = 0;
            return $"Skipped waystone: tier unknown from {inspection.Source} and unknown tiers are disabled.";
        }

        if (revives is null)
        {
            if (DateTime.UtcNow - _waystoneHoverUtc < TimeSpan.FromMilliseconds(Config.TooltipReadTimeoutMs))
            {
                return $"Waiting for Revives Available text ({inspection.Source}).";
            }

            Fail($"Could not read 'Revives Available' from waystone {inspection.Source}.");
            return _lastError;
        }

        if (revives.Value < Config.MinWaystoneRevives)
        {
            _skippedWaystones.Add(waystone.ItemAddress);
            _hoveredWaystoneAddress = 0;
            return $"Skipped waystone: revives {revives.Value} < min {Config.MinWaystoneRevives}.";
        }

        bool rarityUnknown = string.IsNullOrWhiteSpace(inspection.Rarity);
        bool isNormalWaystone = inspection.Rarity.Equals("Normal", StringComparison.OrdinalIgnoreCase) ||
                                (rarityUnknown && waystone.Rarity <= 0);
        if (!_alchemyAttempted && Config.AlchemyWaystones && isNormalWaystone)
        {
            if (TryApplyOrbToWaystone(inventory, waystone, IsOrbOfAlchemy, "Orb of Alchemy", rightClickCurrency: true, out string alchemyAction))
            {
                _alchemyAttempted = true;
                _hoveredWaystoneAddress = 0;
                _lastActionUtc = DateTime.UtcNow;
                return $"{alchemyAction}; re-reading revives.";
            }

            _alchemyAttempted = true;
            if (revives.Value <= Config.RequiredWaystoneRevives)
            {
                return $"{alchemyAction}; revives already {revives.Value}.";
            }

            return $"{alchemyAction}; falling back to Exalted Orb because revives={revives.Value}.";
        }

        if (revives.Value == Config.RequiredWaystoneRevives)
        {
            Enter(AutoMapperState.FindMapDevice, $"Waystone ready: revives {revives.Value}.");
            string tierText = tier is null ? "tier unknown" : $"T{tier.Value}";
            string rarityText = rarityUnknown ? "rarity unknown" : inspection.Rarity;
            return $"Waystone ready: {tierText}, {rarityText}, Revives Available {revives.Value} ({inspection.Source}).";
        }

        if (revives.Value > Config.RequiredWaystoneRevives)
        {
            if (_waystoneExaltAttempts >= Config.MaxExaltedAttemptsPerWaystone)
            {
                _skippedWaystones.Add(waystone.ItemAddress);
                _hoveredWaystoneAddress = 0;
                return $"Skipped waystone: revives stayed {revives.Value} after {_waystoneExaltAttempts} Exalted Orb attempt(s).";
            }

            if (!TryApplyOrbToWaystone(inventory, waystone, IsOrdinaryExaltedOrb, "Exalted Orb", rightClickCurrency: true, out string exaltAction))
            {
                Fail($"{exaltAction}; cannot reduce revives from {revives.Value} to {Config.RequiredWaystoneRevives}.");
                return _lastError;
            }

            _waystoneExaltAttempts++;
            _hoveredWaystoneAddress = 0;
            _lastActionUtc = DateTime.UtcNow;
            return $"{exaltAction}; attempt {_waystoneExaltAttempts}/{Config.MaxExaltedAttemptsPerWaystone}, re-reading revives.";
        }

        _skippedWaystones.Add(waystone.ItemAddress);
        _hoveredWaystoneAddress = 0;
        return $"Skipped waystone: revives {revives.Value} is below required {Config.RequiredWaystoneRevives}.";
    }

    private string ActivateMap(GameSnapshot snapshot)
    {
        if (!ReadyForAction(Config.MapActivationCooldownMs))
        {
            return "Waiting before map activation.";
        }

        _lastActionUtc = DateTime.UtcNow;
        if (Config.DryRun)
        {
            Enter(AutoMapperState.EnterPortal, "Dry-run activated map.");
            string alchemy = Config.AlchemyWaystones ? " after applying Orb of Alchemy to a normal waystone" : string.Empty;
            return $"Dry-run: would insert waystone/tablets{alchemy} and activate '{_selectedMap}'.";
        }

        if (!Config.ActiveInputEnabled)
        {
            Fail("Activating map needs active input or dry-run.");
            return _lastError;
        }

        string alchemyAction = "alchemy disabled";
        if (Config.AlchemyWaystones)
        {
            alchemyAction = "waystone already prepared";
        }

        if (!TryCtrlClickSupplyItems(snapshot.Inventory, out string supplyAction))
        {
            Fail(supplyAction);
            return _lastError;
        }

        (float X, float Y)? traverse = UiButtonLocator?.Invoke(["Traverse", "Activate"]);
        if (traverse is not { } button)
        {
            Fail("Map activation button (Traverse) not found in UI memory. Is the map device open?");
            return _lastError;
        }

        if (_inputController.ClickWindowPixelPoint(Config.ProcessName, button.X, button.Y))
        {
            Enter(AutoMapperState.EnterPortal, "Map activated.");
            return $"Inserted supplies ({supplyAction}; {alchemyAction}) and clicked Traverse at ({button.X:0},{button.Y:0}).";
        }

        return FailWithInput("Map activation click failed");
    }

    private string ClearMap(GameSnapshot snapshot)
    {
        UpdateBossState(snapshot);
        if (Config.DryRun)
        {
            Enter(AutoMapperState.ReturnPortal, "Dry-run clear threshold reached.");
            return $"Dry-run: would run combat/loot loop until monsters <= {Config.FinishMonsterCount}.";
        }

        if (IsHideout(snapshot))
        {
            EnterPreparation("Hideout anchors detected while clear state was active.");
            return "Hideout detected; switching AutoStart back to preparation loop.";
        }

        // Boss rush: the boss dying completes the map — leave immediately, don't clear the rest.
        if (IsBossRush && _bossKilledThisMap)
        {
            Enter(AutoMapperState.ReturnPortal, "Boss rush: boss killed; returning from map.");
            return "Boss rush: boss killed; leaving map.";
        }

        int remaining = snapshot.MapMonsterCount ?? snapshot.Entities.Count(entity => entity.Category == "Monster");
        if (remaining <= Config.FinishMonsterCount)
        {
            if (Config.RequireBossKillBeforeReturn && !_bossKilledThisMap)
            {
                return _bossSeenThisMap
                    ? $"Clear threshold reached ({remaining}), but boss kill is not confirmed yet."
                    : $"Clear threshold reached ({remaining}), but boss was not found yet; keeping combat/explore active.";
            }

            Enter(AutoMapperState.ReturnPortal, $"Map clear threshold reached: {remaining}.");
            return $"Map clear threshold reached: {remaining}.";
        }

        return $"Clearing map: {remaining} monster(s) remain. CombatBot owns movement/fight/loot.";
    }

    private string ReturnPortal(GameSnapshot snapshot)
    {
        if (!ReadyForAction(Config.ReturnActionCooldownMs))
        {
            return "Waiting before return portal action.";
        }

        EntityData? portal = Nearest(snapshot, "Portal");
        _lastActionUtc = DateTime.UtcNow;
        if (Config.DryRun)
        {
            Enter(AutoMapperState.WaitForHideout, "Dry-run returned through portal.");
            return $"Dry-run: would press {Config.PortalKey} and enter portal.";
        }

        if (!Config.ActiveInputEnabled)
        {
            Fail("Return portal needs active input or dry-run.");
            return _lastError;
        }

        if (portal is not null &&
            _inputController.ClickWorldPoint(Config.ProcessName, portal.X, portal.Y, portal.Z, snapshot.CameraMatrix))
        {
            _returnPortalAreaInstance = snapshot.AreaInstanceAddress;
            Enter(AutoMapperState.WaitForHideout, "Clicked existing portal; waiting for hideout load.");
            return "Clicked existing portal; waiting for hideout load.";
        }

        if (_inputController.PressKey(Config.ProcessName, Config.PortalKey))
        {
            return $"Pressed portal key {Config.PortalKey}; waiting for portal entity.";
        }

        return FailWithInput("Portal key failed");
    }

    private string WaitForHideout(GameSnapshot snapshot)
    {
        if (Config.DryRun)
        {
            Enter(AutoMapperState.DumpInventory, "Dry-run hideout loaded.");
            return "Dry-run: hideout load settled.";
        }

        bool loadedDifferentArea = _returnPortalAreaInstance == 0 ||
                                   snapshot.AreaInstanceAddress == 0 ||
                                   snapshot.AreaInstanceAddress != _returnPortalAreaInstance;
        if (IsHideout(snapshot) && loadedDifferentArea)
        {
            _hideoutStableTicks++;
            if (_hideoutStableTicks >= Config.HideoutStableTicks &&
                DateTime.UtcNow - _stateEnteredUtc >= TimeSpan.FromMilliseconds(Config.ZoneSettleMs))
            {
                Enter(AutoMapperState.DumpInventory, "Hideout loaded and stable.");
                return "Hideout loaded and stable; dumping inventory.";
            }

            return $"Hideout detected; settling {_hideoutStableTicks}/{Config.HideoutStableTicks}.";
        }

        _hideoutStableTicks = 0;
        TimeSpan waited = DateTime.UtcNow - _stateEnteredUtc;
        if (waited > TimeSpan.FromMilliseconds(Config.MapLoadTimeoutMs))
        {
            Enter(AutoMapperState.ReturnPortal, "Hideout load did not settle; retrying return portal.");
            return "Hideout load did not settle; retrying return portal.";
        }

        return $"Waiting for hideout load ({waited.TotalSeconds:0.0}s).";
    }

    private string DumpInventory(GameSnapshot snapshot)
    {
        if (Config.DryRun)
        {
            Enter(AutoMapperState.NextRun, "Dry-run dumped inventory.");
            return $"Dry-run: would Ctrl+LMB dump unprotected loot; protected cells {Config.ProtectedInventoryCells.Length}.";
        }

        InventorySnapshot? inventory = snapshot.Inventory;
        if (inventory is null)
        {
            Fail("Inventory scan failed; refusing to dump.");
            return _lastError;
        }

        InventoryProtectedMask mask = new(Config.ProtectedInventoryCells);
        IReadOnlyList<InventoryItemData> dumpItems = inventory.Items
            .Where(item => !inventory.IsProtected(mask, item))
            .Where(item => !IsSupplyItem(item.Metadata))
            .OrderBy(item => item.StartY)
            .ThenBy(item => item.StartX)
            .ToArray();

        if (dumpItems.Count == 0)
        {
            Enter(AutoMapperState.NextRun, "No unprotected loot to dump.");
            return $"No dumpable items. Protected cells: {mask.Count}.";
        }

        if (!ReadyForAction(Config.StashActionCooldownMs))
        {
            return $"Waiting before dump: {dumpItems.Count} item(s) queued.";
        }

        _lastActionUtc = DateTime.UtcNow;
        if (!Config.ActiveInputEnabled)
        {
            Fail("Dumping inventory needs active input or dry-run.");
            return _lastError;
        }

        int dumped = 0;
        foreach (InventoryItemData item in dumpItems.Take(Config.MaxDumpClicksPerPass))
        {
            (double x, double y) = InventoryCellCenter(item);
            if (_inputController.ClickWindowRelativePoint(Config.ProcessName, x, y, ctrl: true))
            {
                dumped++;
                Thread.Sleep(Config.InventoryClickDelayMs);
            }
        }

        Enter(AutoMapperState.NextRun, $"Dumped {dumped}/{dumpItems.Count} item(s).");
        foreach (InventoryItemData item in dumpItems.Take(dumped > 0 ? dumped : dumpItems.Count))
        {
            TrackLoot(item);
        }

        return $"Dumped {dumped}/{dumpItems.Count} item(s), skipped protected {mask.Count} cell(s).";
    }

    private string NextRun()
    {
        _currentRun++;
        if (Config.RunsTarget > 0 && _currentRun >= Config.RunsTarget)
        {
            Enter(AutoMapperState.Completed, $"Completed {_currentRun}/{Config.RunsTarget} run(s).");
            return $"Completed {_currentRun}/{Config.RunsTarget} run(s).";
        }

        EnterPreparation($"Starting run {_currentRun + 1}/{Config.RunsTarget}.");
        return $"Starting next run: {_currentRun + 1}/{Config.RunsTarget}.";
    }

    private bool TryCtrlClickSupplyItems(InventorySnapshot? inventory, out string action)
    {
        action = "Inventory scan failed.";
        if (inventory is null)
        {
            return false;
        }

        InventoryItemData? waystone = _hoveredWaystoneAddress == 0
            ? inventory.Items.FirstOrDefault(item => IsWaystone(item.Metadata))
            : inventory.Items.FirstOrDefault(item => item.ItemAddress == _hoveredWaystoneAddress);
        if (waystone is null)
        {
            action = "No waystone item found in inventory memory.";
            return false;
        }

        List<InventoryItemData> supplies = [waystone];
        List<InventoryItemData> availableTablets = inventory.Items.Where(item => IsTablet(item.Metadata)).ToList();
        string[] selectedTablets = Config.PreferredTabletNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        if (selectedTablets.Length > 0)
        {
            foreach (string tabletName in selectedTablets)
            {
                InventoryItemData? tablet = availableTablets.FirstOrDefault(item => TabletNameMatches(item.Metadata, tabletName));
                if (tablet is null)
                {
                    continue;
                }

                supplies.Add(tablet);
                availableTablets.Remove(tablet);
            }
        }
        else
        {
            supplies.AddRange(availableTablets.Take(Config.TabletTakeCount));
        }
        int clicked = 0;
        foreach (InventoryItemData item in supplies)
        {
            (double x, double y) = InventoryCellCenter(item);
            if (_inputController.ClickWindowRelativePoint(Config.ProcessName, x, y, ctrl: true))
            {
                clicked++;
                Thread.Sleep(Config.InventoryClickDelayMs);
            }
        }

        action = $"Ctrl-clicked {clicked}/{supplies.Count} supply item(s).";
        return clicked > 0;
    }

    private bool TryApplyOrbToWaystone(
        InventorySnapshot inventory,
        InventoryItemData waystone,
        Func<string, bool> currencyPredicate,
        string currencyName,
        bool rightClickCurrency,
        out string action)
    {
        InventoryItemData? currency = inventory.Items.FirstOrDefault(item => currencyPredicate(item.Metadata));
        if (currency is null)
        {
            action = $"{currencyName} not found in inventory. Take it from stash tab '{Config.CurrencyStashTabName}' first.";
            return false;
        }

        if (Config.DryRun)
        {
            action = $"Dry-run: would right-click {currencyName} and apply it to waystone [{waystone.StartX},{waystone.StartY}].";
            return true;
        }

        (double currencyX, double currencyY) = InventoryCellCenter(currency);
        if (!_inputController.ClickWindowRelativePoint(Config.ProcessName, currencyX, currencyY, rightButton: rightClickCurrency))
        {
            action = $"{currencyName} right-click failed: {_inputController.LastError}";
            return false;
        }

        Thread.Sleep(Config.InventoryClickDelayMs);
        (double waystoneX, double waystoneY) = InventoryCellCenter(waystone);
        if (!_inputController.ClickWindowRelativePoint(Config.ProcessName, waystoneX, waystoneY))
        {
            action = $"Waystone target click failed for {currencyName}: {_inputController.LastError}";
            return false;
        }

        action = $"Applied {currencyName} to waystone.";
        return true;
    }

    private UiElementData? FindUiText(UiSnapshot? ui, params string[] texts)
    {
        return ui?.VisibleTextElements.FirstOrDefault(element =>
            texts.Any(text => element.Text.Contains(text, StringComparison.OrdinalIgnoreCase)));
    }


    /// <summary>
    /// Diagnostic: with the inventory open in-game, move the cursor to the first backpack waystone's
    /// calibrated cell and Ctrl+C it, reporting exactly what the clipboard returned. Isolates the
    /// cell->pixel calibration and the copy mechanism from the AutoStart state-machine sequencing.
    /// </summary>
    public string TestWaystoneHover(GameSnapshot? snapshot)
    {
        if (snapshot?.Inventory is not { } inventory)
        {
            return "No inventory snapshot yet. Open the game and wait for the inventory scan.";
        }

        InventoryItemData? waystone = inventory.Items
            .Where(item => IsWaystone(item.Metadata))
            .OrderBy(item => item.StartY)
            .ThenBy(item => item.StartX)
            .FirstOrDefault();
        if (waystone is null)
        {
            return $"No waystone in backpack ({inventory.Items.Count} items). Put a waystone in the inventory first.";
        }

        (double relX, double relY) = InventoryCellCenter(waystone);
        if (!_inputController.MoveWindowRelativePoint(Config.ProcessName, relX, relY))
        {
            return $"Hover move failed: {_inputController.LastError}";
        }

        Thread.Sleep(Math.Clamp(Config.TooltipReadDelayMs, 120, 600));
        string cell = $"[{waystone.StartX},{waystone.StartY}] rel=({relX:0.###},{relY:0.###})";
        if (!_inputController.CopyHoveredText(Config.ProcessName, out string copied, 220))
        {
            return $"Hovered {cell} but Ctrl+C failed: {_inputController.LastError}";
        }

        string firstLine = copied.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
        bool isWaystone = copied.Contains("Waystone", StringComparison.OrdinalIgnoreCase) ||
                          copied.Contains("Item Class: Waystones", StringComparison.OrdinalIgnoreCase);
        return $"Hovered {cell}. Clipboard {(isWaystone ? "OK (waystone)" : "got")}: \"{firstLine}\" ({copied.Length} chars).";
    }

    private (double X, double Y) InventoryCellCenter(InventoryItemData item)
    {
        double centerCellX = item.StartX + (item.Width / 2.0);
        double centerCellY = item.StartY + (item.Height / 2.0);
        double x = Config.InventoryGridLeft + ((centerCellX / Math.Max(1, Config.InventoryGridColumns)) * Config.InventoryGridWidth);
        double y = Config.InventoryGridTop + ((centerCellY / Math.Max(1, Config.InventoryGridRows)) * Config.InventoryGridHeight);
        return (x, y);
    }

    private static bool HasEntity(GameSnapshot snapshot, string category) =>
        snapshot.Entities.Any(entity => entity.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

    private static bool IsHideout(GameSnapshot snapshot) =>
        HasEntity(snapshot, "Stash") || HasEntity(snapshot, "MapDevice") || HasEntity(snapshot, "Waypoint");

    private static EntityData? Nearest(GameSnapshot snapshot, string category)
    {
        if (snapshot.Player is not { HasPosition: true } player)
        {
            return snapshot.Entities.FirstOrDefault(entity => entity.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        }

        return snapshot.Entities
            .Where(entity => entity.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entity => Distance(player.X, player.Y, entity.X, entity.Y))
            .FirstOrDefault();
    }

    private static bool IsActiveMap(GameSnapshot snapshot)
    {
        if (IsHideout(snapshot))
        {
            return false;
        }

        if (snapshot.MapMonsterCount is > 0)
        {
            return true;
        }

        return snapshot.Entities.Any(entity =>
            entity.Category.Equals("Monster", StringComparison.OrdinalIgnoreCase) ||
            entity.Category.Equals("Encounter", StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateBossState(GameSnapshot snapshot)
    {
        foreach (EntityData entity in snapshot.Entities.Where(IsMapBoss))
        {
            _bossSeenThisMap = true;

            // Confirm a kill ONLY after the boss was first observed alive — bosses read Health<=0 transiently
            // (intro/ES phase/misread), which previously caused a false kill and an early return to hideout.
            if (entity.HasLife && entity.MaxHealth > 0 && entity.Health > 0)
            {
                _bossSeenAliveThisMap = true;
            }
            else if (_bossSeenAliveThisMap && entity.HasLife && entity.MaxHealth > 0 && entity.Health <= 0)
            {
                _bossKilledThisMap = true;
            }
        }
    }

    private static bool IsBossMonster(EntityData entity)
    {
        if (!entity.Category.Equals("Monster", StringComparison.OrdinalIgnoreCase) ||
            IsIgnoredTargetMetadata(entity.Metadata))
        {
            return false;
        }

        return entity.Rarity == 3 ||
               entity.Metadata.Contains("Boss", StringComparison.OrdinalIgnoreCase) ||
               entity.Metadata.Contains("MapBoss", StringComparison.OrdinalIgnoreCase) ||
               entity.Metadata.Contains("Unique", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The actual MAP BOSS (for map completion), not just any unique — a Rogue Exile is unique but
    /// not the boss, so killing it must not end the map. Requires boss metadata.</summary>
    private static bool IsMapBoss(EntityData entity) =>
        entity.Category.Equals("Monster", StringComparison.OrdinalIgnoreCase) &&
        !IsIgnoredTargetMetadata(entity.Metadata) &&
        (entity.Metadata.Contains("MapBoss", StringComparison.OrdinalIgnoreCase) ||
         entity.Metadata.Contains("Boss", StringComparison.OrdinalIgnoreCase));

    private static bool IsIgnoredTargetMetadata(string metadata) =>
        metadata.Contains("Expedition", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Azmeri", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Azmiri", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupplyItem(string metadata) => IsWaystone(metadata) || IsTablet(metadata) || IsOrbOfAlchemy(metadata);

    private static bool IsWaystone(string metadata) =>
        metadata.Contains("Waystone", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Map", StringComparison.OrdinalIgnoreCase);

    private bool IsAllowedWaystoneTier(InventoryItemData item)
    {
        int? tier = TryParseTier(item.Metadata);
        return tier is null
            ? Config.AllowUnknownWaystoneTier
            : tier.Value <= Config.MaxWaystoneTier;
    }

    private static bool IsTablet(string metadata) =>
        metadata.Contains("Tablet", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Precursor", StringComparison.OrdinalIgnoreCase);

    private static bool TabletNameMatches(string metadata, string selectedName)
    {
        string normalizedMetadata = NormalizeName(metadata);
        string normalizedSelected = NormalizeName(selectedName);
        return normalizedMetadata.Contains(normalizedSelected, StringComparison.OrdinalIgnoreCase) ||
               normalizedSelected.Contains(normalizedMetadata, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOrbOfAlchemy(string metadata) =>
        metadata.Contains("OrbOfAlchemy", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Orb of Alchemy", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("Alchemy", StringComparison.OrdinalIgnoreCase);

    private static bool IsOrdinaryExaltedOrb(string metadata)
    {
        if (metadata.Contains("Greater", StringComparison.OrdinalIgnoreCase) ||
            metadata.Contains("Perfect", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return metadata.Contains("ExaltedOrb", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("Exalted Orb", StringComparison.OrdinalIgnoreCase) ||
               metadata.Contains("CurrencyAddModToRare", StringComparison.OrdinalIgnoreCase);
    }

    private WaystoneInspection InspectHoveredWaystone(UiSnapshot? ui)
    {
        if (!Config.DryRun && Config.ActiveInputEnabled)
        {
            int copyWaitMs = Math.Clamp(Config.TooltipReadDelayMs / 2, 80, 250);
            if (_inputController.CopyHoveredText(Config.ProcessName, out string copiedText, copyWaitMs))
            {
                WaystoneInspection clipboard = ParseWaystoneClipboard(copiedText);
                if (clipboard.IsWaystone)
                {
                    return clipboard;
                }

                WaystoneInspection fallback = InspectWaystoneUi(ui, "ui tooltip fallback");
                return fallback.HasAnySignal ? fallback : clipboard;
            }

            WaystoneInspection uiFallback = InspectWaystoneUi(ui, "ui tooltip fallback");
            return uiFallback.HasAnySignal
                ? uiFallback with { Status = _inputController.LastError }
                : new WaystoneInspection(false, null, null, string.Empty, "clipboard", _inputController.LastError);
        }

        return InspectWaystoneUi(ui, "ui tooltip");
    }

    private static WaystoneInspection InspectWaystoneUi(UiSnapshot? ui, string source)
    {
        return new WaystoneInspection(
            true,
            TryReadWaystoneTier(ui),
            TryReadWaystoneRevives(ui),
            string.Empty,
            source,
            source);
    }

    private static WaystoneInspection ParseWaystoneClipboard(string text)
    {
        string status = FirstNonEmptyLine(text);
        bool isWaystone =
            text.Contains("Item Class: Waystones", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Waystone (Tier", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Can be used in a Map Device", StringComparison.OrdinalIgnoreCase);

        return new WaystoneInspection(
            isWaystone,
            TryParseClipboardWaystoneTier(text),
            TryParseRevives(text),
            TryParseClipboardRarity(text),
            "clipboard",
            string.IsNullOrWhiteSpace(status) ? "empty clipboard" : status);
    }

    private static int? TryReadWaystoneRevives(UiSnapshot? ui)
    {
        if (ui is null)
        {
            return null;
        }

        foreach (UiElementData element in ui.VisibleTextElements)
        {
            int? revives = TryParseRevives(element.Text);
            if (revives is not null)
            {
                return revives;
            }
        }

        return null;
    }

    private static int? TryReadWaystoneTier(UiSnapshot? ui)
    {
        if (ui is null)
        {
            return null;
        }

        foreach (UiElementData element in ui.VisibleTextElements)
        {
            int? tier = TryParseTier(element.Text);
            if (tier is not null)
            {
                return tier;
            }
        }

        return null;
    }

    private static int? TryParseRevives(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match match = RevivesRegex.Match(text);
        return match.Success && int.TryParse(match.Groups["count"].Value, out int revives)
            ? revives
            : null;
    }

    private static int? TryParseClipboardWaystoneTier(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match match = WaystoneClipboardTierRegex.Match(text);
        if (!match.Success || !int.TryParse(match.Groups["tier"].Value, out int tier) || tier is < 1 or > 16)
        {
            return null;
        }

        return tier;
    }

    private static int? TryParseTier(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        Match match = TierRegex.Match(text);
        if (!match.Success || !int.TryParse(match.Groups["tier"].Value, out int tier) || tier is < 1 or > 16)
        {
            return null;
        }

        return tier;
    }

    private static string TryParseClipboardRarity(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        Match match = RarityRegex.Match(text);
        return match.Success ? match.Groups["rarity"].Value.Trim() : string.Empty;
    }

    private static string FirstNonEmptyLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "empty clipboard";
        }

        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return "empty clipboard";
    }

    private static string NormalizeName(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    private void TrackLoot(InventoryItemData item)
    {
        string bucket = LootBucket(item);
        _lootCounts[bucket] = _lootCounts.TryGetValue(bucket, out int count) ? count + 1 : 1;
    }

    private static string LootBucket(InventoryItemData item)
    {
        return LootClassification.Bucket(item.Metadata, item.Rarity);
    }

    private bool ReadyForAction(int cooldownMs) =>
        DateTime.UtcNow - _lastActionUtc >= TimeSpan.FromMilliseconds(Math.Max(0, cooldownMs));

    private string EnterPortal(GameSnapshot snapshot)
    {
        // Phase 2: portal already clicked — wait for the zone to actually change before handing off to
        // ClearMap, so ClearMap does not bounce back to hideout prep during the map load.
        if (_portalClicked)
        {
            if (snapshot.AreaInstanceAddress != 0 &&
                snapshot.AreaInstanceAddress != _portalAreaInstance &&
                !IsHideout(snapshot))
            {
                Enter(AutoMapperState.WaitForMap, "Area changed after portal; waiting for map load to settle.");
                return "Area changed after portal; waiting for map load to settle.";
            }

            TimeSpan sinceClick = DateTime.UtcNow - _lastActionUtc;
            if (sinceClick > TimeSpan.FromMilliseconds(Config.PortalClickRetryMs) &&
                _portalClickAttempts < Config.PortalClickMaxAttempts)
            {
                _portalClicked = false;
                return $"Portal click #{_portalClickAttempts} did not load after {sinceClick.TotalSeconds:0.0}s; retrying another portal/click point.";
            }

            if (DateTime.UtcNow - _lastActionUtc > TimeSpan.FromMilliseconds(Config.MapLoadTimeoutMs))
            {
                _portalClicked = false;
                return "Map did not load after portal click; retrying portal.";
            }

            return $"Entering map; waiting for zone load ({(DateTime.UtcNow - _lastActionUtc).TotalSeconds:0.0}s).";
        }

        // Phase 1: the portal spawns a few seconds after activation — wait for it instead of failing fast.
        IReadOnlyList<EntityData> portals = PortalCandidates(snapshot);
        EntityData? portal = portals.Count == 0
            ? null
            : portals[Math.Min(_portalClickAttempts, portals.Count - 1)];
        if (portal is null)
        {
            TimeSpan waited = DateTime.UtcNow - _stateEnteredUtc;
            if (waited < TimeSpan.FromMilliseconds(Config.PortalWaitTimeoutMs))
            {
                // One-shot diagnostic: if the portal still isn't categorized after a few seconds, log the
                // nearest non-monster entities + metadata so we can see what the map portal actually is.
                if (!_portalDiagnosed && waited > TimeSpan.FromSeconds(6))
                {
                    _portalDiagnosed = true;
                    string near = string.Join("; ", snapshot.Entities
                        .Where(entity => entity.Category is not "Monster" and not "Player")
                        .OrderBy(entity => snapshot.Player is { HasPosition: true } p ? Distance(p.X, p.Y, entity.X, entity.Y) : 0)
                        .Take(10)
                        .Select(entity => $"{entity.Category}:{entity.Metadata}"));
                    return $"Portal not detected at {waited.TotalSeconds:0}s. Nearby: {near}";
                }

                return $"Waiting for portal to open ({waited.TotalSeconds:0.0}s).";
            }

            Fail("Portal did not appear within the wait window after map activation.");
            return _lastError;
        }

        // Even once the portal entity exists, it needs a moment after map activation to become interactable.
        // Clicking too early just bounces (the player isn't teleported), which was causing the rapid retry
        // cycle. Give it a fixed initial settle before the first click attempt.
        TimeSpan sinceEnter = DateTime.UtcNow - _stateEnteredUtc;
        if (_portalClickAttempts == 0 && sinceEnter < TimeSpan.FromMilliseconds(Config.PortalInitialWaitMs))
        {
            return $"Portal found; letting it stabilise ({sinceEnter.TotalSeconds:0.0}/{Config.PortalInitialWaitMs / 1000.0:0.0}s).";
        }

        if (!ReadyForAction(Config.EntityInteractCooldownMs))
        {
            return "Portal found; waiting before clicking.";
        }

        _lastActionUtc = DateTime.UtcNow;
        if (Config.DryRun)
        {
            Enter(AutoMapperState.WaitForMap, "Dry-run entered portal.");
            return "Dry-run: would click portal and enter the map.";
        }

        if (!Config.ActiveInputEnabled)
        {
            Fail("Entering portal needs active input or dry-run.");
            return _lastError;
        }

        (double offsetX, double offsetY) = PortalClickOffset(_portalClickAttempts);
        if (_inputController.ClickWorldPoint(Config.ProcessName, portal.X, portal.Y, portal.Z, snapshot.CameraMatrix, offsetX, offsetY))
        {
            _portalClicked = true;
            _portalAreaInstance = snapshot.AreaInstanceAddress;
            _portalClickAttempts++;
            return $"Clicked portal #{portal.Id} attempt {_portalClickAttempts}/{Config.PortalClickMaxAttempts} offset ({offsetX:0},{offsetY:0}); waiting for the map to load.";
        }

        return FailWithInput("Portal click failed");
    }

    private string WaitForMap(GameSnapshot snapshot)
    {
        if (Config.DryRun)
        {
            Enter(AutoMapperState.ClearMap, "Dry-run map loaded.");
            return "Dry-run: map load settled.";
        }

        TimeSpan waited = DateTime.UtcNow - _stateEnteredUtc;
        bool loadedDifferentArea = _portalAreaInstance == 0 ||
                                   snapshot.AreaInstanceAddress == 0 ||
                                   snapshot.AreaInstanceAddress != _portalAreaInstance;
        bool mapReady = loadedDifferentArea && IsActiveMap(snapshot);
        int settleMs = Math.Max(5000, Config.MapSettleMs);
        if (mapReady)
        {
            if (_mapReadySinceUtc == DateTime.MinValue)
            {
                _mapReadySinceUtc = DateTime.UtcNow;
                return $"Map detected; settling for {settleMs / 1000.0:0.0}s before control handoff.";
            }

            TimeSpan readyFor = DateTime.UtcNow - _mapReadySinceUtc;
            if (readyFor >= TimeSpan.FromMilliseconds(settleMs))
            {
                Enter(AutoMapperState.ClearMap, "Map loaded and stable.");
                return "Map loaded and stable; starting clear loop.";
            }

            return $"Map detected; settling ({readyFor.TotalSeconds:0.0}/{settleMs / 1000.0:0.0}s).";
        }
        else
        {
            _mapReadySinceUtc = DateTime.MinValue;
        }

        if (waited > TimeSpan.FromMilliseconds(Config.MapLoadTimeoutMs))
        {
            _portalClicked = false;
            Enter(AutoMapperState.EnterPortal, "Map load did not settle; retrying portal entry.");
            return "Map load did not settle; retrying portal entry.";
        }

        return IsHideout(snapshot)
            ? $"Waiting for map load; still seeing hideout anchors ({waited.TotalSeconds:0.0}s)."
            : $"Waiting for map monsters/entities to settle ({waited.TotalSeconds:0.0}s).";
    }

    private IReadOnlyList<EntityData> PortalCandidates(GameSnapshot snapshot)
    {
        float originX = _lastMapDeviceX != 0 || _lastMapDeviceY != 0
            ? _lastMapDeviceX
            : snapshot.Player?.X ?? 0;
        float originY = _lastMapDeviceX != 0 || _lastMapDeviceY != 0
            ? _lastMapDeviceY
            : snapshot.Player?.Y ?? 0;

        return snapshot.Entities
            .Where(entity => entity.Category.Equals("Portal", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entity => Distance(originX, originY, entity.X, entity.Y))
            .Take(Math.Max(1, Config.PortalClickMaxAttempts))
            .ToArray();
    }

    private static (double X, double Y) PortalClickOffset(int attempt)
    {
        return (attempt % 6) switch
        {
            1 => (0, -38),
            2 => (0, 38),
            3 => (-34, 0),
            4 => (34, 0),
            5 => (0, -72),
            _ => (0, 0)
        };
    }

    /// <summary>Enter the preparation phase: the stash (only when supply-taking from stash is enabled),
    /// otherwise straight to waystone prep using whatever supplies are already in the backpack.</summary>
    private void EnterPreparation(string reason) =>
        Enter(Config.TakeSuppliesFromStash ? AutoMapperState.FindStash : AutoMapperState.PrepareWaystone, reason);

    private void Enter(AutoMapperState state, string reason)
    {
        if (state == AutoMapperState.EnterPortal)
        {
            _portalClicked = false;
            _portalAreaInstance = 0;
            _portalDiagnosed = false;
            _portalClickAttempts = 0;
            _mapReadySinceUtc = DateTime.MinValue;
            _bossSeenThisMap = false;
            _bossSeenAliveThisMap = false;
            _bossKilledThisMap = false;
        }

        if (state == AutoMapperState.WaitForMap)
        {
            _mapReadySinceUtc = DateTime.MinValue;
        }

        if (state == AutoMapperState.WaitForHideout)
        {
            _hideoutStableTicks = 0;
        }

        if (state == AutoMapperState.PrepareWaystone)
        {
            _waystoneInventoryOpened = false;
        }

        _state = state;
        _stateEnteredUtc = DateTime.UtcNow;
        LastReport = BuildReport(reason);
        StatusChanged?.Invoke(this, reason);
    }

    private string Fail(string reason)
    {
        _lastError = reason;
        Enter(AutoMapperState.Failed, reason);
        return reason;
    }

    private string FailWithInput(string prefix) => Fail($"{prefix}: {_inputController.LastError}");

    private AutoMapperReport BuildReport(string action, GameSnapshot? snapshot = null)
    {
        InventorySnapshot? inventory = snapshot?.Inventory;
        int mobsLeft = snapshot is null
            ? 0
            : IsHideout(snapshot)
                ? 0
                : snapshot.MapMonsterCount ?? snapshot.Entities.Count(entity => entity.Category.Equals("Monster", StringComparison.OrdinalIgnoreCase));
        return new AutoMapperReport(
            _state.ToString(),
            _currentRun,
            Config.RunsTarget,
            _selectedMap,
            mobsLeft,
            inventory?.FreeCellCount ?? 0,
            Config.ProtectedInventoryCells.Length,
            DateTime.UtcNow - _stateEnteredUtc,
            action,
            _state == AutoMapperState.Failed ? _lastError : string.Empty,
            Config.DryRun,
            new Dictionary<string, int>(_lootCounts, StringComparer.OrdinalIgnoreCase));
    }

    private void ThrottledStatus(string message)
    {
        if (DateTime.UtcNow - _lastStatusUtc < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastStatusUtc = DateTime.UtcNow;
        StatusChanged?.Invoke(this, message);
    }

    private static double Distance(float x0, float y0, float x1, float y1)
    {
        double dx = x1 - x0;
        double dy = y1 - y0;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private readonly record struct WaystoneInspection(
        bool IsWaystone,
        int? Tier,
        int? Revives,
        string Rarity,
        string Source,
        string Status)
    {
        public bool HasAnySignal => Tier is not null || Revives is not null;
    }
}

public enum AutoMapperState
{
    Idle,
    DetectLocation,
    ReturnToHideout,
    FindStash,
    TakeSupplies,
    FindMapDevice,
    OpenAtlas,
    SelectMap,
    PrepareWaystone,
    ActivateMap,
    EnterPortal,
    WaitForMap,
    ClearMap,
    ReturnPortal,
    WaitForHideout,
    DumpInventory,
    NextRun,
    Completed,
    Failed
}

public sealed class AutoMapperConfig
{
    public string ProcessName { get; set; } = "PathOfExileSteam";
    public string StrategyPreset { get; set; } = "FullMapClear";
    public bool Enabled { get; set; }
    public bool ActiveInputEnabled { get; set; }
    public bool DryRun { get; set; }
    public int RunsTarget { get; set; } = 1;
    public int CurrentRun { get; set; }
    public int FinishMonsterCount { get; set; } = 10;
    public bool RequireBossKillBeforeReturn { get; set; } = true;
    public string ReturnToHideoutKey { get; set; } = "F5";
    public string OpenInventoryKey { get; set; } = "I";
    public string PortalKey { get; set; } = "T";
    public string WaystoneStashTabName { get; set; } = "maps";
    public string TabletStashTabName { get; set; } = "maps";
    public string CurrencyStashTabName { get; set; } = "валюта";
    public int WaystoneTakeCount { get; set; } = 1;
    public int MaxWaystoneTier { get; set; } = 16;
    public bool AllowUnknownWaystoneTier { get; set; }
    public int TabletTakeCount { get; set; }
    public string[] PreferredTabletNames { get; set; } = [];
    public bool TakeSuppliesFromStash { get; set; }
    public bool AlchemyWaystones { get; set; } = true;
    public int AlchemyTakeCount { get; set; } = 1;
    public int RequiredWaystoneRevives { get; set; } = 2;
    public int MinWaystoneRevives { get; set; } = 2;
    public int MaxExaltedAttemptsPerWaystone { get; set; } = 5;
    public int TooltipReadDelayMs { get; set; } = 250;
    public int TooltipReadTimeoutMs { get; set; } = 1600;
    public int InventoryScanTimeoutMs { get; set; } = 8500;
    public int InventoryOpenRetryMs { get; set; } = 1600;
    public string[] MapBlacklist { get; set; } = [];
    public bool AvoidDecoratedAtlasNodes { get; set; } = true;
    public double AtlasDecorationRadiusPixels { get; set; } = 82;
    public string[] DecoratedAtlasKeywords { get; set; } = ["Boss", "Unique", "Corrupted", "Breach", "Ritual", "Expedition", "Delirium"];
    public int[] ProtectedInventoryCells { get; set; } = [];
    public float LastAtlasNodeX { get; set; }
    public float LastAtlasNodeY { get; set; }
    public double InventoryGridLeft { get; set; } = 0.03;
    public double InventoryGridTop { get; set; } = 0.655;
    public double InventoryGridWidth { get; set; } = 0.945;
    public double InventoryGridHeight { get; set; } = 0.288;
    public int InventoryGridColumns { get; set; } = 12;
    public int InventoryGridRows { get; set; } = 5;
    public int InventoryClickDelayMs { get; set; } = 85;
    public int MaxDumpClicksPerPass { get; set; } = 60;
    public int EntityInteractCooldownMs { get; set; } = 900;
    public int StashActionCooldownMs { get; set; } = 1200;
    public int AtlasOpenCooldownMs { get; set; } = 1200;
    public int AtlasClickCooldownMs { get; set; } = 900;
    public int MapActivationCooldownMs { get; set; } = 1200;
    public int PortalWaitTimeoutMs { get; set; } = 20000;
    public int PortalInitialWaitMs { get; set; } = 5000;
    public int PortalClickRetryMs { get; set; } = 7000;
    public int PortalClickMaxAttempts { get; set; } = 8;
    public int MapLoadTimeoutMs { get; set; } = 30000;
    public int MapSettleMs { get; set; } = 5000;
    public int ZoneSettleMs { get; set; } = 2500;
    public int HideoutStableTicks { get; set; } = 3;
    public int ReturnActionCooldownMs { get; set; } = 1800;
}

/// <summary>Live atlas read passed to <see cref="AutoMapper.AtlasNodeProvider"/>: the memory-decoded map
/// nodes, the player's current node grid coord, and the game window size (for on-screen filtering).</summary>
public sealed record AtlasSelection(
    IReadOnlyList<AtlasNodeData> Nodes,
    (int X, int Y)? Current,
    float WinW,
    float WinH,
    IReadOnlyDictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>> Connections);

public sealed record AutoMapperReport(
    string State,
    int CurrentRun,
    int TargetRuns,
    string SelectedMap,
    int MobsLeft,
    int InventoryFreeCells,
    int ProtectedCells,
    TimeSpan StateTime,
    string LastAction,
    string Error,
    bool DryRun,
    IReadOnlyDictionary<string, int> LootCounts)
{
    public static AutoMapperReport Idle { get; } = new("Idle", 0, 0, "n/a", 0, 0, 0, TimeSpan.Zero, "n/a", string.Empty, true, new Dictionary<string, int>());
}
