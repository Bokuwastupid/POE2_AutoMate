using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Interop;
using POE2_AutoMate.Automation;
using POE2_AutoMate.Core.Game;
using POE2_AutoMate.Core.Memory;
using POE2_AutoMate.Services;

namespace POE2_AutoMate;

public partial class MainWindow : Window
{
    private readonly MemoryReader _memoryReader = new();
    private readonly SettingsService _settingsService = new();
    private readonly MapRunner _mapRunner;
    private readonly CombatBot _combatBot;
    private readonly AutoMapper _autoMapper;
    private readonly LootValueTracker _lootValueTracker = new();
    private readonly SkillLoadoutReader _skillLoadoutReader = new();
    private readonly GameInputController _reviveInputController = new();
    private readonly GameReader _gameReader = new();
    private bool _atlasDumpedThisRun;
    private CancellationTokenSource? _liveDataCts;
    private DateTime _lastSnapshotLogUtc = DateTime.MinValue;
    private DateTime _lastFullLiveSnapshotUtc = DateTime.MinValue;
    private DateTime _lastCombatUiUpdateUtc = DateTime.MinValue;
    private DateTime _lastMapMonsterCounterRefreshUtc = DateTime.MinValue;
    private long _lastKnownMapMonsterArea;
    private int? _lastKnownMapMonsterCount;
    private string _lastKnownMapMonsterCountText = string.Empty;
    private DateTime _lastReviveAttemptUtc = DateTime.MinValue;
    private DateTime _lastOverlayPositionUtc = DateTime.MinValue;
    private DateTime _lastAutoConnectLogUtc = DateTime.MinValue;
    private DateTime _lastLootValueRefreshUtc = DateTime.MinValue;
    private readonly DispatcherTimer _autoConnectTimer;
    private OverlayWindow? _overlayWindow;
    private BotStatsOverlayWindow? _botStatsOverlayWindow;
    private AppSettings _settings;
    private bool _applyingSettingsToUi;
    private bool _combatStartedByAutoMapper;
    private HwndSource? _hotkeySource;
    private SkillLoadoutSnapshot? _lastSkillLoadout;
    private const int BotToggleHotkeyId = 0xB07;
    private const int WmHotkey = 0x0312;
    private const int FullLiveTickMs = 250;
    private const int CombatLiveTickMs = 60;
    private static readonly TimeSpan CombatFullSnapshotInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CombatUiUpdateInterval = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan CombatMonsterCounterInterval = TimeSpan.FromMilliseconds(500);
    private static readonly string[] TabletChoices =
    [
        "",
        "Abyss Tablet",
        "Breach Tablet",
        "Delirium Tablet",
        "Expedition Tablet",
        "Irradiated Tablet",
        "Temple Tablet",
        "Overseer Tablet",
        "Ritual Tablet",
        "Clear Skies Delirium Tablet",
        "Cruel Hegemony Overseer Tablet",
        "Forgotten By Time Expedition Tablet",
        "Freedom of Faith Ritual Tablet",
        "Mastered Domain Irradiated Tablet",
        "Season of the Hunt Overseer Tablet",
        "The Grand Project Irradiated Tablet",
        "Unforeseen Consequences Abyss Tablet",
        "Visions of Paradise Irradiated Tablet",
        "Wraeclast Besieged Breach Tablet"
    ];

    public MainWindow()
    {
        InitializeComponent();
        InitializeTabletChoices();

        _settings = _settingsService.LoadOrCreate();
        DisableAutomationOnStartup();
        _mapRunner = new MapRunner();
        _mapRunner.UpdateConfig(ToMapRunnerConfig(_settings));
        _combatBot = new CombatBot(ToCombatConfig(_settings));
        _combatBot.LootLabelLocator = LocateLootLabel;
        _autoMapper = new AutoMapper();
        _autoMapper.UpdateConfig(ToAutoMapperConfig(_settings));
        _autoMapper.AtlasNodeProvider = ReadAtlasSelection;
        _autoMapper.UiButtonLocator = LocateUiButton;

        _mapRunner.StatusChanged += (_, message) => Dispatcher.InvokeAsync(() => Log(message));
        _combatBot.StatusChanged += (_, message) => Dispatcher.InvokeAsync(() => Log(message));
        _autoMapper.StatusChanged += (_, message) => Dispatcher.InvokeAsync(() => Log(message));
        _autoConnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autoConnectTimer.Tick += (_, _) => AutoConnectTick();

        ApplySettingsToUi();
        BuildProtectedInventoryGrid();
        AppLog.Write("system", $"Log files: daily={AppLog.DailyLogPath}; latest={AppLog.LatestLogPath}");
        Log("Application started.");
        Loaded += (_, _) => _autoConnectTimer.Start();
        SourceInitialized += MainWindowSourceInitialized;
        Closed += (_, _) => UnregisterBotHotkey();
    }

    private void DisableAutomationOnStartup()
    {
        bool changed = _settings.MapRunnerEnabled ||
                       _settings.CombatEnabled ||
                       _settings.AutoMapperEnabled ||
                       _settings.MapRunnerActiveInputEnabled ||
                       _settings.CombatActiveInputEnabled ||
                       _settings.AutoMapperActiveInputEnabled;

        _settings.MapRunnerEnabled = false;
        _settings.CombatEnabled = false;
        _settings.AutoMapperEnabled = false;
        _settings.MapRunnerActiveInputEnabled = false;
        _settings.CombatActiveInputEnabled = false;
        _settings.AutoMapperActiveInputEnabled = false;

        if (changed)
        {
            _settingsService.Save(_settings);
            AppLog.Write("safety", "Disabled persisted automation/input flags on startup.");
        }
    }

    private void ApplySettingsToUi()
    {
        _applyingSettingsToUi = true;
        ProcessNameBox.Text = _settings.ProcessName;
        SidebarProcessText.Text = _settings.ProcessName;
        BotToggleHotkeyBox.Text = _settings.BotToggleHotkey;
        _settings.AutoMapperDryRun = false;
        SelectMappingPreset(_settings.MappingPreset);
        EnableAutoMapper.IsChecked = _settings.AutoMapperEnabled;
        AutoMapperActiveInputBox.IsChecked = _settings.AutoMapperActiveInputEnabled;
        AutoMapperDryRunBox.IsChecked = false;
        RequireBossKillBox.IsChecked = _settings.RequireBossKillBeforeReturn;
        AutoMapperRunsTargetBox.Text = _settings.AutoMapperRunsTarget.ToString();
        WaystoneTabBox.Text = _settings.WaystoneStashTabName;
        TabletTabBox.Text = _settings.TabletStashTabName;
        CurrencyTabBox.Text = _settings.CurrencyStashTabName;
        WaystoneTakeCountBox.Text = _settings.WaystoneTakeCount.ToString();
        MaxWaystoneTierBox.Text = _settings.MaxWaystoneTier.ToString();
        AllowUnknownWaystoneTierBox.IsChecked = _settings.AllowUnknownWaystoneTier;
        TabletTakeCountBox.Text = _settings.TabletTakeCount.ToString();
        SelectTabletSlots(_settings.PreferredTabletNames);
        AlchemyWaystonesBox.IsChecked = _settings.AlchemyWaystones;
        AlchemyTakeCountBox.Text = _settings.AlchemyTakeCount.ToString();
        ReturnToHideoutKeyBox.Text = _settings.ReturnToHideoutKey;
        OpenInventoryKeyBox.Text = _settings.OpenInventoryKey;
        PortalKeyBox.Text = _settings.PortalKey;
        AtlasBlacklistBox.Text = string.Join(Environment.NewLine, _settings.AtlasMapBlacklist);
        AvoidDecoratedAtlasNodesBox.IsChecked = _settings.AvoidDecoratedAtlasNodes;
        AtlasDecorationRadiusBox.Text = _settings.AtlasDecorationRadiusPixels.ToString("0", CultureInfo.InvariantCulture);
        InventoryGridLeftBox.Text = _settings.InventoryGridLeft.ToString("0.###", CultureInfo.InvariantCulture);
        InventoryGridTopBox.Text = _settings.InventoryGridTop.ToString("0.###", CultureInfo.InvariantCulture);
        InventoryGridWidthBox.Text = _settings.InventoryGridWidth.ToString("0.###", CultureInfo.InvariantCulture);
        InventoryGridHeightBox.Text = _settings.InventoryGridHeight.ToString("0.###", CultureInfo.InvariantCulture);
        AttackSkillKeyBox.Text = _settings.AttackSkillKey;
        HealthFlaskKeysBox.Text = string.Join(",", _settings.HealthFlaskKeys.Length == 0 ? _settings.FlaskKeys : _settings.HealthFlaskKeys);
        ManaFlaskKeysBox.Text = string.Join(",", _settings.ManaFlaskKeys);
        MinHealthBox.Text = _settings.MinHealthPercent.ToString();
        MinManaBox.Text = _settings.MinManaPercent.ToString();
        CombatKitingBox.IsChecked = _settings.CombatKitingEnabled;
        CombatKiteThresholdBox.Text = _settings.CombatKiteEffectiveHealthPercent.ToString();
        CombatKiteCooldownBox.Text = _settings.CombatKiteCooldownMs.ToString();
        CombatDodgeKeyBox.Text = _settings.CombatDodgeKey;
        MapRunnerActiveInputBox.IsChecked = _settings.MapRunnerActiveInputEnabled;
        MapRunnerInteractKeyBox.Text = _settings.MapRunnerInteractKey;
        MapRunnerInteractRangeBox.Text = _settings.MapRunnerInteractRange.ToString("0");
        MapRunnerInteractCooldownBox.Text = _settings.MapRunnerInteractCooldownMs.ToString();
        CombatActiveInputBox.IsChecked = _settings.CombatActiveInputEnabled;
        CombatAutoMoveBox.IsChecked = _settings.CombatAutoMoveEnabled;
        CombatAutoAttackBox.IsChecked = _settings.CombatAutoAttackEnabled;
        CombatTargetEncountersBox.IsChecked = _settings.CombatTargetEncounters;
        CombatPressAllAbilitiesBox.IsChecked = _settings.CombatPressAllAbilities;
        CombatAutoLootBox.IsChecked = _settings.CombatAutoLootEnabled;
        LootCurrencyBox.IsChecked = _settings.CombatLootCurrencyEnabled;
        LootGearBox.IsChecked = _settings.CombatLootGearEnabled;
        LootOtherBox.IsChecked = _settings.CombatLootOtherEnabled;
        CombatExploreWhenNoTargetBox.IsChecked = _settings.CombatExploreWhenNoTarget;
        CombatDebugOverlayBox.IsChecked = _settings.CombatDebugOverlayEnabled;
        CombatProjectMovementBox.IsChecked = _settings.CombatProjectMovementToScreen;
        CombatInvertInputXBox.IsChecked = _settings.CombatInvertInputX;
        CombatInvertInputYBox.IsChecked = _settings.CombatInvertInputY;
        SelectMovementMode(_settings.CombatMovementMode);
        CombatAbilityKeysBox.Text = string.Join(",", _settings.CombatAbilityKeys);
        CombatRareComboKeysBox.Text = string.Join(",", _settings.CombatRareComboKeys);
        CombatUniqueComboKeysBox.Text = string.Join(",", _settings.CombatUniqueComboKeys);
        CombatUniqueComboRepeatBox.IsChecked = _settings.CombatUniqueComboRepeatEnabled;
        CombatLootKeyBox.Text = _settings.CombatLootKey;
        CombatEncounterInteractKeyBox.Text = _settings.CombatEncounterInteractKey;
        CombatLootRangeBox.Text = _settings.CombatLootRange.ToString("0");
        CombatLootCooldownBox.Text = _settings.CombatLootCooldownMs.ToString();
        CombatLootLabelXOffsetBox.Text = _settings.CombatLootLabelXOffsetPixels.ToString("0");
        CombatLootLabelOffsetBox.Text = _settings.CombatLootLabelYOffsetPixels.ToString("0");
        CombatLootLabelSearchBox.Text = _settings.CombatLootLabelSearchPixels.ToString("0");
        CombatLootSafeMonsterRadiusBox.Text = _settings.CombatLootSafeMonsterRadius.ToString("0");
        CombatLootMaxAttemptsBox.Text = _settings.CombatLootMaxAttemptsPerItem.ToString();
        CombatEncounterRangeBox.Text = _settings.CombatEncounterInteractRange.ToString("0");
        CombatEncounterEnterRangeBox.Text = _settings.CombatEncounterEnterRange.ToString("0");
        CombatEssenceClickCountBox.Text = _settings.CombatEssenceClickCount.ToString();
        CombatExploreStepBox.Text = _settings.CombatExploreStepWorldUnits.ToString("0");
        CombatExploreDirectionMsBox.Text = _settings.CombatExploreDirectionMs.ToString();
        CombatMoveUpKeyBox.Text = _settings.CombatMoveUpKey;
        CombatMoveLeftKeyBox.Text = _settings.CombatMoveLeftKey;
        CombatMoveDownKeyBox.Text = _settings.CombatMoveDownKey;
        CombatMoveRightKeyBox.Text = _settings.CombatMoveRightKey;
        CombatRangeBox.Text = _settings.CombatRange.ToString("0");
        CombatCastRangeBox.Text = _settings.CombatCastRange.ToString("0");
        CombatFinishMonsterCountBox.Text = _settings.CombatFinishMonsterCount.ToString();
        CombatMoveClickPixelsBox.Text = _settings.CombatMoveClickPixels.ToString("0");
        CombatWasdHoldMsBox.Text = _settings.CombatWasdHoldMs.ToString();
        CombatNearbyRadiusBox.Text = _settings.CombatNearbyMonsterRadius.ToString("0");
        CombatDangerCountBox.Text = _settings.CombatDangerMonsterCount.ToString();
        CombatAbilityCooldownBox.Text = _settings.CombatAbilityCooldownMs.ToString();
        CombatAbilityDelayBox.Text = _settings.CombatAbilityDelayMs.ToString();
        CombatComboCooldownBox.Text = _settings.CombatComboCooldownMs.ToString();
        CombatUniqueComboRecastBox.Text = _settings.CombatUniqueComboRecastMs.ToString();
        CombatTargetClickCooldownBox.Text = _settings.CombatTargetClickCooldownMs.ToString();
        CombatFlaskCooldownBox.Text = _settings.CombatFlaskCooldownMs.ToString();
        EnableMapRunner.IsChecked = _settings.MapRunnerEnabled;
        EnableCombat.IsChecked = _settings.CombatEnabled;
        SettingsPathBox.Text = _settingsService.SettingsPath;
        OverlayRotate180Box.IsChecked = _settings.OverlayRotate180;
        OverlayFollowGameBox.IsChecked = _settings.OverlayFollowGameWindow;
        OverlayFullMapBox.IsChecked = _settings.OverlayFullMapView;
        OverlayWidthBox.Text = _settings.OverlayWidth.ToString("0");
        OverlayHeightBox.Text = _settings.OverlayHeight.ToString("0");
        OverlayScaleBox.Text = _settings.OverlayWorldUnitsPerPixel.ToString("0.##");
        OverlayOpacityBox.Text = _settings.OverlayOpacityPercent.ToString("0");
        OverlayShowTerrainBox.IsChecked = _settings.OverlayShowTerrain;
        OverlayShowMonstersBox.IsChecked = _settings.OverlayShowMonsters;
        OverlayShowChestsBox.IsChecked = _settings.OverlayShowChests;
        OverlayShowTransitionsBox.IsChecked = _settings.OverlayShowTransitions;
        OverlayShowNpcsBox.IsChecked = _settings.OverlayShowNpcs;
        OverlayShowOtherBox.IsChecked = _settings.OverlayShowOther;
        UpdateOverlayScaleText();
        _applyingSettingsToUi = false;
        RefreshProtectedInventoryGrid();
    }

