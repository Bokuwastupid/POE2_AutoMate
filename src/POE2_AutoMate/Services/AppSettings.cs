namespace POE2_AutoMate.Services;

public sealed class AppSettings
{
    public string ProcessName { get; set; } = "PathOfExileSteam";
    public string Theme { get; set; } = "Dark";
    public bool MapRunnerEnabled { get; set; }
    public bool CombatEnabled { get; set; }
    public bool AutoMapperEnabled { get; set; }
    public string MappingPreset { get; set; } = "FullMapClear";
    public bool AutoMapperActiveInputEnabled { get; set; }
    public bool AutoMapperDryRun { get; set; } = false;
    public int AutoMapperRunsTarget { get; set; } = 1;
    public int AutoMapperCurrentRun { get; set; }
    public bool RequireBossKillBeforeReturn { get; set; } = true;
    public string WaystoneStashTabName { get; set; } = "maps";
    public string TabletStashTabName { get; set; } = "maps";
    public string CurrencyStashTabName { get; set; } = "валюта";
    public int WaystoneTakeCount { get; set; } = 1;
    public int MaxWaystoneTier { get; set; } = 16;
    public bool AllowUnknownWaystoneTier { get; set; } = false;
    public int TabletTakeCount { get; set; }
    public string[] PreferredTabletNames { get; set; } = [];
    public bool AlchemyWaystones { get; set; } = true;
    public int AlchemyTakeCount { get; set; } = 1;
    public string ReturnToHideoutKey { get; set; } = "F5";
    public string OpenInventoryKey { get; set; } = "I";
    public string PortalKey { get; set; } = "T";
    public string[] AtlasMapBlacklist { get; set; } = [];
    public bool AvoidDecoratedAtlasNodes { get; set; } = true;
    public double AtlasDecorationRadiusPixels { get; set; } = 82;
    public string PoeNinjaLeague { get; set; } = "Runes of Aldur";
    public int LootPriceRefreshMinutes { get; set; } = 180;
    public int[] ProtectedInventoryCells { get; set; } = [];
    public float LastAtlasNodeX { get; set; }
    public float LastAtlasNodeY { get; set; }
    public double InventoryGridLeft { get; set; } = 0.03;
    public double InventoryGridTop { get; set; } = 0.655;
    public double InventoryGridWidth { get; set; } = 0.945;
    public double InventoryGridHeight { get; set; } = 0.288;
    public string BotToggleHotkey { get; set; } = "F8";
    public string AttackSkillKey { get; set; } = "1";
    public string[] FlaskKeys { get; set; } = ["2", "3", "4", "5"];
    public string[] HealthFlaskKeys { get; set; } = ["1"];
    public string[] ManaFlaskKeys { get; set; } = ["2"];
    public int MinHealthPercent { get; set; } = 50;
    public int MinManaPercent { get; set; } = 35;
    public bool MapRunnerActiveInputEnabled { get; set; }
    public string MapRunnerInteractKey { get; set; } = "F";
    public double MapRunnerInteractRange { get; set; } = 260;
    public int MapRunnerInteractCooldownMs { get; set; } = 1200;
    public bool CombatActiveInputEnabled { get; set; }
    public bool CombatAutoMoveEnabled { get; set; }
    public bool CombatAutoAttackEnabled { get; set; }
    public bool CombatPressAllAbilities { get; set; }
    public bool CombatAutoLootEnabled { get; set; }
    public bool CombatLootCurrencyEnabled { get; set; } = true;
    public bool CombatLootGearEnabled { get; set; } = true;
    public bool CombatLootOtherEnabled { get; set; }
    public bool CombatTargetEncounters { get; set; }
    public bool CombatUniqueComboRepeatEnabled { get; set; }
    public bool CombatClickTargetBeforeAttack { get; set; } = true;
    public bool CombatDebugOverlayEnabled { get; set; } = true;
    public bool CombatProjectMovementToScreen { get; set; } = true;
    public bool CombatInvertInputX { get; set; }
    public bool CombatInvertInputY { get; set; }
    public string CombatMovementMode { get; set; } = "MouseClick";
    public string CombatMoveUpKey { get; set; } = "W";
    public string CombatMoveLeftKey { get; set; } = "A";
    public string CombatMoveDownKey { get; set; } = "S";
    public string CombatMoveRightKey { get; set; } = "D";
    public string[] CombatAbilityKeys { get; set; } = ["1"];
    public string[] CombatRareComboKeys { get; set; } = [];
    public string[] CombatUniqueComboKeys { get; set; } = [];
    public string CombatLootKey { get; set; } = "F";
    public string CombatEncounterInteractKey { get; set; } = "F";
    public double CombatRange { get; set; } = 550;
    public double CombatCastRange { get; set; } = 420;
    public double CombatEncounterInteractRange { get; set; } = 260;
    public double CombatEncounterEnterRange { get; set; } = 45;
    public int CombatFinishMonsterCount { get; set; } = 10;
    public int CombatAbilityCooldownMs { get; set; } = 650;
    public int CombatAbilityDelayMs { get; set; } = 90;
    public int CombatComboCooldownMs { get; set; } = 2200;
    public int CombatUniqueComboRecastMs { get; set; } = 4000;
    public int CombatTargetClickCooldownMs { get; set; } = 650;
    public int CombatMoveCooldownMs { get; set; } = 160;
    public double CombatMoveClickPixels { get; set; } = 230;
    public int CombatWasdHoldMs { get; set; } = 260;
    public double CombatAttackWorldUnitsPerPixel { get; set; } = 12;
    public double CombatAttackClickMaxPixels { get; set; } = 430;
    public double CombatLootRange { get; set; } = 260;
    public int CombatLootCooldownMs { get; set; } = 220;
    public double CombatLootLabelXOffsetPixels { get; set; }
    public double CombatLootLabelYOffsetPixels { get; set; } = -54;
    public double CombatLootLabelSearchPixels { get; set; } = 34;
    public int CombatLootLabelClickAttempts { get; set; } = 6;
    public int CombatLootMaxAttemptsPerItem { get; set; } = 3;
    public double CombatLootFailedBlacklistSeconds { get; set; } = 120;
    public double CombatLootSafeMonsterRadius { get; set; } = 850;
    public bool CombatExploreWhenNoTarget { get; set; } = true;
    public double CombatExploreStepWorldUnits { get; set; } = 900;
    public int CombatExploreDirectionMs { get; set; } = 2500;
    public double CombatNearbyMonsterRadius { get; set; } = 650;
    public int CombatDangerMonsterCount { get; set; } = 6;
    public int CombatAttackCooldownMs { get; set; } = 900;
    public int CombatFlaskCooldownMs { get; set; } = 3200;
    public int CombatFlaskInputDelayMs { get; set; } = 20;
    public bool CombatKitingEnabled { get; set; } = true;
    public int CombatKiteEffectiveHealthPercent { get; set; } = 55;
    public int CombatKiteCooldownMs { get; set; } = 1400;
    public int CombatKiteMoveHoldMs { get; set; } = 130;
    public string CombatDodgeKey { get; set; } = "SPACE";
    public int CombatUnproductiveTargetTimeoutMs { get; set; } = 2600;
    public int CombatUnproductiveTargetIgnoreMs { get; set; } = 12000;
    public int CombatEncounterInteractCooldownMs { get; set; } = 1200;
    public int CombatEssenceClickCount { get; set; } = 4;
    public int CombatEncounterMultiClickDelayMs { get; set; } = 110;
    public bool OverlayRotate180 { get; set; } = true;
    public bool OverlayFollowGameWindow { get; set; } = true;
    public double OverlayWidth { get; set; } = 420;
    public double OverlayHeight { get; set; } = 420;
    public double OverlayWorldUnitsPerPixel { get; set; } = 12;
    public double OverlayOpacityPercent { get; set; } = 100;
    public bool OverlayFullMapView { get; set; }
    public bool OverlayShowTerrain { get; set; } = true;
    public bool OverlayShowMonsters { get; set; } = true;
    public bool OverlayShowChests { get; set; } = true;
    public bool OverlayShowTransitions { get; set; } = true;
    public bool OverlayShowNpcs { get; set; } = true;
    public bool OverlayShowOther { get; set; }
}
