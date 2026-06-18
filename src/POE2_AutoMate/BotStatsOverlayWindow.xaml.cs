using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using POE2_AutoMate.Automation;

namespace POE2_AutoMate;

public partial class BotStatsOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    public BotStatsOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void SetHotkey(string hotkey)
    {
        HotkeyText.Text = string.IsNullOrWhiteSpace(hotkey) ? "hotkey n/a" : hotkey;
    }

    public void UpdateCombat(CombatReport report)
    {
        if (StateText.Text is "Idle" or "Waiting" or "Combat Monitor")
        {
            StateText.Text = report.State;
        }

        VitalsText.Text = report.HasVitals
            ? $"HP {report.HealthPercent}% / MP {report.ManaPercent}%"
            : "HP -- / MP --";
        ThreatText.Text = $"Mobs {report.RemainingMonsterCount} / near {report.NearbyMonsterCount} / route {report.RouteWaypointCount}";
        ActionText.Text = report.LastAction;
    }

    public void UpdateMap(MapRunnerReport report)
    {
        if (StateText.Text is "Idle" or "Waiting")
        {
            StateText.Text = report.State;
        }

        ThreatText.Text = $"Map mobs {report.MonsterCount} / chests {report.ChestCount}";
    }

    public void UpdateAutoMapper(AutoMapperReport report, LootValueSummary lootSummary)
    {
        StateText.Text = report.State;
        RunText.Text = $"Run {report.CurrentRun}/{report.TargetRuns} / {report.SelectedMap}";
        ThreatText.Text = $"Mobs left {report.MobsLeft}";
        InventoryText.Text = $"Inv free {report.InventoryFreeCells} / locked {report.ProtectedCells}";
        LootText.Text = $"Loot {lootSummary.DivineEquivalent:0.0000} div / {CompactLoot(report.LootCounts)}";
        ActionText.Text = report.LastAction;
        ErrorText.Text = report.Error;
    }

    private static string CompactLoot(IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(3)
            .Select(pair => $"{pair.Key}:{pair.Value}"));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
}
