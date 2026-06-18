using POE2_AutoMate.Core.Game;

namespace POE2_AutoMate.Automation;

public sealed class MapRunner
{
    private readonly GameInputController _inputController = new();
    private long _currentArea;
    private DateTime _areaEnteredUtc = DateTime.MinValue;
    private DateTime _lastStatusUtc = DateTime.MinValue;
    private DateTime _lastInteractUtc = DateTime.MinValue;

    public MapRunnerConfig Config { get; private set; } = new();
    public bool IsRunning { get; private set; }
    public MapRunnerReport LastReport { get; private set; } = MapRunnerReport.Idle;
    public event EventHandler<string>? StatusChanged;

    public void UpdateConfig(MapRunnerConfig config)
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
        LastReport = MapRunnerReport.Waiting;
        StatusChanged?.Invoke(this, "Map runner monitor started. Waiting for live snapshots.");
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        LastReport = MapRunnerReport.Idle;
        StatusChanged?.Invoke(this, "Map runner monitor stopped.");
    }

    public MapRunnerReport UpdateSnapshot(GameSnapshot? snapshot)
    {
        if (!IsRunning)
        {
            return LastReport;
        }

        if (snapshot is null)
        {
            LastReport = MapRunnerReport.Waiting;
            ThrottledStatus("Map runner waiting for resolved game state.");
            return LastReport;
        }

        if (_currentArea != snapshot.AreaInstanceAddress)
        {
            _currentArea = snapshot.AreaInstanceAddress;
            _areaEnteredUtc = DateTime.UtcNow;
            StatusChanged?.Invoke(this, $"Map runner entered area 0x{_currentArea:X}.");
        }

        int monsters = 0;
        int chests = 0;
        int transitions = 0;
        EntityData? nearestMonster = null;
        EntityData? nearestChest = null;
        EntityData? nearestTransition = null;
        double nearestMonsterDistance = double.MaxValue;
        double nearestChestDistance = double.MaxValue;
        double nearestTransitionDistance = double.MaxValue;

        foreach (EntityData entity in snapshot.Entities)
        {
            double distance = snapshot.Player is { HasPosition: true } player
                ? Distance(player, entity)
                : double.MaxValue;

            switch (entity.Category)
            {
                case "Monster":
                    monsters++;
                    if (distance < nearestMonsterDistance)
                    {
                        nearestMonster = entity;
                        nearestMonsterDistance = distance;
                    }

                    break;
                case "Chest":
                    chests++;
                    if (distance < nearestChestDistance)
                    {
                        nearestChest = entity;
                        nearestChestDistance = distance;
                    }

                    break;
                case "Transition":
                    transitions++;
                    if (distance < nearestTransitionDistance)
                    {
                        nearestTransition = entity;
                        nearestTransitionDistance = distance;
                    }

                    break;
            }
        }

        string objective = PickObjective(nearestTransition, nearestTransitionDistance, nearestChest, nearestChestDistance, nearestMonster, nearestMonsterDistance);
        string action = TryRunActiveAction(nearestTransition, nearestTransitionDistance, nearestChest, nearestChestDistance);
        TimeSpan areaTime = _areaEnteredUtc == DateTime.MinValue ? TimeSpan.Zero : DateTime.UtcNow - _areaEnteredUtc;
        LastReport = new MapRunnerReport(
            "In Area",
            snapshot.AreaInstanceAddress,
            areaTime,
            snapshot.Terrain is not null,
            monsters,
            chests,
            transitions,
            objective,
            action);

        ThrottledStatus($"Map runner: {monsters} monster(s), {chests} chest(s), {transitions} transition(s). {objective}");
        return LastReport;
    }

    private string TryRunActiveAction(EntityData? transition, double transitionDistance, EntityData? chest, double chestDistance)
    {
        if (!Config.ActiveInputEnabled)
        {
            return "Active input disabled.";
        }

        if (DateTime.UtcNow - _lastInteractUtc < TimeSpan.FromMilliseconds(Config.InteractCooldownMs))
        {
            return "Active input cooling down.";
        }

        bool shouldInteract = transition is not null && transitionDistance <= Config.InteractRange ||
                              chest is not null && chestDistance <= Config.InteractRange;
        if (!shouldInteract)
        {
            return "No interact target in range.";
        }

        _lastInteractUtc = DateTime.UtcNow;
        if (_inputController.PressKey(Config.ProcessName, Config.InteractKey))
        {
            string target = transition is not null && transitionDistance <= Config.InteractRange ? "transition" : "chest";
            StatusChanged?.Invoke(this, $"Map runner pressed {Config.InteractKey} for nearby {target}.");
            return $"Pressed {Config.InteractKey}.";
        }

        StatusChanged?.Invoke(this, $"Map runner input failed: {_inputController.LastError}");
        return $"Input failed: {_inputController.LastError}";
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

    private static string PickObjective(
        EntityData? transition,
        double transitionDistance,
        EntityData? chest,
        double chestDistance,
        EntityData? monster,
        double monsterDistance)
    {
        if (transition is not null && transitionDistance < 1800)
        {
            return $"Nearest transition {transitionDistance:0} world units away.";
        }

        if (chest is not null && chestDistance < 1200)
        {
            return $"Nearby chest {chestDistance:0} world units away.";
        }

        if (monster is not null)
        {
            return $"Nearest monster pack {monsterDistance:0} world units away.";
        }

        return "No immediate objective found.";
    }

    private static double Distance(PlayerData player, EntityData entity)
    {
        double dx = entity.X - player.X;
        double dy = entity.Y - player.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}

public sealed record MapRunnerReport(
    string State,
    long AreaInstanceAddress,
    TimeSpan AreaTime,
    bool HasTerrain,
    int MonsterCount,
    int ChestCount,
    int TransitionCount,
    string Objective,
    string LastAction)
{
    public static MapRunnerReport Idle { get; } = new("Idle", 0, TimeSpan.Zero, false, 0, 0, 0, "Monitor disabled.", "n/a");
    public static MapRunnerReport Waiting { get; } = new("Waiting", 0, TimeSpan.Zero, false, 0, 0, 0, "Waiting for live game data.", "n/a");
}

public sealed class MapRunnerConfig
{
    public string ProcessName { get; set; } = "PathOfExileSteam";
    public bool ActiveInputEnabled { get; set; }
    public string InteractKey { get; set; } = "F";
    public double InteractRange { get; set; } = 260;
    public int InteractCooldownMs { get; set; } = 1200;
}