    private void Log(string message, bool memory = false)
    {
        AppLog.Write(memory ? "memory" : "ui", message);
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        TextBoxAppend(memory ? MemoryLogBox : LogBox, line);
    }

    private static void TextBoxAppend(System.Windows.Controls.TextBox textBox, string line)
    {
        textBox.AppendText(line);
        textBox.ScrollToEnd();
    }

    private void HeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button)
        {
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void StopBotClick(object sender, RoutedEventArgs e)
    {
        StopAllBots("Emergency stop: map runner and combat bot stopped.");
    }

    private void StopAllBots(string message)
    {
        _mapRunner.Stop();
        _combatBot.Stop();
        _autoMapper.Stop();
        _combatStartedByAutoMapper = false;
        _settings.MapRunnerEnabled = false;
        _settings.CombatEnabled = false;
        _settings.AutoMapperEnabled = false;
        _settings.MapRunnerActiveInputEnabled = false;
        _settings.CombatActiveInputEnabled = false;
        _settings.AutoMapperActiveInputEnabled = false;
        _settingsService.Save(_settings);

        EnableMapRunner.IsChecked = false;
        EnableCombat.IsChecked = false;
        EnableAutoMapper.IsChecked = false;
        MapRunnerActiveInputBox.IsChecked = false;
        CombatActiveInputBox.IsChecked = false;
        AutoMapperActiveInputBox.IsChecked = false;
        UpdateAutoMapperUi(_autoMapper.LastReport);
        UpdateMapRunnerUi(_mapRunner.LastReport);
        UpdateCombatUi(_combatBot.LastReport);
        UpdateBotState();
        Log(message);
    }

    private void ToggleBotHotkey()
    {
        if (_autoMapper.IsRunning ||
            _combatBot.IsRunning ||
            _mapRunner.IsRunning ||
            _settings.AutoMapperActiveInputEnabled ||
            _settings.CombatActiveInputEnabled ||
            _settings.MapRunnerActiveInputEnabled)
        {
            StopAllBots($"Bot stopped by {_settings.BotToggleHotkey}.");
            return;
        }

        _mapRunner.Stop();
        _combatBot.Stop();
        _combatStartedByAutoMapper = false;
        _settings.MapRunnerEnabled = false;
        _settings.MapRunnerActiveInputEnabled = false;
        _settings.CombatEnabled = false;
        _settings.CombatActiveInputEnabled = false;
        _settings.AutoMapperEnabled = true;
        _settings.AutoMapperActiveInputEnabled = true;
        _settings.AutoMapperDryRun = false;
        _settingsService.Save(_settings);

        SetCheckBoxSilently(EnableMapRunner, false);
        SetCheckBoxSilently(MapRunnerActiveInputBox, false);
        SetCheckBoxSilently(EnableCombat, false);
        SetCheckBoxSilently(CombatActiveInputBox, false);
        SetCheckBoxSilently(EnableAutoMapper, true);
        SetCheckBoxSilently(AutoMapperActiveInputBox, true);
        SetCheckBoxSilently(AutoMapperDryRunBox, false);

        _mapRunner.UpdateConfig(ToMapRunnerConfig(_settings));
        _combatBot.UpdateConfig(ToCombatConfig(_settings));
        _autoMapper.UpdateConfig(ToAutoMapperConfig(_settings));
        _atlasDumpedThisRun = false;
        _autoMapper.Start();
        UpdateMapRunnerUi(_mapRunner.LastReport);
        UpdateCombatUi(_combatBot.LastReport);
        UpdateAutoMapperUi(_autoMapper.LastReport);
        UpdateBotState();
        EnsureBotStatsOverlay();
        Log($"AutoStart cleanup loop started by {_settings.BotToggleHotkey}.");
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        _mapRunner.Stop();
        _combatBot.Stop();
        _autoMapper.Stop();
        StopLiveDataLoop();
        _autoConnectTimer.Stop();
        CloseBotStatsOverlay();
        CloseOverlay();
        _gameReader.Dispose();
        _memoryReader.Dispose();
        Application.Current.Shutdown();
    }

    private void MainWindowSourceInitialized(object? sender, EventArgs e)
    {
        _hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hotkeySource?.AddHook(HotkeyWndProc);
        RegisterBotHotkey();
    }

    private nint HotkeyWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == BotToggleHotkeyId)
        {
            ToggleBotHotkey();
            handled = true;
        }

        return nint.Zero;
    }

    private void RegisterBotHotkey()
    {
        if (_hotkeySource is null)
        {
            return;
        }

        nint hwnd = _hotkeySource.Handle;
        UnregisterHotKey(hwnd, BotToggleHotkeyId);
        if (!TryHotkeyTokenToVirtualKey(_settings.BotToggleHotkey, out int virtualKey))
        {
            Log($"Bot hotkey '{_settings.BotToggleHotkey}' is not supported.");
            return;
        }

        if (!RegisterHotKey(hwnd, BotToggleHotkeyId, 0, virtualKey))
        {
            Log($"Could not register bot hotkey {_settings.BotToggleHotkey}. It may already be used.");
            return;
        }

        _botStatsOverlayWindow?.SetHotkey(_settings.BotToggleHotkey);
        Log($"Bot hotkey registered: {_settings.BotToggleHotkey}.");
    }

    private void UnregisterBotHotkey()
    {
        if (_hotkeySource is null)
        {
            return;
        }

        UnregisterHotKey(_hotkeySource.Handle, BotToggleHotkeyId);
        _hotkeySource.RemoveHook(HotkeyWndProc);
        _hotkeySource = null;
    }

    private void ShowDashboard(object sender, RoutedEventArgs e) => ShowOnly(DashboardView);
    private void ShowDev(object sender, RoutedEventArgs e) => ShowOnly(DevView);
    private void ShowMapPreview(object sender, RoutedEventArgs e) => ShowOnly(MapPreviewView);
    private void ShowAutoStart(object sender, RoutedEventArgs e) => ShowOnly(AutoStartView);
    private void ShowMapRunner(object sender, RoutedEventArgs e) => ShowOnly(MapRunnerView);
    private void ShowInventory(object sender, RoutedEventArgs e) => ShowOnly(InventoryView);
    private void ShowCombat(object sender, RoutedEventArgs e) => ShowOnly(CombatView);
    private void ShowLoot(object sender, RoutedEventArgs e) => ShowOnly(LootView);
    private void ShowAtlas(object sender, RoutedEventArgs e) => ShowOnly(AtlasView);
    private void ShowDebug(object sender, RoutedEventArgs e) => ShowOnly(DebugView);
    private void ShowSettings(object sender, RoutedEventArgs e) => ShowOnly(SettingsView);

    private void ShowOnly(UIElement view)
    {
        DashboardView.Visibility = Visibility.Collapsed;
        DevView.Visibility = Visibility.Collapsed;
        MapPreviewView.Visibility = Visibility.Collapsed;
        AutoStartView.Visibility = Visibility.Collapsed;
        MapRunnerView.Visibility = Visibility.Collapsed;
        InventoryView.Visibility = Visibility.Collapsed;
        CombatView.Visibility = Visibility.Collapsed;
        LootView.Visibility = Visibility.Collapsed;
        AtlasView.Visibility = Visibility.Collapsed;
        DebugView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        view.Visibility = Visibility.Visible;
    }

    private void AttachProcessClick(object sender, RoutedEventArgs e)
    {
        string processName = ProcessNameBox.Text.Trim();
        if (_memoryReader.Attach(processName))
        {
            StatusGame.Text = "Detected";
            StatusGame.Foreground = FindBrush("SuccessBrush");
            StatusMemory.Text = "Connected";
            StatusMemory.Foreground = FindBrush("SuccessBrush");
            SidebarStatusText.Text = $"Connected ({_memoryReader.ProcessId})";
            SidebarStatusText.Foreground = FindBrush("SuccessBrush");
            SidebarProcessText.Text = processName;

            Log($"Attached to {processName} ({_memoryReader.ProcessId}).");
            Log("Memory reader active.", true);
            return;
        }

        Log($"Attach failed: {_memoryReader.LastError}");
        Log($"Attach failed: {_memoryReader.LastError}", true);
        MessageBox.Show(_memoryReader.LastError, "Attach failed", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void DetachProcessClick(object sender, RoutedEventArgs e)
    {
        _mapRunner.Stop();
        _autoMapper.Stop();
        StopLiveDataLoop();
        CloseOverlay();
        _gameReader.Disconnect();
        _memoryReader.Detach();
        ResetLiveDataUi();
        UpdateDisconnectedState();
        Log("Detached from process.");
    }

    private void ScanPatternsClick(object sender, RoutedEventArgs e)
    {
        string processName = ProcessNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(processName))
        {
            Log("Pattern scan skipped: process name is empty.", true);
            return;
        }

        _settings.ProcessName = processName;
        _settingsService.Save(_settings);
        _mapRunner.UpdateConfig(ToMapRunnerConfig(_settings));
        _combatBot.UpdateConfig(ToCombatConfig(_settings));
        _autoMapper.UpdateConfig(ToAutoMapperConfig(_settings));

        if (!_gameReader.Connect(processName))
        {
            StatusGame.Text = "Not Detected";
            StatusGame.Foreground = FindBrush("DangerBrush");
            StatusMemory.Text = "Scan Failed";
            StatusMemory.Foreground = FindBrush("DangerBrush");
            SidebarStatusText.Text = "Disconnected";
            SidebarStatusText.Foreground = FindBrush("DangerBrush");
            Log($"Pattern scan attach failed: {_gameReader.LastError}", true);
            return;
        }

        StatusGame.Text = "Detected";
        StatusGame.Foreground = FindBrush("SuccessBrush");
        SidebarProcessText.Text = _gameReader.ConnectedProcessName;
        SidebarStatusText.Text = $"Connected ({_gameReader.ConnectedProcessId})";
        SidebarStatusText.Foreground = FindBrush("SuccessBrush");

        Log($"AOB scanner attached to {_gameReader.ConnectedProcessName} ({_gameReader.ConnectedProcessId}).", true);
        Log($"Module base=0x{_gameReader.ModuleBaseAddress:X}, size={_gameReader.ModuleSize:N0} bytes.", true);

        PatternScanReport report = _gameReader.ScanPatterns();
        if (report.HasError)
        {
            StatusMemory.Text = "Scan Failed";
            StatusMemory.Foreground = FindBrush("DangerBrush");
            Log(report.Error, true);
            return;
        }

        foreach (PatternScanHit hit in report.Hits)
        {
            string status = hit.IsFound ? "FOUND" : "MISS";
            string slot = hit.IsFound ? $"slot=0x{hit.SlotAddress:X}" : "slot=n/a";
            string target = hit.TargetAddress != 0 ? $"target=0x{hit.TargetAddress:X}" : "target=n/a";
            Log($"{status}: {hit.Description}; {slot}; {target}", true);
        }

        StatusMemory.Text = report.FoundCount > 0
            ? $"Patterns {report.FoundCount}/{report.TotalCount}"
            : "No Patterns";
        StatusMemory.Foreground = report.FoundCount > 0
            ? FindBrush("SuccessBrush")
            : FindBrush("DangerBrush");

        Log($"Pattern scan complete: {report.FoundCount}/{report.TotalCount} found.", false);

        if (report.FoundCount > 0)
        {
            StartLiveDataLoop();
        }
    }

    private void AutoConnectTick()
    {
        if (_gameReader.IsConnected)
        {
            _autoConnectTimer.Stop();
            return;
        }

        string processName = ProcessNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(processName))
        {
            processName = _settings.ProcessName;
        }

        if (!_gameReader.Connect(processName))
        {
            if (DateTime.UtcNow - _lastAutoConnectLogUtc >= TimeSpan.FromSeconds(30))
            {
                _lastAutoConnectLogUtc = DateTime.UtcNow;
                AppLog.Write("auto-connect", $"waiting for {processName}: {_gameReader.LastError}");
            }

            return;
        }

        _autoConnectTimer.Stop();
        _settings.ProcessName = _gameReader.ConnectedProcessName;
        _settingsService.Save(_settings);
        _mapRunner.UpdateConfig(ToMapRunnerConfig(_settings));
        _combatBot.UpdateConfig(ToCombatConfig(_settings));
        _autoMapper.UpdateConfig(ToAutoMapperConfig(_settings));

        StatusGame.Text = "Detected";
        StatusGame.Foreground = FindBrush("SuccessBrush");
        SidebarProcessText.Text = _gameReader.ConnectedProcessName;
        SidebarStatusText.Text = $"Connected ({_gameReader.ConnectedProcessId})";
        SidebarStatusText.Foreground = FindBrush("SuccessBrush");

        Log($"Auto-attached to {_gameReader.ConnectedProcessName} ({_gameReader.ConnectedProcessId}).", true);
        Log($"Module base=0x{_gameReader.ModuleBaseAddress:X}, size={_gameReader.ModuleSize:N0} bytes.", true);

        PatternScanReport report = _gameReader.ScanPatterns();
        foreach (PatternScanHit hit in report.Hits)
        {
            string status = hit.IsFound ? "FOUND" : "MISS";
            string slot = hit.IsFound ? $"slot=0x{hit.SlotAddress:X}" : "slot=n/a";
            string target = hit.TargetAddress != 0 ? $"target=0x{hit.TargetAddress:X}" : "target=n/a";
            Log($"{status}: {hit.Description}; {slot}; {target}", true);
        }

        StatusMemory.Text = report.FoundCount > 0
            ? $"Patterns {report.FoundCount}/{report.TotalCount}"
            : "No Patterns";
        StatusMemory.Foreground = report.FoundCount > 0
            ? FindBrush("SuccessBrush")
            : FindBrush("DangerBrush");

        if (report.FoundCount > 0)
        {
            StartLiveDataLoop();
        }
    }

    private void StartLiveDataLoop()
    {
        StopLiveDataLoop();

        _liveDataCts = new CancellationTokenSource();
        CancellationToken token = _liveDataCts.Token;
        _lastFullLiveSnapshotUtc = DateTime.MinValue;
        _lastCombatUiUpdateUtc = DateTime.MinValue;
        _lastMapMonsterCounterRefreshUtc = DateTime.MinValue;
        _lastKnownMapMonsterArea = 0;
        _lastKnownMapMonsterCount = null;
        _lastKnownMapMonsterCountText = string.Empty;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    DateTime now = DateTime.UtcNow;
                    bool combatLive = IsCombatLiveLoopActive();
                    bool readFullSnapshot = !combatLive;
                    bool refreshMonsterCounter = combatLive &&
                                                 !readFullSnapshot &&
                                                 now - _lastMapMonsterCounterRefreshUtc >= CombatMonsterCounterInterval;

                    GameSnapshot? snapshot = readFullSnapshot
                        ? _gameReader.ReadSnapshot()
                        : _gameReader.ReadCombatSnapshot(includeMapMonsterCounter: refreshMonsterCounter);

                    if (readFullSnapshot)
                    {
                        _lastFullLiveSnapshotUtc = now;
                    }

                    if (refreshMonsterCounter)
                    {
                        _lastMapMonsterCounterRefreshUtc = now;
                    }

                    snapshot = ApplyCachedMonsterCounter(snapshot, allowCached: combatLive);
                    LogLiveSnapshot(snapshot, combatLive && !readFullSnapshot);
                    if (TryHandleDeath(snapshot))
                    {
                        await Dispatcher.InvokeAsync(() => UpdateCombatLiveUi(snapshot, _autoMapper.LastReport, _combatBot.LastReport, includeHeavyUi: readFullSnapshot));
                        await Task.Delay(combatLive ? CombatLiveTickMs : FullLiveTickMs, token);
                        continue;
                    }

                    if (!combatLive)
                    {
                        await Dispatcher.InvokeAsync(() => UpdateLiveDataUi(snapshot));
                        await Task.Delay(FullLiveTickMs, token);
                        continue;
                    }

                    AutoMapperReport? autoReport = _autoMapper.IsRunning
                        ? _autoMapper.UpdateSnapshot(snapshot)
                        : null;
                    CombatReport? combatReport = _combatBot.IsRunning
                        ? _combatBot.UpdateSnapshot(snapshot)
                        : null;

                    bool updateUi = readFullSnapshot ||
                                    now - _lastCombatUiUpdateUtc >= CombatUiUpdateInterval;
                    if (updateUi)
                    {
                        _lastCombatUiUpdateUtc = now;
                        await Dispatcher.InvokeAsync(() => UpdateCombatLiveUi(snapshot, autoReport, combatReport, includeHeavyUi: readFullSnapshot));
                    }

                    await Task.Delay(CombatLiveTickMs, token);
                }
            }
            catch (OperationCanceledException)
            {
                AppLog.Write("snapshot", "live data loop cancelled");
            }
            catch (Exception ex)
            {
                AppLog.WriteException("snapshot", ex);
                await Dispatcher.InvokeAsync(() => Log($"Live data loop failed: {ex.Message}", true));
            }
        }, token);

        Log("Live data loop started.", true);
    }

    private bool IsCombatLiveLoopActive()
    {
        if (_combatBot.IsRunning)
        {
            return true;
        }

        return _autoMapper.IsRunning &&
               _settings.AutoMapperActiveInputEnabled &&
               !_settings.AutoMapperDryRun &&
               _autoMapper.LastReport.State.Equals(AutoMapperState.ClearMap.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private GameSnapshot? ApplyCachedMonsterCounter(GameSnapshot? snapshot, bool allowCached)
    {
        if (snapshot is null)
        {
            return null;
        }

        if (_lastKnownMapMonsterArea != 0 && _lastKnownMapMonsterArea != snapshot.AreaInstanceAddress)
        {
            _lastKnownMapMonsterArea = 0;
            _lastKnownMapMonsterCount = null;
            _lastKnownMapMonsterCountText = string.Empty;
        }

        if (snapshot.MapMonsterCount is { } count)
        {
            _lastKnownMapMonsterArea = snapshot.AreaInstanceAddress;
            _lastKnownMapMonsterCount = count;
            _lastKnownMapMonsterCountText = snapshot.MapMonsterCountText;
            return snapshot;
        }

        if (!allowCached ||
            _lastKnownMapMonsterArea != snapshot.AreaInstanceAddress ||
            _lastKnownMapMonsterCount is not { } cachedCount)
        {
            return snapshot;
        }

        return snapshot with
        {
            MapMonsterCount = cachedCount,
            MapMonsterCountText = _lastKnownMapMonsterCountText
        };
    }

    private void LogLiveSnapshot(GameSnapshot? snapshot, bool combatLight)
    {
        if (DateTime.UtcNow - _lastSnapshotLogUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastSnapshotLogUtc = DateTime.UtcNow;
        if (snapshot is null)
        {
            AppLog.Write("snapshot", combatLight ? "combat-light unresolved" : "unresolved");
            return;
        }

        string mode = combatLight ? "mode=combat-light " : "mode=full ";
        string player = snapshot.Player is { HasPosition: true } p
            ? $"player=0x{snapshot.LocalPlayerAddress:X} pos=({p.X:0.0},{p.Y:0.0},{p.Z:0.0})"
            : $"player=0x{snapshot.LocalPlayerAddress:X} pos=n/a";
        string terrain = snapshot.Terrain is { } t
            ? $" terrain={t.Width}x{t.Height}"
            : " terrain=n/a";
        string mapMonsters = snapshot.MapMonsterCount is null
            ? " mapMonsters=n/a"
            : $" mapMonsters={snapshot.MapMonsterCount} text=\"{snapshot.MapMonsterCountText}\"";
        string camera = snapshot.CameraMatrix is null ? " camera=n/a" : " camera=ok";
        string inventory = snapshot.Inventory is null
            ? $" inventory=n/a invError=\"{_gameReader.LastInventoryError}\""
            : $" inventory={snapshot.Inventory.Items.Count}/{snapshot.Inventory.FreeCellCount}free";
        string ui = snapshot.Ui is null ? " ui=n/a" : $" ui={snapshot.Ui.Elements.Count}";
        AppLog.Write("snapshot", $"{mode}igs=0x{snapshot.InGameStateAddress:X} area=0x{snapshot.AreaInstanceAddress:X} {player} entities={snapshot.Entities.Count}{terrain}{mapMonsters}{camera}{inventory}{ui}");
    }

    private void UpdateCombatLiveUi(GameSnapshot? snapshot, AutoMapperReport? autoReport, CombatReport? combatReport, bool includeHeavyUi)
    {
        if (snapshot is null)
        {
            LiveResolveText.Text = "Resolve: waiting";
            if (autoReport is not null)
            {
                UpdateAutoMapperUi(autoReport);
            }

            if (combatReport is not null)
            {
                UpdateCombatUi(combatReport);
            }

            return;
        }

        LiveResolveText.Text = $"Resolve: IGS 0x{snapshot.InGameStateAddress:X} / Area 0x{snapshot.AreaInstanceAddress:X}";
        LiveLocalPlayerText.Text = $"LocalPlayer: 0x{snapshot.LocalPlayerAddress:X}";

        PlayerData? player = snapshot.Player;
        if (player is null)
        {
            LiveHealthText.Text = "Health: n/a";
            LiveManaText.Text = "Mana: n/a";
            LivePositionText.Text = "Position: n/a";
        }
        else
        {
            LiveHealthText.Text = player.HasVitals
                ? $"Life/ES/Ward: {player.Health}/{player.MaxHealth} + {player.EnergyShield}/{player.MaxEnergyShield} + {player.Ward}/{player.MaxWard}"
                : "Health: component not resolved";

            LiveManaText.Text = player.HasVitals
                ? $"Mana: {player.Mana}/{player.MaxMana}"
                : "Mana: component not resolved";

            LivePositionText.Text = player.HasPosition
                ? $"Position: X {player.X:0.0}, Y {player.Y:0.0}, Z {player.Z:0.0}"
                : "Position: render component not resolved";
        }

        UpdateMapPreview(snapshot);
        _overlayWindow?.RenderSnapshot(snapshot);
        if (includeHeavyUi)
        {
            UpdateInventoryUi(snapshot.Inventory);
            UpdateAtlasDebugUi(snapshot.Ui);
        }

        if (autoReport is not null)
        {
            UpdateAutoMapperUi(autoReport);
            SyncCombatWithAutoMapper(autoReport, snapshot);
        }

        if (combatReport is not null)
        {
            UpdateCombatUi(_combatBot.LastReport);
        }

        if (_overlayWindow is not null &&
            _settings.OverlayFollowGameWindow &&
            DateTime.UtcNow - _lastOverlayPositionUtc >= TimeSpan.FromSeconds(1))
        {
            _lastOverlayPositionUtc = DateTime.UtcNow;
            PositionOverlay();
        }
    }

    private bool TryHandleDeath(GameSnapshot? snapshot)
    {
        bool botOwnsInput = (_combatBot.IsRunning && _settings.CombatActiveInputEnabled) ||
                            (_autoMapper.IsRunning && _settings.AutoMapperActiveInputEnabled);
        if (!botOwnsInput)
        {
            return false;
        }

        bool vitalsDead = snapshot?.Player is { HasVitals: true } player && player.Health <= 0;
        if (!vitalsDead && snapshot?.Ui is null)
        {
            return false;
        }

        if (DateTime.UtcNow - _lastReviveAttemptUtc < TimeSpan.FromMilliseconds(1800))
        {
            return vitalsDead;
        }

        string[] reviveTexts =
        [
            "Revive at Checkpoint",
            "Respawn at Checkpoint",
            "Revive",
            "Respawn"
        ];
        (float X, float Y)? button = LocateUiButton(reviveTexts);
        if (!vitalsDead && button is null)
        {
            return false;
        }

        _lastReviveAttemptUtc = DateTime.UtcNow;
        if (_combatBot.IsRunning)
        {
            _combatBot.Stop();
        }

        if (button is { } point &&
            _reviveInputController.ClickWindowPixelPoint(_settings.ProcessName, point.X, point.Y))
        {
            AppLog.Write("death", $"Clicked revive button at ({point.X:0},{point.Y:0}).");
            Dispatcher.InvokeAsync(() => Log($"Death detected: clicked revive button at ({point.X:0},{point.Y:0})."));
            return true;
        }

        if (vitalsDead && _reviveInputController.PressKey(_settings.ProcessName, "SPACE"))
        {
            AppLog.Write("death", "Revive button not decoded; pressed SPACE fallback.");
            Dispatcher.InvokeAsync(() => Log("Death detected: revive button not decoded; pressed SPACE fallback."));
            return true;
        }

        AppLog.Write("death", $"Revive failed: {_reviveInputController.LastError}");
        Dispatcher.InvokeAsync(() => Log($"Death detected: revive failed: {_reviveInputController.LastError}", true));
        return true;
    }

    private void StopLiveDataLoop()
    {
        if (_liveDataCts is null)
        {
            return;
        }

        _liveDataCts.Cancel();
        AppLog.Write("snapshot", "live data loop stop requested");
        _liveDataCts.Dispose();
        _liveDataCts = null;
    }

    private void UpdateLiveDataUi(GameSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            LiveResolveText.Text = "Resolve: waiting";
            UpdateMapPreview(null);
            if (_mapRunner.IsRunning)
            {
                UpdateMapRunnerUi(_mapRunner.UpdateSnapshot(null));
            }

            if (_autoMapper.IsRunning)
            {
                UpdateAutoMapperUi(_autoMapper.UpdateSnapshot(null));
            }

            if (_combatBot.IsRunning)
            {
                UpdateCombatUi(_combatBot.UpdateSnapshot(null));
            }

            return;
        }

        LiveResolveText.Text = $"Resolve: IGS 0x{snapshot.InGameStateAddress:X} / Area 0x{snapshot.AreaInstanceAddress:X}";
        LiveLocalPlayerText.Text = $"LocalPlayer: 0x{snapshot.LocalPlayerAddress:X}";

        PlayerData? player = snapshot.Player;
        if (player is null)
        {
            LiveHealthText.Text = "Health: n/a";
            LiveManaText.Text = "Mana: n/a";
            LivePositionText.Text = "Position: n/a";
            if (_mapRunner.IsRunning)
            {
                UpdateMapRunnerUi(_mapRunner.UpdateSnapshot(snapshot));
            }

            if (_autoMapper.IsRunning)
            {
                UpdateAutoMapperUi(_autoMapper.UpdateSnapshot(snapshot));
            }

            if (_combatBot.IsRunning)
            {
                UpdateCombatUi(_combatBot.UpdateSnapshot(snapshot));
            }

            return;
        }

        LiveHealthText.Text = player.HasVitals
            ? $"Life/ES/Ward: {player.Health}/{player.MaxHealth} + {player.EnergyShield}/{player.MaxEnergyShield} + {player.Ward}/{player.MaxWard}"
            : "Health: component not resolved";

        LiveManaText.Text = player.HasVitals
            ? $"Mana: {player.Mana}/{player.MaxMana}"
            : "Mana: component not resolved";

        LivePositionText.Text = player.HasPosition
            ? $"Position: X {player.X:0.0}, Y {player.Y:0.0}, Z {player.Z:0.0}"
            : "Position: render component not resolved";

        UpdateMapPreview(snapshot);
        UpdateInventoryUi(snapshot.Inventory);
        UpdateAtlasDebugUi(snapshot.Ui);
        _overlayWindow?.RenderSnapshot(snapshot);
        if (_mapRunner.IsRunning)
        {
            UpdateMapRunnerUi(_mapRunner.UpdateSnapshot(snapshot));
        }

        if (_autoMapper.IsRunning)
        {
            AutoMapperReport autoReport = _autoMapper.UpdateSnapshot(snapshot);
            UpdateAutoMapperUi(autoReport);
            SyncCombatWithAutoMapper(autoReport, snapshot);
        }

        if (_combatBot.IsRunning)
        {
            UpdateCombatUi(_combatBot.UpdateSnapshot(snapshot));
        }

        if (_overlayWindow is not null &&
            _settings.OverlayFollowGameWindow &&
            DateTime.UtcNow - _lastOverlayPositionUtc >= TimeSpan.FromSeconds(1))
        {
            _lastOverlayPositionUtc = DateTime.UtcNow;
            PositionOverlay();
        }
    }

    private void ResetLiveDataUi()
    {
        LiveResolveText.Text = "Resolve: idle";
        LiveLocalPlayerText.Text = "LocalPlayer: n/a";
        LiveHealthText.Text = "Health: n/a";
        LiveManaText.Text = "Mana: n/a";
        LivePositionText.Text = "Position: n/a";
        UpdateMapPreview(null);
        _overlayWindow?.RenderSnapshot(null);
        UpdateMapRunnerUi(MapRunnerReport.Waiting);
        UpdateAutoMapperUi(AutoMapperReport.Idle);
        UpdateCombatUi(CombatReport.Waiting);
        UpdateInventoryUi(null);
        UpdateAtlasDebugUi(null);
    }

    private void ToggleOverlayClick(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow is null)
        {
            OpenOverlay();
        }
        else
        {
            CloseOverlay();
        }
    }

    private void OpenOverlay()
    {
        if (_overlayWindow is not null)
        {
            return;
        }

        _overlayWindow = new OverlayWindow
        {
            Owner = this
        };
        ApplyOverlaySettingsToWindow();
        _overlayWindow.Closed += (_, _) =>
        {
            _overlayWindow = null;
            OverlayToggleButton.Content = "Open Overlay";
            AppLog.Write("overlay", "overlay window closed");
        };

        PositionOverlay();
        _overlayWindow.Show();
        OverlayToggleButton.Content = "Close Overlay";
        AppLog.Write("overlay", "overlay window opened");
    }

    private void ApplyOverlaySettingsClick(object sender, RoutedEventArgs e)
    {
        _settings.OverlayRotate180 = OverlayRotate180Box.IsChecked == true;
        _settings.OverlayFollowGameWindow = OverlayFollowGameBox.IsChecked == true;
        _settings.OverlayFullMapView = OverlayFullMapBox.IsChecked == true;
        _settings.OverlayWidth = ParseClampedDouble(OverlayWidthBox.Text, _settings.OverlayWidth, 240, 1200);
        _settings.OverlayHeight = ParseClampedDouble(OverlayHeightBox.Text, _settings.OverlayHeight, 240, 1200);
        _settings.OverlayWorldUnitsPerPixel = ParseClampedDouble(OverlayScaleBox.Text, _settings.OverlayWorldUnitsPerPixel, 4, 40);
        _settings.OverlayOpacityPercent = ParseClampedDouble(OverlayOpacityBox.Text, _settings.OverlayOpacityPercent, 30, 100);
        _settings.OverlayShowTerrain = OverlayShowTerrainBox.IsChecked == true;
        _settings.OverlayShowMonsters = OverlayShowMonstersBox.IsChecked == true;
        _settings.OverlayShowChests = OverlayShowChestsBox.IsChecked == true;
        _settings.OverlayShowTransitions = OverlayShowTransitionsBox.IsChecked == true;
        _settings.OverlayShowNpcs = OverlayShowNpcsBox.IsChecked == true;
        _settings.OverlayShowOther = OverlayShowOtherBox.IsChecked == true;
        _settingsService.Save(_settings);

        OverlayWidthBox.Text = _settings.OverlayWidth.ToString("0");
        OverlayHeightBox.Text = _settings.OverlayHeight.ToString("0");
        OverlayScaleBox.Text = _settings.OverlayWorldUnitsPerPixel.ToString("0.##");
        OverlayOpacityBox.Text = _settings.OverlayOpacityPercent.ToString("0");
        UpdateOverlayScaleText();
        ApplyOverlaySettingsToWindow();
        PositionOverlay();
        Log($"Overlay settings applied: {OverlayModeText()}, size {_settings.OverlayWidth:0}x{_settings.OverlayHeight:0}, scale {_settings.OverlayWorldUnitsPerPixel:0.##}, opacity {_settings.OverlayOpacityPercent:0}%.");
    }

    private void ApplyOverlaySettingsToWindow()
    {
        if (_overlayWindow is null)
        {
            return;
        }

        _overlayWindow.RotateMap180 = _settings.OverlayRotate180;
        _overlayWindow.FullMapView = _settings.OverlayFullMapView;
        _overlayWindow.WorldUnitsPerPixel = _settings.OverlayWorldUnitsPerPixel;
        _overlayWindow.ShowTerrain = _settings.OverlayShowTerrain;
        _overlayWindow.ShowMonsters = _settings.OverlayShowMonsters;
        _overlayWindow.ShowChests = _settings.OverlayShowChests;
        _overlayWindow.ShowTransitions = _settings.OverlayShowTransitions;
        _overlayWindow.ShowNpcs = _settings.OverlayShowNpcs;
        _overlayWindow.ShowOther = _settings.OverlayShowOther;
        _overlayWindow.ShowBotDebug = _settings.CombatDebugOverlayEnabled;
        _overlayWindow.Width = _settings.OverlayWidth;
        _overlayWindow.Height = _settings.OverlayHeight;
        _overlayWindow.Opacity = _settings.OverlayOpacityPercent / 100.0;
    }

    private void CloseOverlay()
    {
        if (_overlayWindow is null)
        {
            return;
        }

        OverlayWindow overlay = _overlayWindow;
        _overlayWindow = null;
        overlay.Close();
        OverlayToggleButton.Content = "Open Overlay";
    }

    private void EnsureBotStatsOverlay()
    {
        if (_botStatsOverlayWindow is null)
        {
            _botStatsOverlayWindow = new BotStatsOverlayWindow();
            _botStatsOverlayWindow.Closed += (_, _) => _botStatsOverlayWindow = null;
        }

        _botStatsOverlayWindow.SetHotkey(_settings.BotToggleHotkey);
        if (!_botStatsOverlayWindow.IsVisible)
        {
            _botStatsOverlayWindow.Show();
        }
    }

    private void CloseBotStatsOverlay()
    {
        if (_botStatsOverlayWindow is null)
        {
            return;
        }

        BotStatsOverlayWindow overlay = _botStatsOverlayWindow;
        _botStatsOverlayWindow = null;
        overlay.Close();
    }

    private void PositionOverlay()
    {
        if (_overlayWindow is null)
        {
            return;
        }

        Rect? gameRect = GameWindowLocator.TryGetWindowRect(ProcessNameBox.Text.Trim());
        if (gameRect is { } rect)
        {
            _overlayWindow.Left = rect.Right - _overlayWindow.Width - 24;
            _overlayWindow.Top = rect.Top + 72;
            return;
        }

        _overlayWindow.Left = Left + Width + 16;
        _overlayWindow.Top = Top;
    }

    private void UpdateOverlayScaleText()
    {
        if (MapScaleText is null)
        {
            return;
        }

        MapScaleText.Text = $"Overlay: {OverlayModeText()}, 1px = {_settings.OverlayWorldUnitsPerPixel:0.##} world";
    }

    private string OverlayModeText()
    {
        string orientation = _settings.OverlayRotate180 ? "rotated 180" : "normal";
        return _settings.OverlayFullMapView ? $"{orientation}, full map" : orientation;
    }

    private void SelectMovementMode(string movementMode)
    {
        foreach (object item in CombatMovementModeBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem &&
                string.Equals(comboBoxItem.Content?.ToString(), movementMode, StringComparison.OrdinalIgnoreCase))
            {
                CombatMovementModeBox.SelectedItem = comboBoxItem;
                return;
            }
        }

        CombatMovementModeBox.SelectedIndex = 0;
    }

    private string SelectedMovementMode()
    {
        return CombatMovementModeBox.SelectedItem is ComboBoxItem comboBoxItem
            ? comboBoxItem.Content?.ToString() ?? "MouseClick"
            : "MouseClick";
    }

    private void SelectMappingPreset(string preset)
    {
        foreach (object item in MappingPresetBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem &&
                string.Equals(comboBoxItem.Tag?.ToString(), preset, StringComparison.OrdinalIgnoreCase))
            {
                MappingPresetBox.SelectedItem = comboBoxItem;
                return;
            }
        }

        MappingPresetBox.SelectedIndex = 0;
    }

    private string SelectedMappingPreset()
    {
        return MappingPresetBox.SelectedItem is ComboBoxItem comboBoxItem
            ? comboBoxItem.Tag?.ToString() ?? "FullMapClear"
            : "FullMapClear";
    }

    private void UpdateMapPreview(GameSnapshot? snapshot)
    {
        MapCanvas.Children.Clear();

        if (snapshot?.Player is not { HasPosition: true } player)
        {
            MapPreviewStatusText.Text = "Map preview waiting for player position.";
            MapEntityCountText.Text = "Entities: 0";
            MapMonsterCountText.Text = "Monsters: 0";
            MapChestCountText.Text = "Chests: 0";
            return;
        }

        double width = Math.Max(MapCanvas.ActualWidth, 1);
        double height = Math.Max(MapCanvas.ActualHeight, 1);
        if (width <= 1 || height <= 1)
        {
            width = 720;
            height = 380;
        }

        const double worldUnitsPerPixel = 10.0;
        double centerX = width / 2.0;
        double centerY = height / 2.0;

        DrawGrid(width, height, centerX, centerY);
        if (_settings.OverlayShowTerrain)
        {
            DrawTerrain(snapshot.Terrain, player, centerX, centerY, width, height, worldUnitsPerPixel);
        }

        int monsterCount = 0;
        int chestCount = 0;
        int visibleCount = 0;
        foreach (EntityData entity in snapshot.Entities)
        {
            if (!ShouldDrawEntity(entity.Category))
            {
                continue;
            }

            (double x, double y) = MapProjection.WorldToMapPoint(
                entity.X,
                entity.Y,
                entity.Z,
                player,
                centerX,
                centerY,
                worldUnitsPerPixel,
                _settings.OverlayRotate180);

            if (x < -10 || y < -10 || x > width + 10 || y > height + 10)
            {
                continue;
            }

            Brush brush = BrushForCategory(entity.Category);
            double size = entity.Category == "Monster" ? 6 : 5;
            if (entity.Category == "Monster")
            {
                monsterCount++;
            }
            else if (entity.Category == "Chest")
            {
                chestCount++;
            }

            visibleCount++;
            Ellipse dot = new()
            {
                Width = size,
                Height = size,
                Fill = brush,
                Stroke = Brushes.Black,
                StrokeThickness = 0.5,
                ToolTip = string.IsNullOrWhiteSpace(entity.Metadata) ? entity.Category : entity.Metadata
            };

            Canvas.SetLeft(dot, x - (size / 2));
            Canvas.SetTop(dot, y - (size / 2));
            MapCanvas.Children.Add(dot);
        }

        Ellipse playerDot = new()
        {
            Width = 12,
            Height = 12,
            Fill = FindBrush("AccentBrush"),
            Stroke = Brushes.White,
            StrokeThickness = 1.2,
            ToolTip = "Local player"
        };
        Canvas.SetLeft(playerDot, centerX - 6);
        Canvas.SetTop(playerDot, centerY - 6);
        MapCanvas.Children.Add(playerDot);

        string terrainStatus = snapshot.Terrain is { } terrain
            ? $"; terrain {terrain.Width}x{terrain.Height}"
            : "; terrain waiting";
        string monsterCounter = snapshot.MapMonsterCount is null
            ? string.Empty
            : $"; map monsters {snapshot.MapMonsterCount} ({snapshot.MapMonsterCountText})";
        MapPreviewStatusText.Text = $"Area 0x{snapshot.AreaInstanceAddress:X}; player X {player.X:0.0}, Y {player.Y:0.0}{terrainStatus}{monsterCounter}";
        MapEntityCountText.Text = $"Entities: {visibleCount}/{snapshot.Entities.Count}";
        MapMonsterCountText.Text = snapshot.MapMonsterCount is null
            ? $"Monsters: {monsterCount}"
            : $"Monsters: {snapshot.MapMonsterCount} map / {monsterCount} visible";
        MapChestCountText.Text = $"Chests: {chestCount}";
    }

    private void DrawTerrain(TerrainData? terrain, PlayerData player, double centerX, double centerY, double width, double height, double worldUnitsPerPixel)
    {
        if (terrain is null)
        {
            return;
        }

        double playerGridX = player.X / Poe2Offsets.WorldToGridRatio;
        double playerGridY = player.Y / Poe2Offsets.WorldToGridRatio;
        double pixelsPerGridCell = Poe2Offsets.WorldToGridRatio / worldUnitsPerPixel;
        const int sampleStep = 4;
        const int sampleRadius = 100;

        Brush walkableBrush = new SolidColorBrush(Color.FromArgb(72, 78, 154, 124));
        Brush walkableBorderBrush = new SolidColorBrush(Color.FromArgb(145, 132, 217, 172));
        for (int gy = -sampleRadius; gy <= sampleRadius; gy += sampleStep)
        {
            int terrainY = (int)Math.Round(playerGridY + gy);
            if (terrainY < 0 || terrainY >= terrain.Height)
            {
                continue;
            }

            for (int gx = -sampleRadius; gx <= sampleRadius; gx += sampleStep)
            {
                int terrainX = (int)Math.Round(playerGridX + gx);
                if (terrainX < 0 || terrainX >= terrain.Width)
                {
                    continue;
                }

                if (terrain.Walkable[(terrainY * terrain.Width) + terrainX] == 0)
                {
                    continue;
                }

                (double dx, double dy) = MapProjection.GridDeltaToMapDelta(gx, gy, pixelsPerGridCell);
                double x = _settings.OverlayRotate180 ? centerX - dx : centerX + dx;
                double y = _settings.OverlayRotate180 ? centerY - dy : centerY + dy;
                if (x < -4 || y < -4 || x > width + 4 || y > height + 4)
                {
                    continue;
                }

                Rectangle cell = new()
                {
                    Width = Math.Max(2, sampleStep * pixelsPerGridCell),
                    Height = Math.Max(2, sampleStep * pixelsPerGridCell),
                    Fill = walkableBrush,
                    Stroke = walkableBorderBrush,
                    StrokeThickness = 0.65,
                    SnapsToDevicePixels = true
                };
                Canvas.SetLeft(cell, x);
                Canvas.SetTop(cell, y);
                MapCanvas.Children.Add(cell);
            }
        }
    }

    private void DrawGrid(double width, double height, double centerX, double centerY)
    {
        Brush gridBrush = new SolidColorBrush(Color.FromRgb(35, 38, 43));
        for (double x = centerX % 50; x < width; x += 50)
        {
            MapCanvas.Children.Add(new Line
            {
                X1 = x,
                Y1 = 0,
                X2 = x,
                Y2 = height,
                Stroke = gridBrush,
                StrokeThickness = 1
            });
        }

        for (double y = centerY % 50; y < height; y += 50)
        {
            MapCanvas.Children.Add(new Line
            {
                X1 = 0,
                Y1 = y,
                X2 = width,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1
            });
        }
    }

    private Brush BrushForCategory(string category)
    {
        return category switch
        {
            "Monster" => Brushes.IndianRed,
            "Chest" => Brushes.Goldenrod,
            "Transition" => FindBrush("AccentBlueBrush"),
            "Npc" => Brushes.LightSkyBlue,
            "Player" => Brushes.MediumSeaGreen,
            "Encounter" => Brushes.MediumPurple,
            "Loot" => Brushes.LawnGreen,
            "Object" => Brushes.DimGray,
            _ => Brushes.Gray
        };
    }

    private bool ShouldDrawEntity(string category)
    {
        return category switch
        {
            "Monster" => _settings.OverlayShowMonsters,
            "Chest" => _settings.OverlayShowChests,
            "Transition" => _settings.OverlayShowTransitions,
            "Npc" => _settings.OverlayShowNpcs,
            "Player" => true,
            "Encounter" => _settings.OverlayShowOther,
            "Loot" => _settings.OverlayShowOther,
            "Object" => _settings.OverlayShowOther,
            "Other" => _settings.OverlayShowOther,
            _ => _settings.OverlayShowOther
        };
    }

    private void UpdateMapRunnerUi(MapRunnerReport report)
    {
        MapRunnerStateText.Text = report.AreaInstanceAddress == 0
            ? $"State: {report.State}"
            : $"State: {report.State} / Area 0x{report.AreaInstanceAddress:X} / {report.AreaTime:mm\\:ss}";
        MapRunnerCountsText.Text = $"Counts: monsters {report.MonsterCount}, chests {report.ChestCount}, transitions {report.TransitionCount}, terrain {(report.HasTerrain ? "yes" : "no")}";
        MapRunnerObjectiveText.Text = $"Objective: {report.Objective}";
        MapRunnerActionText.Text = $"Action: {report.LastAction}";
        _botStatsOverlayWindow?.UpdateMap(report);
    }

    private void UpdateAutoMapperUi(AutoMapperReport report)
    {
        MaybeRefreshLootValueCache();
        LootValueSummary lootSummary = _lootValueTracker.Summarize(report.LootCounts);
        AutoMapperReport displayReport = DisplayAutoMapperReport(report);
        AutoMapperStatusText.Text = $"State: {displayReport.State} / run {displayReport.CurrentRun}/{displayReport.TargetRuns}";
        AutoMapperActionText.Text = $"Action: {displayReport.LastAction}";
        AutoMapperErrorText.Text = string.IsNullOrWhiteSpace(report.Error) ? "Error: none" : $"Error: {report.Error}";
        AutoMapperInventoryText.Text = $"Inventory: free {report.InventoryFreeCells}, protected {report.ProtectedCells}";
        AutoMapperMapText.Text = $"Map: {report.SelectedMap}; mobs left {report.MobsLeft}";
        LootValueText.Text = $"Divine value: {lootSummary.DivineEquivalent:0.0000}" +
                             (lootSummary.LastPriceUpdateUtc == DateTime.MinValue ? " / prices waiting" : $" / prices {lootSummary.LastPriceUpdateUtc:HH:mm}");
        LootStatsTextBox.Text = report.LootCounts.Count == 0
            ? "No dumped loot counted yet."
            : FormatLootStats(report.LootCounts);
        _botStatsOverlayWindow?.UpdateAutoMapper(displayReport, lootSummary);
    }

    private AutoMapperReport DisplayAutoMapperReport(AutoMapperReport report)
    {
        if (_settings.MappingPreset.Equals("BossRush", StringComparison.OrdinalIgnoreCase) &&
            report.State.Equals(AutoMapperState.ClearMap.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return report with
            {
                State = "BossRush",
                LastAction = string.IsNullOrWhiteSpace(report.LastAction)
                    ? "Boss Rush: hunting boss landmark and clearing only path threats."
                    : $"Boss Rush: {report.LastAction}"
            };
        }

        return report;
    }

    private string FormatLootStats(IReadOnlyDictionary<string, int> counts)
    {
        List<string> lines = [];
        var currency = counts
            .Where(pair => LootClassification.IsCurrencyBucket(pair.Key))
            .OrderByDescending(pair => pair.Value * _lootValueTracker.ValueOfDivine(pair.Key))
            .ThenBy(pair => pair.Key)
            .ToList();
        if (currency.Count > 0)
        {
            lines.Add("Currency");
            foreach ((string name, int count) in currency)
            {
                double value = count * _lootValueTracker.ValueOfDivine(name);
                lines.Add($"{name}: {count} = {value:0.0000} div");
            }
        }

        var items = counts
            .Where(pair => !LootClassification.IsCurrencyBucket(pair.Key))
            .OrderBy(pair => LootRarityOrder(pair.Key))
            .ThenBy(pair => pair.Key)
            .ToList();
        if (items.Count > 0)
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("Items");
            lines.AddRange(items.Select(pair => $"{pair.Key}: {pair.Value}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int LootRarityOrder(string key) => key switch
    {
        "Unique" => 0,
        "Rare" => 1,
        "Magic" => 2,
        "Common" => 3,
        _ => 9
    };

    private void MaybeRefreshLootValueCache()
    {
        TimeSpan refresh = TimeSpan.FromMinutes(Math.Clamp(_settings.LootPriceRefreshMinutes, 30, 360));
        if (DateTime.UtcNow - _lastLootValueRefreshUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastLootValueRefreshUtc = DateTime.UtcNow;
        string cachePath = System.IO.Path.Combine(ProjectPaths.Root, "data", "poe-ninja-currency-cache.json");
        _ = Task.Run(async () => await _lootValueTracker.RefreshAsync(_settings.PoeNinjaLeague, cachePath, refresh));
    }

    private void UpdateInventoryUi(InventorySnapshot? inventory)
    {
        if (inventory is null)
        {
            InventorySummaryText.Text = "Inventory: scanner waiting";
            InventoryItemsText.Text = "No inventory snapshot yet.";
            return;
        }

        InventoryProtectedMask mask = new(_settings.ProtectedInventoryCells);
        int protectedItems = inventory.Items.Count(item => inventory.IsProtected(mask, item));
        InventorySummaryText.Text = $"Inventory: {inventory.Width}x{inventory.Height}, items {inventory.Items.Count}, free cells {inventory.FreeCellCount}, protected cells {mask.Count}, protected items {protectedItems}";
        InventoryItemsText.Text = string.Join(Environment.NewLine, inventory.Items
            .OrderBy(item => item.StartY)
            .ThenBy(item => item.StartX)
            .Take(80)
            .Select(item =>
            {
                string marker = inventory.IsProtected(mask, item) ? "LOCK" : "dump";
                string name = string.IsNullOrWhiteSpace(item.Metadata) ? "unknown" : item.Metadata;
                return $"{marker} [{item.StartX},{item.StartY}-{item.EndX},{item.EndY}] {name}";
            }));
    }

    private void UpdateAtlasDebugUi(UiSnapshot? ui)
    {
        if (ui is null)
        {
            AtlasCandidateText.Text = "Atlas/UI: scanner waiting";
            DebugUiTextBox.Text = "No UI snapshot yet.";
            return;
        }

        IReadOnlyList<UiElementData> texts = ui.VisibleTextElements;
        AtlasCandidateText.Text = $"Atlas/UI text nodes: {texts.Count}. Last atlas node: ({_settings.LastAtlasNodeX:0},{_settings.LastAtlasNodeY:0})";
        DebugUiTextBox.Text = string.Join(Environment.NewLine, texts
            .Take(160)
            .Select(element => $"0x{element.Address:X} ({element.X:0},{element.Y:0}) {element.Width:0}x{element.Height:0}: {element.Text}"));
    }

    private void UpdateCombatUi(CombatReport report)
    {
        CombatStateText.Text = $"State: {report.State}";
        CombatVitalsText.Text = report.HasVitals
            ? $"Vitals: HP {report.HealthPercent}%, MP {report.ManaPercent}%"
            : "Vitals: waiting";
        CombatThreatText.Text = $"Threat: nearby {report.NearbyMonsterCount}, remaining {report.RemainingMonsterCount}, route {report.RouteWaypointCount}. {report.Advice}";
        CombatActionText.Text = $"Action: {report.LastAction}";
        _overlayWindow?.SetCombatDebug(report);
        _botStatsOverlayWindow?.UpdateCombat(report);
    }

    private void SyncCombatWithAutoMapper(AutoMapperReport report, GameSnapshot snapshot)
    {
        if (!_autoMapper.IsRunning)
        {
            return;
        }

        bool shouldRunCombat =
            report.State.Equals(AutoMapperState.ClearMap.ToString(), StringComparison.OrdinalIgnoreCase) &&
            _settings.AutoMapperActiveInputEnabled &&
            !_settings.AutoMapperDryRun &&
            !SnapshotLooksLikeHideout(snapshot);

        if (shouldRunCombat)
        {
            if (_combatBot.IsRunning)
            {
                return;
            }

            _combatStartedByAutoMapper = true;
            _settings.CombatEnabled = true;
            _settings.CombatActiveInputEnabled = true;
            SetCheckBoxSilently(EnableCombat, true);
            SetCheckBoxSilently(CombatActiveInputBox, true);
            _combatBot.UpdateConfig(ToCombatConfig(_settings));
            _combatBot.Start();
            UpdateCombatUi(_combatBot.LastReport);
            Log("Combat executor armed by AutoStart ClearMap stage.");
            return;
        }

        if (_combatStartedByAutoMapper && _combatBot.IsRunning)
        {
            _combatBot.Stop();
            _combatStartedByAutoMapper = false;
            _settings.CombatEnabled = false;
            SetCheckBoxSilently(EnableCombat, false);
            _combatBot.UpdateConfig(ToCombatConfig(_settings));
            UpdateCombatUi(_combatBot.LastReport);
            Log("Combat executor paused because AutoStart left ClearMap stage.");
        }
    }

    private static bool SnapshotLooksLikeHideout(GameSnapshot snapshot) =>
        snapshot.Entities.Any(entity =>
            entity.Category.Equals("Stash", StringComparison.OrdinalIgnoreCase) ||
            entity.Category.Equals("MapDevice", StringComparison.OrdinalIgnoreCase) ||
            entity.Category.Equals("Waypoint", StringComparison.OrdinalIgnoreCase));

    private void EnableMapRunnerChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            if (EnableMapRunner.IsChecked == true)
            {
                _settings.MapRunnerEnabled = true;
                _settingsService.Save(_settings);
                _mapRunner.Start();
                UpdateMapRunnerUi(_mapRunner.LastReport);
                StatusBot.Text = "Map Monitor";
                StatusBot.Foreground = FindBrush("AccentBlueBrush");
                EnsureBotStatsOverlay();
            }
            else
            {
                _settings.MapRunnerEnabled = false;
                _settingsService.Save(_settings);
                _mapRunner.Stop();
                UpdateMapRunnerUi(_mapRunner.LastReport);
                UpdateBotState();
            }
        }
        catch (Exception ex)
        {
            EnableMapRunner.IsChecked = false;
            Log($"Map runner could not start: {ex.Message}");
        }
    }

    private void EnableAutoMapperChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyAutoMapperSettingsFromUi(saveToDisk: true);
        if (EnableAutoMapper.IsChecked == true)
        {
            _atlasDumpedThisRun = false;
            _autoMapper.Start();
            UpdateAutoMapperUi(_autoMapper.LastReport);
            EnsureBotStatsOverlay();
        }
        else
        {
            _autoMapper.Stop();
            UpdateAutoMapperUi(_autoMapper.LastReport);
        }

        UpdateBotState();
    }

    private void EnableCombatChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (EnableCombat.IsChecked == true)
        {
            _settings.CombatEnabled = true;
            _settingsService.Save(_settings);
            _combatBot.Start();
            UpdateCombatUi(_combatBot.LastReport);
        }
        else
        {
            _settings.CombatEnabled = false;
            _settingsService.Save(_settings);
            _combatBot.Stop();
            UpdateCombatUi(_combatBot.LastReport);
        }

        UpdateBotState();
    }

    private void SaveCombatConfigClick(object sender, RoutedEventArgs e)
    {
        ApplyCombatSettingsFromUi(saveToDisk: true);
        Log("Combat configuration saved.");
    }

    private void SaveAutoMapperConfigClick(object sender, RoutedEventArgs e)
    {
        ApplyAutoMapperSettingsFromUi(saveToDisk: true);
        Log("AutoStart configuration saved.");
    }

    private void SaveInventoryConfigClick(object sender, RoutedEventArgs e)
    {
        ApplyAutoMapperSettingsFromUi(saveToDisk: true);
        Log("Inventory safe-slot configuration saved.");
    }

    private void CalibrateInventoryGridClick(object sender, RoutedEventArgs e)
    {
        if (!_gameReader.IsConnected)
        {
            InventoryCalibrationText.Text = "Grid calibration: attach to the game first.";
            Log("Grid calibration failed: not attached.");
            return;
        }

        Rect? gameRect = GameWindowLocator.TryGetWindowRect(_gameReader.ConnectedProcessName);
        if (gameRect is not { } rect || rect.Width <= 0 || rect.Height <= 0)
        {
            InventoryCalibrationText.Text = "Grid calibration: could not read the game window rectangle.";
            Log("Grid calibration failed: no game window rect.");
            return;
        }

        InventorySnapshot? liveInventory = _gameReader.ReadInventorySnapshot();
        int columns = liveInventory is { Width: > 0 and <= 24 } ? liveInventory.Width : 12;
        int rows = liveInventory is { Height: > 0 and <= 12 } ? liveInventory.Height : 5;
        (GridGeometry? grid, IReadOnlyList<UiScreenElement> candidates) =
            _gameReader.DetectInventoryGrid((float)rect.Width, (float)rect.Height, columns, rows);

        AppLog.Write("calibrate", $"window={rect.Width}x{rect.Height} grid={columns}x{rows} candidates={candidates.Count}");
        foreach (UiScreenElement candidate in candidates.Take(6))
        {
            AppLog.Write("calibrate", $"cand 0x{candidate.Address:X} rect=({candidate.ScreenX:0},{candidate.ScreenY:0} {candidate.ScreenWidth:0}x{candidate.ScreenHeight:0}) ratio={candidate.AspectRatio:0.00} text=\"{candidate.Text}\"");
        }

        if (grid is null)
        {
            InventoryCalibrationText.Text = $"Grid calibration: no backpack grid found among {candidates.Count} candidates. Open the inventory in-game and retry. See log for details.";
            Log("Grid calibration: no grid container matched. Check that the inventory is open.");
            return;
        }

        (double left, double top, double width, double height) = grid.AsFractions((float)rect.Width, (float)rect.Height);
        _settings.InventoryGridLeft = Math.Clamp(left, 0, 1);
        _settings.InventoryGridTop = Math.Clamp(top, 0, 1);
        _settings.InventoryGridWidth = Math.Clamp(width, 0.05, 1);
        _settings.InventoryGridHeight = Math.Clamp(height, 0.05, 1);

        _applyingSettingsToUi = true;
        InventoryGridLeftBox.Text = _settings.InventoryGridLeft.ToString("0.####", CultureInfo.InvariantCulture);
        InventoryGridTopBox.Text = _settings.InventoryGridTop.ToString("0.####", CultureInfo.InvariantCulture);
        InventoryGridWidthBox.Text = _settings.InventoryGridWidth.ToString("0.####", CultureInfo.InvariantCulture);
        InventoryGridHeightBox.Text = _settings.InventoryGridHeight.ToString("0.####", CultureInfo.InvariantCulture);
        _applyingSettingsToUi = false;

        _autoMapper.UpdateConfig(ToAutoMapperConfig(_settings));
        _settingsService.Save(_settings);

        InventoryCalibrationText.Text =
            $"Grid calibration: {grid.Source} -> px ({grid.OriginX:0},{grid.OriginY:0}) cell {grid.CellWidth:0}x{grid.CellHeight:0}; " +
            $"fractions L={_settings.InventoryGridLeft:0.###} T={_settings.InventoryGridTop:0.###} W={_settings.InventoryGridWidth:0.###} H={_settings.InventoryGridHeight:0.###}. Saved.";
        Log($"Grid calibration applied from memory ({grid.Source}).");
    }

    private (float X, float Y)? LocateUiButton(string[] texts)
    {
        if (!_gameReader.IsConnected)
        {
            return null;
        }

        Rect? gameRect = GameWindowLocator.TryGetWindowRect(_gameReader.ConnectedProcessName);
        if (gameRect is not { } rect || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        return _gameReader.FindUiButtonCenter((float)rect.Width, (float)rect.Height, texts);
    }

    private (double X, double Y)? LocateLootLabel(GameSnapshot snapshot, EntityData loot)
    {
        if (!_gameReader.IsConnected)
        {
            return null;
        }

        Rect? gameRect = GameWindowLocator.TryGetWindowRect(_gameReader.ConnectedProcessName);
        if (gameRect is not { } rect || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        if (!MapProjection.TryWorldToScreen(
                snapshot.CameraMatrix,
                loot.X,
                loot.Y,
                loot.Z,
                rect.Width,
                rect.Height,
                out double screenX,
                out double screenY))
        {
            return null;
        }

        float anchorX = (float)(screenX + _settings.CombatLootLabelXOffsetPixels);
        float anchorY = (float)(screenY + _settings.CombatLootLabelYOffsetPixels);
        float searchRadius = (float)Math.Clamp(Math.Max(80, _settings.CombatLootLabelSearchPixels * 3), 40, 220);
        return _gameReader.FindNearestUiTextCenter((float)rect.Width, (float)rect.Height, anchorX, anchorY, searchRadius);
    }

    private AtlasSelection? ReadAtlasSelection()
    {
        if (!_gameReader.IsConnected)
        {
            return null;
        }

        Rect? gameRect = GameWindowLocator.TryGetWindowRect(_gameReader.ConnectedProcessName);
        if (gameRect is not { } rect || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        (IReadOnlyList<AtlasNodeData> nodes, (int X, int Y)? current,
            IReadOnlyDictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>> connections) =
            _gameReader.ReadAtlasNodes((float)rect.Width, (float)rect.Height);

        // Auto-dump the atlas once per AutoStart run (first lap) so the detected nodes are logged without
        // a manual button press.
        if (!_atlasDumpedThisRun && nodes.Count > 0)
        {
            _atlasDumpedThisRun = true;
            LogAtlasDump(nodes, current, rect.Width, rect.Height);
            Dispatcher.Invoke(() => Log($"Atlas auto-dump: {nodes.Count} nodes, {nodes.Count(n => n.IsRealMap)} real maps, {connections.Count} graph nodes."));
        }

        return new AtlasSelection(nodes, current, (float)rect.Width, (float)rect.Height, connections);
    }

    private static void LogAtlasDump(IReadOnlyList<AtlasNodeData> nodes, (int X, int Y)? current, double winW, double winH)
    {
        int visibleCount = nodes.Count(n => n.Visible);
        int onScreen = nodes.Count(n => n.Visible && n.CenterX >= 0 && n.CenterX <= winW && n.CenterY >= 0 && n.CenterY <= winH);
        AppLog.Write("atlas", $"nodes={nodes.Count} visible={visibleCount} onScreen={onScreen} current={(current is { } c ? $"({c.X},{c.Y})" : "n/a")} window={winW}x{winH}");

        (int X, int Y) cur = current ?? (0, 0);
        int shown = 0;
        foreach (AtlasNodeData node in nodes
            .Where(n => n.IsRealMap)
            .OrderBy(n => ((n.GridX - cur.X) * (n.GridX - cur.X)) + ((n.GridY - cur.Y) * (n.GridY - cur.Y))))
        {
            if (shown++ >= 40)
            {
                break;
            }

            string tags = node.Tags.Count == 0 ? "" : " tags=[" + string.Join(",", node.Tags) + "]";
            AppLog.Write("atlas", $"grid({node.GridX},{node.GridY}) vis={node.Visible} unlk={node.Unlocked} visited={node.Visited} content={node.HasContent} screen=({node.CenterX:0},{node.CenterY:0}) \"{node.MapName}\" [{node.MapCode}]{tags}");
        }
    }

    private void DumpAtlasNodesClick(object sender, RoutedEventArgs e)
    {
        if (!_gameReader.IsConnected)
        {
            AtlasNodesDumpText.Text = "Atlas nodes: attach to the game first.";
            return;
        }

        Rect? gameRect = GameWindowLocator.TryGetWindowRect(_gameReader.ConnectedProcessName);
        if (gameRect is not { } rect || rect.Width <= 0 || rect.Height <= 0)
        {
            AtlasNodesDumpText.Text = "Atlas nodes: could not read the game window rectangle.";
            return;
        }

        (IReadOnlyList<AtlasNodeData> nodes, (int X, int Y)? current, _) =
            _gameReader.ReadAtlasNodes((float)rect.Width, (float)rect.Height);

        LogAtlasDump(nodes, current, rect.Width, rect.Height);

        int realCount = nodes.Count(n => n.IsRealMap);
        AtlasNodesDumpText.Text = nodes.Count == 0
            ? "Atlas nodes: 0 read. Make sure the atlas screen is open in-game, then retry."
            : $"Atlas nodes: {nodes.Count} read ({realCount} real maps), current={(current is { } cc ? $"({cc.X},{cc.Y})" : "n/a")}. See log ([atlas]).";
        Log($"Atlas dump: {nodes.Count} nodes, {realCount} real maps.");
    }

    private void TestWaystoneHoverClick(object sender, RoutedEventArgs e)
    {
        if (!_gameReader.IsConnected)
        {
            InventoryCalibrationText.Text = "Waystone hover test: attach to the game first.";
            Log("Waystone hover test failed: not attached.");
            return;
        }

        GameSnapshot? snapshot = _gameReader.ReadSnapshot();
        string result = _autoMapper.TestWaystoneHover(snapshot);
        InventoryCalibrationText.Text = $"Waystone hover test: {result}";
        Log($"Waystone hover test: {result}");
    }

    private void RuntimeSettingsChanged(object sender, RoutedEventArgs e)
    {
        ApplyRuntimeSettingsFromUi(saveToDisk: false);
    }

    private void RuntimeTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyRuntimeSettingsFromUi(saveToDisk: false);
    }

    private void ApplyRuntimeSettingsFromUi(bool saveToDisk)
    {
        ApplyAutoMapperSettingsFromUi(saveToDisk);
        ApplyMapRunnerSettingsFromUi(saveToDisk);
        ApplyCombatSettingsFromUi(saveToDisk);
        ApplyOverlaySettingsToWindow();
    }

    private void ApplyAutoMapperSettingsFromUi(bool saveToDisk)
    {
        if (!IsLoaded || _applyingSettingsToUi)
        {
            return;
        }

        _settings.AutoMapperEnabled = EnableAutoMapper.IsChecked == true;
        _settings.MappingPreset = SelectedMappingPreset();
        _settings.RequireBossKillBeforeReturn = RequireBossKillBox.IsChecked == true ||
                                                _settings.MappingPreset.Equals("BossRush", StringComparison.OrdinalIgnoreCase);
        _settings.AutoMapperActiveInputEnabled = AutoMapperActiveInputBox.IsChecked == true;
        _settings.AutoMapperDryRun = false;
        _settings.AutoMapperRunsTarget = (int)ParseClampedDouble(AutoMapperRunsTargetBox.Text, _settings.AutoMapperRunsTarget, 0, 10_000);
        _settings.WaystoneStashTabName = WaystoneTabBox.Text.Trim();
        _settings.TabletStashTabName = TabletTabBox.Text.Trim();
        _settings.CurrencyStashTabName = CurrencyTabBox.Text.Trim();
        _settings.WaystoneTakeCount = (int)ParseClampedDouble(WaystoneTakeCountBox.Text, _settings.WaystoneTakeCount, 1, 12);
        _settings.MaxWaystoneTier = (int)ParseClampedDouble(MaxWaystoneTierBox.Text, _settings.MaxWaystoneTier, 1, 16);
        _settings.AllowUnknownWaystoneTier = AllowUnknownWaystoneTierBox.IsChecked == true;
        _settings.TabletTakeCount = (int)ParseClampedDouble(TabletTakeCountBox.Text, _settings.TabletTakeCount, 0, 12);
        _settings.PreferredTabletNames = SelectedTabletSlots();
        _settings.AlchemyWaystones = AlchemyWaystonesBox.IsChecked == true;
        _settings.AlchemyTakeCount = (int)ParseClampedDouble(AlchemyTakeCountBox.Text, _settings.AlchemyTakeCount, 0, 12);
        _settings.ReturnToHideoutKey = ReturnToHideoutKeyBox.Text.Trim();
        _settings.OpenInventoryKey = OpenInventoryKeyBox.Text.Trim();
        _settings.PortalKey = PortalKeyBox.Text.Trim();
        _settings.AtlasMapBlacklist = AtlasBlacklistBox.Text
            .Split([Environment.NewLine, "\n", "\r", ","], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _settings.AvoidDecoratedAtlasNodes = AvoidDecoratedAtlasNodesBox.IsChecked == true;
        _settings.AtlasDecorationRadiusPixels = ParseClampedDouble(AtlasDecorationRadiusBox.Text, _settings.AtlasDecorationRadiusPixels, 16, 240);
        _settings.InventoryGridLeft = ParseClampedDouble(InventoryGridLeftBox.Text, _settings.InventoryGridLeft, 0, 1);
        _settings.InventoryGridTop = ParseClampedDouble(InventoryGridTopBox.Text, _settings.InventoryGridTop, 0, 1);
        _settings.InventoryGridWidth = ParseClampedDouble(InventoryGridWidthBox.Text, _settings.InventoryGridWidth, 0.05, 1);
        _settings.InventoryGridHeight = ParseClampedDouble(InventoryGridHeightBox.Text, _settings.InventoryGridHeight, 0.05, 1);
        _autoMapper.UpdateConfig(ToAutoMapperConfig(_settings));

        if (saveToDisk)
        {
            _settingsService.Save(_settings);
        }
    }

    private void ApplyCombatSettingsFromUi(bool saveToDisk)
    {
        if (!IsLoaded || _applyingSettingsToUi)
        {
            return;
        }

        _settings.AttackSkillKey = AttackSkillKeyBox.Text.Trim();
        string oldHotkey = _settings.BotToggleHotkey;
        _settings.BotToggleHotkey = BotToggleHotkeyBox.Text.Trim();
        _settings.HealthFlaskKeys = HealthFlaskKeysBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _settings.ManaFlaskKeys = ManaFlaskKeysBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _settings.FlaskKeys = _settings.HealthFlaskKeys.Concat(_settings.ManaFlaskKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _settings.CombatActiveInputEnabled = CombatActiveInputBox.IsChecked == true;
        _settings.CombatAutoMoveEnabled = CombatAutoMoveBox.IsChecked == true;
        _settings.CombatAutoAttackEnabled = CombatAutoAttackBox.IsChecked == true;
        _settings.CombatTargetEncounters = _settings.MappingPreset.Equals("BossRush", StringComparison.OrdinalIgnoreCase)
            ? false
            : CombatTargetEncountersBox.IsChecked == true;
        _settings.CombatPressAllAbilities = CombatPressAllAbilitiesBox.IsChecked == true;
        _settings.CombatAutoLootEnabled = CombatAutoLootBox.IsChecked == true;
        _settings.CombatLootCurrencyEnabled = LootCurrencyBox.IsChecked == true;
        _settings.CombatLootGearEnabled = LootGearBox.IsChecked == true;
        _settings.CombatLootOtherEnabled = LootOtherBox.IsChecked == true;
        _settings.CombatExploreWhenNoTarget = CombatExploreWhenNoTargetBox.IsChecked == true;
        _settings.CombatKitingEnabled = CombatKitingBox.IsChecked == true;
        _settings.CombatDebugOverlayEnabled = CombatDebugOverlayBox.IsChecked == true;
        _settings.CombatProjectMovementToScreen = CombatProjectMovementBox.IsChecked == true;
        _settings.CombatInvertInputX = CombatInvertInputXBox.IsChecked == true;
        _settings.CombatInvertInputY = CombatInvertInputYBox.IsChecked == true;
        _settings.CombatMovementMode = SelectedMovementMode();
        _settings.CombatAbilityKeys = CombatAbilityKeysBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _settings.CombatRareComboKeys = CombatRareComboKeysBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _settings.CombatUniqueComboKeys = CombatUniqueComboKeysBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _settings.CombatUniqueComboRepeatEnabled = CombatUniqueComboRepeatBox.IsChecked == true;
        _settings.CombatLootKey = CombatLootKeyBox.Text.Trim();
        _settings.CombatEncounterInteractKey = CombatEncounterInteractKeyBox.Text.Trim();
        _settings.CombatMoveUpKey = CombatMoveUpKeyBox.Text.Trim();
        _settings.CombatMoveLeftKey = CombatMoveLeftKeyBox.Text.Trim();
        _settings.CombatMoveDownKey = CombatMoveDownKeyBox.Text.Trim();
        _settings.CombatMoveRightKey = CombatMoveRightKeyBox.Text.Trim();
        _settings.CombatDodgeKey = CombatDodgeKeyBox.Text.Trim();
        _settings.CombatRange = ParseClampedDouble(CombatRangeBox.Text, _settings.CombatRange, 80, 3000);
        _settings.CombatCastRange = ParseClampedDouble(CombatCastRangeBox.Text, _settings.CombatCastRange, 60, 3000);
        _settings.CombatFinishMonsterCount = (int)ParseClampedDouble(CombatFinishMonsterCountBox.Text, _settings.CombatFinishMonsterCount, 0, 1000);
        _settings.CombatMoveClickPixels = ParseClampedDouble(CombatMoveClickPixelsBox.Text, _settings.CombatMoveClickPixels, 50, 900);
        _settings.CombatWasdHoldMs = (int)ParseClampedDouble(CombatWasdHoldMsBox.Text, _settings.CombatWasdHoldMs, 40, 2000);
        _settings.CombatNearbyMonsterRadius = ParseClampedDouble(CombatNearbyRadiusBox.Text, _settings.CombatNearbyMonsterRadius, 100, 3000);
        _settings.CombatDangerMonsterCount = (int)ParseClampedDouble(CombatDangerCountBox.Text, _settings.CombatDangerMonsterCount, 1, 100);
        _settings.CombatAbilityCooldownMs = (int)ParseClampedDouble(CombatAbilityCooldownBox.Text, _settings.CombatAbilityCooldownMs, 20, 5000);
        _settings.CombatAbilityDelayMs = (int)ParseClampedDouble(CombatAbilityDelayBox.Text, _settings.CombatAbilityDelayMs, 0, 2000);
        _settings.CombatComboCooldownMs = (int)ParseClampedDouble(CombatComboCooldownBox.Text, _settings.CombatComboCooldownMs, 50, 10000);
        _settings.CombatUniqueComboRecastMs = (int)ParseClampedDouble(CombatUniqueComboRecastBox.Text, _settings.CombatUniqueComboRecastMs, 250, 30000);
        _settings.CombatTargetClickCooldownMs = (int)ParseClampedDouble(CombatTargetClickCooldownBox.Text, _settings.CombatTargetClickCooldownMs, 20, 5000);
        _settings.CombatFlaskCooldownMs = (int)ParseClampedDouble(CombatFlaskCooldownBox.Text, _settings.CombatFlaskCooldownMs, 100, 10000);
        _settings.CombatLootRange = ParseClampedDouble(CombatLootRangeBox.Text, _settings.CombatLootRange, 80, 1200);
        _settings.CombatLootCooldownMs = (int)ParseClampedDouble(CombatLootCooldownBox.Text, _settings.CombatLootCooldownMs, 20, 5000);
        _settings.CombatLootLabelXOffsetPixels = ParseClampedDouble(CombatLootLabelXOffsetBox.Text, _settings.CombatLootLabelXOffsetPixels, -240, 240);
        _settings.CombatLootLabelYOffsetPixels = ParseClampedDouble(CombatLootLabelOffsetBox.Text, _settings.CombatLootLabelYOffsetPixels, -240, 120);
        _settings.CombatLootLabelSearchPixels = ParseClampedDouble(CombatLootLabelSearchBox.Text, _settings.CombatLootLabelSearchPixels, 4, 120);
        _settings.CombatLootSafeMonsterRadius = ParseClampedDouble(CombatLootSafeMonsterRadiusBox.Text, _settings.CombatLootSafeMonsterRadius, 0, 3000);
        _settings.CombatLootMaxAttemptsPerItem = (int)ParseClampedDouble(CombatLootMaxAttemptsBox.Text, _settings.CombatLootMaxAttemptsPerItem, 1, 20);
        _settings.CombatEncounterInteractRange = ParseClampedDouble(CombatEncounterRangeBox.Text, _settings.CombatEncounterInteractRange, 80, 1500);
        _settings.CombatEncounterEnterRange = ParseClampedDouble(CombatEncounterEnterRangeBox.Text, _settings.CombatEncounterEnterRange, 5, 250);
        _settings.CombatEssenceClickCount = (int)ParseClampedDouble(CombatEssenceClickCountBox.Text, _settings.CombatEssenceClickCount, 1, 12);
        _settings.CombatExploreStepWorldUnits = ParseClampedDouble(CombatExploreStepBox.Text, _settings.CombatExploreStepWorldUnits, 100, 4000);
        _settings.CombatExploreDirectionMs = (int)ParseClampedDouble(CombatExploreDirectionMsBox.Text, _settings.CombatExploreDirectionMs, 250, 15000);
        _settings.CombatKiteEffectiveHealthPercent = (int)ParseClampedDouble(CombatKiteThresholdBox.Text, _settings.CombatKiteEffectiveHealthPercent, 1, 100);
        _settings.CombatKiteCooldownMs = (int)ParseClampedDouble(CombatKiteCooldownBox.Text, _settings.CombatKiteCooldownMs, 100, 10000);

        if (int.TryParse(MinHealthBox.Text, out int minHealth))
        {
            _settings.MinHealthPercent = Math.Clamp(minHealth, 1, 100);
        }

        if (int.TryParse(MinManaBox.Text, out int minMana))
        {
            _settings.MinManaPercent = Math.Clamp(minMana, 1, 100);
        }

        _settings.CombatEnabled = EnableCombat.IsChecked == true;
        _combatBot.UpdateConfig(ToCombatConfig(_settings));
        if (!oldHotkey.Equals(_settings.BotToggleHotkey, StringComparison.OrdinalIgnoreCase))
        {
            RegisterBotHotkey();
        }

        if (saveToDisk)
        {
            _settingsService.Save(_settings);
        }
    }

    private void BindTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || KeyToToken(e) is not { } token)
        {
            return;
        }

        textBox.Text = token;
        textBox.CaretIndex = textBox.Text.Length;
        e.Handled = true;
        ApplyRuntimeSettingsFromUi(saveToDisk: false);
    }

    private void ScanSkillLoadoutClick(object sender, RoutedEventArgs e)
    {
        _lastSkillLoadout = _skillLoadoutReader.Read();
        if (!string.IsNullOrWhiteSpace(_lastSkillLoadout.Error))
        {
            SkillLoadoutPreviewBox.Text = _lastSkillLoadout.Error;
            Log($"Skill bind scan failed: {_lastSkillLoadout.Error}", memory: true);
            return;
        }

        string preview = string.Join(Environment.NewLine, _lastSkillLoadout.Binds
            .Take(24)
            .Select(bind => $"{bind.Action} = {bind.Key}"));
        SkillLoadoutPreviewBox.Text = $"{_lastSkillLoadout.ConfigPath}{Environment.NewLine}{preview}";
        Log($"Skill bind scan found {_lastSkillLoadout.Binds.Count} bind(s).");
    }

    private void ApplyScannedSkillKeysClick(object sender, RoutedEventArgs e)
    {
        _lastSkillLoadout ??= _skillLoadoutReader.Read();
        string[] keys = _lastSkillLoadout.Binds
            .Where(bind => bind.Action.StartsWith("use_bound_skill", StringComparison.OrdinalIgnoreCase))
            .Select(bind => bind.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length == 0)
        {
            Log("No scanned bound skill keys to apply.", memory: true);
            return;
        }

        CombatAbilityKeysBox.Text = string.Join(",", keys);
        ApplyCombatSettingsFromUi(saveToDisk: true);
        Log($"Applied scanned ability keys: {CombatAbilityKeysBox.Text}");
    }

    private void BindTextBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox || MouseButtonToToken(e) is not { } token)
        {
            return;
        }

        if (!textBox.IsKeyboardFocusWithin)
        {
            textBox.Focus();
            textBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (textBox.Tag is string tag && tag.Equals("List", StringComparison.OrdinalIgnoreCase))
        {
            string[] current = textBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            textBox.Text = string.Join(",", current.Append(token).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            textBox.Text = token;
        }

        textBox.CaretIndex = textBox.Text.Length;
        e.Handled = true;
        ApplyRuntimeSettingsFromUi(saveToDisk: false);
    }

    private void BindListTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || KeyToToken(e) is not { } token)
        {
            return;
        }

        if (e.Key is Key.Back or Key.Delete)
        {
            textBox.Clear();
        }
        else
        {
            string[] current = textBox.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            textBox.Text = string.Join(",", current.Append(token).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        textBox.CaretIndex = textBox.Text.Length;
        e.Handled = true;
        ApplyCombatSettingsFromUi(saveToDisk: false);
    }

    private static string? KeyToToken(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete or Key.Tab or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
        {
            return key is Key.Back or Key.Delete ? string.Empty : null;
        }

        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString().ToUpperInvariant();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return ((int)(key - Key.NumPad0)).ToString();
        }

        if (key is Key.OemMinus or Key.Subtract)
        {
            return "-";
        }

        if (key is >= Key.F1 and <= Key.F12)
        {
            return key.ToString().ToUpperInvariant();
        }

        return key switch
        {
            Key.Space => "SPACE",
            Key.Enter => "ENTER",
            Key.Escape => "ESC",
            _ => null
        };
    }

    private static string? MouseButtonToToken(MouseButtonEventArgs e)
    {
        return e.ChangedButton switch
        {
            MouseButton.Left => "LMB",
            MouseButton.Right => "RMB",
            _ => null
        };
    }

    private static bool TryHotkeyTokenToVirtualKey(string token, out int virtualKey)
    {
        virtualKey = 0;
        token = token.Trim();
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z')
            {
                virtualKey = c;
                return true;
            }

            if (c == '-')
            {
                virtualKey = 0xBD;
                return true;
            }
        }

        string normalized = token.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        if (normalized.StartsWith("F", StringComparison.Ordinal) &&
            int.TryParse(normalized[1..], out int functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            virtualKey = 0x70 + functionKey - 1;
            return true;
        }

        virtualKey = normalized switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ENTER" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "-" or "OEMMINUS" or "MINUS" => 0xBD,
            "NUM-" or "NUMPADSUBTRACT" => 0x6D,
            _ => 0
        };

        return virtualKey != 0;
    }

    private void SaveMapRunnerConfigClick(object sender, RoutedEventArgs e)
    {
        ApplyMapRunnerSettingsFromUi(saveToDisk: true);
        Log($"Map runner configuration saved. Active input={_settings.MapRunnerActiveInputEnabled}, interact={_settings.MapRunnerInteractKey}.");
    }

    private void ApplyMapRunnerSettingsFromUi(bool saveToDisk)
    {
        if (!IsLoaded || _applyingSettingsToUi)
        {
            return;
        }

        _settings.MapRunnerEnabled = EnableMapRunner.IsChecked == true;
        _settings.MapRunnerActiveInputEnabled = MapRunnerActiveInputBox.IsChecked == true;
        _settings.MapRunnerInteractKey = MapRunnerInteractKeyBox.Text.Trim();
        _settings.MapRunnerInteractRange = ParseClampedDouble(MapRunnerInteractRangeBox.Text, _settings.MapRunnerInteractRange, 50, 2000);
        _settings.MapRunnerInteractCooldownMs = (int)ParseClampedDouble(MapRunnerInteractCooldownBox.Text, _settings.MapRunnerInteractCooldownMs, 250, 10000);
        _mapRunner.UpdateConfig(ToMapRunnerConfig(_settings));
        if (saveToDisk)
        {
            _settingsService.Save(_settings);
        }
    }

    private void ReloadSettingsClick(object sender, RoutedEventArgs e)
    {
        _settings = _settingsService.LoadOrCreate();
        _mapRunner.UpdateConfig(ToMapRunnerConfig(_settings));
        _combatBot.UpdateConfig(ToCombatConfig(_settings));
        _autoMapper.UpdateConfig(ToAutoMapperConfig(_settings));
        ApplySettingsToUi();
        Log("Settings reloaded.");
    }

    private void UpdateDisconnectedState()
    {
        StatusGame.Text = "Not Detected";
        StatusGame.Foreground = FindBrush("DangerBrush");
        StatusMemory.Text = "Disconnected";
        StatusMemory.Foreground = FindBrush("DangerBrush");
        SidebarStatusText.Text = "Disconnected";
        SidebarStatusText.Foreground = FindBrush("DangerBrush");
        UpdateBotState();
    }

    private void UpdateBotState()
    {
        if (_autoMapper.IsRunning)
        {
            StatusBot.Text = "AutoStart";
            StatusBot.Foreground = FindBrush("AccentBrush");
            EnsureBotStatsOverlay();
            return;
        }

        if (_mapRunner.IsRunning)
        {
            StatusBot.Text = "Map Monitor";
            StatusBot.Foreground = FindBrush("AccentBlueBrush");
            EnsureBotStatsOverlay();
            return;
        }

        if (_combatBot.IsRunning)
        {
            StatusBot.Text = "Combat Monitor";
            StatusBot.Foreground = FindBrush("AccentBrush");
            EnsureBotStatsOverlay();
            return;
        }

        StatusBot.Text = "Inactive";
        StatusBot.Foreground = FindBrush("MutedTextBrush");
        _botStatsOverlayWindow?.Hide();
    }

    private void SetCheckBoxSilently(CheckBox checkBox, bool value)
    {
        bool wasApplying = _applyingSettingsToUi;
        _applyingSettingsToUi = true;
        checkBox.IsChecked = value;
        _applyingSettingsToUi = wasApplying;
    }

    private void BuildProtectedInventoryGrid()
    {
        InventorySafeGrid.Children.Clear();
        for (int cell = 0; cell < 60; cell++)
        {
            int captured = cell;
            System.Windows.Controls.Primitives.ToggleButton button = new()
            {
                Content = $"{(cell % 12) + 1}:{(cell / 12) + 1}",
                Margin = new Thickness(2),
                MinHeight = 34,
                Background = FindBrush("PanelAltBrush"),
                Foreground = FindBrush("MutedTextBrush"),
                BorderBrush = new SolidColorBrush(Color.FromRgb(65, 70, 80)),
                ToolTip = "Click to protect/unprotect this inventory cell"
            };
            button.Checked += (_, _) => SetProtectedInventoryCell(captured, true);
            button.Unchecked += (_, _) => SetProtectedInventoryCell(captured, false);
            InventorySafeGrid.Children.Add(button);
        }

        RefreshProtectedInventoryGrid();
    }

    private void InitializeTabletChoices()
    {
        foreach (ComboBox comboBox in new[] { TabletSlot1Box, TabletSlot2Box, TabletSlot3Box })
        {
            comboBox.Items.Clear();
            foreach (string tablet in TabletChoices)
            {
                comboBox.Items.Add(tablet);
            }

            comboBox.SelectedIndex = 0;
        }
    }

    private void SelectTabletSlots(IReadOnlyList<string> tablets)
    {
        _applyingSettingsToUi = true;
        SelectTabletSlot(TabletSlot1Box, tablets.Count > 0 ? tablets[0] : string.Empty);
        SelectTabletSlot(TabletSlot2Box, tablets.Count > 1 ? tablets[1] : string.Empty);
        SelectTabletSlot(TabletSlot3Box, tablets.Count > 2 ? tablets[2] : string.Empty);
        _applyingSettingsToUi = false;
    }

    private static void SelectTabletSlot(ComboBox comboBox, string value)
    {
        for (int i = 0; i < comboBox.Items.Count; i++)
        {
            if (string.Equals(comboBox.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private string[] SelectedTabletSlots()
    {
        return new[] { TabletSlot1Box, TabletSlot2Box, TabletSlot3Box }
            .Select(comboBox => comboBox.SelectedItem?.ToString() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private void RefreshProtectedInventoryGrid()
    {
        if (InventorySafeGrid is null)
        {
            return;
        }

        HashSet<int> protectedCells = [.. _settings.ProtectedInventoryCells];
        _applyingSettingsToUi = true;
        for (int i = 0; i < InventorySafeGrid.Children.Count; i++)
        {
            if (InventorySafeGrid.Children[i] is not System.Windows.Controls.Primitives.ToggleButton button)
            {
                continue;
            }

            bool locked = protectedCells.Contains(i);
            button.IsChecked = locked;
            button.Background = locked ? new SolidColorBrush(Color.FromRgb(75, 49, 30)) : FindBrush("PanelAltBrush");
            button.Foreground = locked ? FindBrush("AccentBrush") : FindBrush("MutedTextBrush");
        }

        _applyingSettingsToUi = false;
        ProtectedCellsText.Text = $"Protected cells: {protectedCells.Count}/60";
    }

    private void SetProtectedInventoryCell(int cell, bool isProtected)
    {
        if (_applyingSettingsToUi)
        {
            return;
        }

        HashSet<int> cells = [.. _settings.ProtectedInventoryCells];
        if (isProtected)
        {
            cells.Add(cell);
        }
        else
        {
            cells.Remove(cell);
        }

        _settings.ProtectedInventoryCells = [.. cells.Order()];
        _settingsService.Save(_settings);
        _autoMapper.UpdateConfig(ToAutoMapperConfig(_settings));
        RefreshProtectedInventoryGrid();
        Log($"Protected inventory cells updated: {_settings.ProtectedInventoryCells.Length}/60.");
    }

    private void SelectAllInventoryCellsClick(object sender, RoutedEventArgs e)
    {
        _settings.ProtectedInventoryCells = Enumerable.Range(0, 60).ToArray();
        _settingsService.Save(_settings);
        _autoMapper.UpdateConfig(ToAutoMapperConfig(_settings));
        RefreshProtectedInventoryGrid();
        Log("All inventory cells protected.");
    }

    private void DeselectAllInventoryCellsClick(object sender, RoutedEventArgs e)
    {
        _settings.ProtectedInventoryCells = [];
        _settingsService.Save(_settings);
        _autoMapper.UpdateConfig(ToAutoMapperConfig(_settings));
        RefreshProtectedInventoryGrid();
        Log("All inventory cells unprotected.");
    }

    private SolidColorBrush FindBrush(string key)
    {
        return (SolidColorBrush)FindResource(key);
    }

    private static double ParseClampedDouble(string text, double fallback, double min, double max)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            value = fallback;
        }

        return Math.Clamp(value, min, max);
    }

    private static CombatConfig ToCombatConfig(AppSettings settings)
    {
        return new CombatConfig
        {
            ProcessName = settings.ProcessName,
            AttackSkillKey = settings.AttackSkillKey,
            AbilityKeys = settings.CombatAbilityKeys.Length == 0 ? [settings.AttackSkillKey] : settings.CombatAbilityKeys,
            RareComboKeys = settings.CombatRareComboKeys,
            UniqueComboKeys = settings.CombatUniqueComboKeys,
            FlaskKeys = settings.FlaskKeys,
            HealthFlaskKeys = settings.HealthFlaskKeys,
            ManaFlaskKeys = settings.ManaFlaskKeys,
            LootKey = settings.CombatLootKey,
            EncounterInteractKey = settings.CombatEncounterInteractKey,
            MinHealthPercent = settings.MinHealthPercent,
            MinManaPercent = settings.MinManaPercent,
            CombatRange = settings.CombatRange,
            CastRange = settings.CombatCastRange,
            EncounterInteractRange = settings.CombatEncounterInteractRange,
            EncounterEnterRange = settings.CombatEncounterEnterRange,
            NearbyMonsterRadius = settings.CombatNearbyMonsterRadius,
            PathClearMonsterRadius = Math.Max(settings.CombatNearbyMonsterRadius, settings.CombatCastRange * 1.8),
            DangerMonsterCount = settings.CombatDangerMonsterCount,
            FinishMonsterCount = settings.CombatFinishMonsterCount,
            RequireBossKillBeforeFinish = settings.RequireBossKillBeforeReturn ||
                                          settings.MappingPreset.Equals("BossRush", StringComparison.OrdinalIgnoreCase),
            PrioritizeBossTargets = settings.MappingPreset.Equals("BossRush", StringComparison.OrdinalIgnoreCase),
            UniqueComboRepeatEnabled = settings.CombatUniqueComboRepeatEnabled,
            ActiveInputEnabled = settings.CombatActiveInputEnabled,
            AutoMoveEnabled = settings.CombatAutoMoveEnabled,
            AutoAttackEnabled = settings.CombatAutoAttackEnabled,
            PressAllAbilities = settings.CombatPressAllAbilities,
            AutoLootEnabled = settings.CombatAutoLootEnabled,
            LootCurrencyEnabled = settings.CombatLootCurrencyEnabled,
            LootGearEnabled = settings.CombatLootGearEnabled,
            LootOtherEnabled = settings.CombatLootOtherEnabled,
            TargetEncounters = settings.CombatTargetEncounters,
            ClickTargetBeforeAttack = settings.CombatClickTargetBeforeAttack,
            AvoidCastingThroughWalls = true,
            ProjectMovementToScreen = settings.CombatProjectMovementToScreen,
            InvertInputX = settings.CombatInvertInputX,
            InvertInputY = settings.CombatInvertInputY,
            MovementMode = settings.CombatMovementMode,
            MoveUpKey = settings.CombatMoveUpKey,
            MoveLeftKey = settings.CombatMoveLeftKey,
            MoveDownKey = settings.CombatMoveDownKey,
            MoveRightKey = settings.CombatMoveRightKey,
            WasdHoldMs = settings.CombatWasdHoldMs,
            AbilityCooldownMs = settings.CombatAbilityCooldownMs,
            AbilityDelayMs = settings.CombatAbilityDelayMs,
            ComboCooldownMs = settings.CombatComboCooldownMs,
            UniqueComboRecastMs = settings.CombatUniqueComboRecastMs,
            TargetClickCooldownMs = settings.CombatTargetClickCooldownMs,
            AttackCooldownMs = settings.CombatAttackCooldownMs,
            FlaskCooldownMs = settings.CombatFlaskCooldownMs,
            FlaskInputDelayMs = settings.CombatFlaskInputDelayMs,
            KitingEnabled = settings.CombatKitingEnabled,
            KiteEffectiveHealthPercent = settings.CombatKiteEffectiveHealthPercent,
            KiteCooldownMs = settings.CombatKiteCooldownMs,
            KiteMoveHoldMs = settings.CombatKiteMoveHoldMs,
            KiteDodgeKey = settings.CombatDodgeKey,
            ProximityDamageRange = 155,
            UnproductiveTargetTimeoutMs = settings.CombatUnproductiveTargetTimeoutMs,
            UnproductiveTargetIgnoreMs = settings.CombatUnproductiveTargetIgnoreMs,
            MoveCooldownMs = Math.Clamp(settings.CombatMoveCooldownMs, 60, 220),
            MoveClickPixels = settings.CombatMoveClickPixels,
            AttackWorldUnitsPerPixel = settings.CombatAttackWorldUnitsPerPixel,
            AttackClickMaxPixels = settings.CombatAttackClickMaxPixels,
            LootRange = settings.CombatLootRange,
            LootCooldownMs = settings.CombatLootCooldownMs,
            LootLabelXOffsetPixels = settings.CombatLootLabelXOffsetPixels,
            LootLabelYOffsetPixels = settings.CombatLootLabelYOffsetPixels,
            LootLabelSearchPixels = settings.CombatLootLabelSearchPixels,
            LootLabelClickAttempts = settings.CombatLootLabelClickAttempts,
            LootMaxAttemptsPerItem = settings.CombatLootMaxAttemptsPerItem,
            LootFailedBlacklistSeconds = settings.CombatLootFailedBlacklistSeconds,
            LootSafeMonsterRadius = settings.CombatLootSafeMonsterRadius,
            ExploreWhenNoTarget = settings.CombatExploreWhenNoTarget,
            ExploreStepWorldUnits = settings.CombatExploreStepWorldUnits,
            ExploreDirectionMs = settings.CombatExploreDirectionMs,
            EncounterInteractCooldownMs = settings.CombatEncounterInteractCooldownMs,
            EssenceClickCount = settings.CombatEssenceClickCount,
            EncounterMultiClickDelayMs = settings.CombatEncounterMultiClickDelayMs,
            Enabled = settings.CombatEnabled
        };
    }

    private static MapRunnerConfig ToMapRunnerConfig(AppSettings settings)
    {
        return new MapRunnerConfig
        {
            ProcessName = settings.ProcessName,
            ActiveInputEnabled = settings.MapRunnerActiveInputEnabled,
            InteractKey = settings.MapRunnerInteractKey,
            InteractRange = settings.MapRunnerInteractRange,
            InteractCooldownMs = settings.MapRunnerInteractCooldownMs
        };
    }

    private static AutoMapperConfig ToAutoMapperConfig(AppSettings settings)
    {
        return new AutoMapperConfig
        {
            ProcessName = settings.ProcessName,
            Enabled = settings.AutoMapperEnabled,
            StrategyPreset = settings.MappingPreset,
            ActiveInputEnabled = settings.AutoMapperActiveInputEnabled,
            DryRun = false,
            RunsTarget = settings.AutoMapperRunsTarget,
            CurrentRun = settings.AutoMapperCurrentRun,
            FinishMonsterCount = settings.CombatFinishMonsterCount,
            ReturnToHideoutKey = settings.ReturnToHideoutKey,
            OpenInventoryKey = settings.OpenInventoryKey,
            PortalKey = settings.PortalKey,
            WaystoneStashTabName = settings.WaystoneStashTabName,
            TabletStashTabName = settings.TabletStashTabName,
            CurrencyStashTabName = settings.CurrencyStashTabName,
            WaystoneTakeCount = settings.WaystoneTakeCount,
            MaxWaystoneTier = settings.MaxWaystoneTier,
            AllowUnknownWaystoneTier = settings.AllowUnknownWaystoneTier,
            TabletTakeCount = settings.TabletTakeCount,
            PreferredTabletNames = settings.PreferredTabletNames,
            AlchemyWaystones = settings.AlchemyWaystones,
            AlchemyTakeCount = settings.AlchemyTakeCount,
            MapBlacklist = settings.AtlasMapBlacklist,
            AvoidDecoratedAtlasNodes = settings.AvoidDecoratedAtlasNodes,
            AtlasDecorationRadiusPixels = settings.AtlasDecorationRadiusPixels,
            ProtectedInventoryCells = settings.ProtectedInventoryCells,
            LastAtlasNodeX = settings.LastAtlasNodeX,
            LastAtlasNodeY = settings.LastAtlasNodeY,
            InventoryGridLeft = settings.InventoryGridLeft,
            InventoryGridTop = settings.InventoryGridTop,
            InventoryGridWidth = settings.InventoryGridWidth,
            InventoryGridHeight = settings.InventoryGridHeight,
            RequireBossKillBeforeReturn = settings.RequireBossKillBeforeReturn ||
                                          settings.MappingPreset.Equals("BossRush", StringComparison.OrdinalIgnoreCase)
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
