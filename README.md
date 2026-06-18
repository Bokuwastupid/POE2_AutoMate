# POE2 AutoMate

Standalone WPF desktop project for a Path of Exile 2 assistant-style control panel.

## Projects

- `POE2_AutoMate` - WPF executable with dashboard, memory/dev, map runner, combat/binds, and settings views.
- `POE2_AutoMate.Core` - process attachment, memory reading primitives, game models, and signatures placeholder.
- `POE2_AutoMate.Automation` - invasive automation state machines and configuration objects.
- `POE2_SignatureFinder` - console utility under `tools/` for scanning a local process module for byte signatures.

## Current Memory Layer

- Default process name is `PathOfExileSteam`.
- The WPF Dev tab can attach, scan POE2Radar-style AOB patterns, and start a live read-only snapshot loop.
- The current build resolves `GameState -> InGameState -> AreaInstance -> LocalPlayer` when the stored signatures still match the game build.
- `POE2_SignatureFinder` also probes that chain. On the current tested client it found `GameState global slot`, missed the older direct `InGameState` AOB, then successfully resolved `CurrentStatePtr[0] -> InGameState -> AreaInstance -> LocalPlayer`.
- Health/mana and position use component-name lookup (`Life`, `Render`) in the same style as POE2Radar, so the later map/overlay layer can reuse the same snapshot pipeline.
- The app manifest requests administrator rights because reading another process memory commonly needs elevation.

The map/overlay is not implemented yet, but the core is now shaped for it: `GameSnapshot` carries `InGameState`, `AreaInstance`, `LocalPlayer`, and player vitals/position. The next layer can add terrain/entity reads and render them in an overlay window.

## Build

This workspace currently has .NET SDK 10 installed, so the project targets `net10.0-windows`.

```powershell
& "C:\Program Files\dotnet\dotnet.exe" restore
& "C:\Program Files\dotnet\dotnet.exe" build --configuration Release
```

Build only the desktop app:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build .\src\POE2_AutoMate\POE2_AutoMate.csproj --configuration Release
```

Build only the signature finder:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build .\tools\POE2_SignatureFinder\POE2_SignatureFinder.csproj --configuration Release
```

## Run

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --project .\src\POE2_AutoMate
```

Run the signature finder:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --project .\tools\POE2_SignatureFinder -- PathOfExileSteam
```

The executable is emitted under:

`src\POE2_AutoMate\bin\Release\net10.0-windows\`

On first launch the app creates `config/settings.json`.
