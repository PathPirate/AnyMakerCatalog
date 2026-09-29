# AnyMaker Catalog Mod 1.0.8 — source

This archive contains the first-party source used for the 1.0.8 Windows release. It contains no compiled executables, bundled runtimes, game files, saves, logs, or personal `game-path.txt`.

## Files

- `AnyMakerOverlay/`: .NET 8 Windows Forms catalog, model previews, and description tooltip.
- `AnyMakerBridge/bridge.py`: local bridge and Frida hooks for inventory state and adding items.
- `AnyMakerBridge/AnyMakerBridge.spec`: PyInstaller build configuration.
- `tests/test_inventory_during_add.py`: regression check for inventory visibility during an Add request.
- `requirements-build.txt`: Python build dependencies.

## Build on Windows

Install the .NET 8 SDK and Python 3.12. From this directory in PowerShell:

```powershell
dotnet publish .\AnyMakerOverlay\AnyMakerOverlay.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o .\publish\overlay

py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r .\requirements-build.txt
Push-Location .\AnyMakerBridge
..\.venv\Scripts\python.exe -m PyInstaller --noconfirm .\AnyMakerBridge.spec
Pop-Location
```

The catalog executable is `publish/overlay/AnyMakerOverlay.exe`. The bridge build is `AnyMakerBridge/dist/AnyMakerBridge/`; keep its executable and `_internal` directory together. Put both executables and `_internal` in one folder for the mod. Create `game-path.txt` beside them containing the folder with your `game.exe`.

## What it accesses

The overlay reads the game's definitions, meshes, and font, and writes `catalog-diagnostic.log` beside the executable. It stores display settings in `%LOCALAPPDATA%\AnyMakerCatalog\settings.json`. Its Windows keyboard hook only handles Tab and Escape for inventory navigation.

The bridge reads the configured game's `game.gcl` and definitions, checks the game version and routine signatures, then uses Frida to observe the inventory UI and call the game's item routines. It listens only on `127.0.0.1` for requests from the overlay. The code makes no internet requests and does not edit installed game files. Adding an item does change the current game world.

This is an unofficial mod. The source is provided so people can inspect the behavior; the Python, Frida, PyInstaller, and .NET runtime sources are separate third-party projects.
