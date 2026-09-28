AnyMaker Catalog Mod
Inspired by Electron's posts on the AnyMaker Discord

An unofficial item and component catalog for AnyMaker.

Setup:
1. Unzip anywhere. Keep both executables and "_internal" together.
2. Set "game-path.txt" to the folder containing "game.exe". Example: E:\SteamLibrary\steamapps\common\Anymaker
3. Load your world, run "AnyMakerOverlay.exe", then open your inventory.

The startup window shows connection progress. Close it to cancel. Closing the game also closes the mod.

Using it:
Search Items or Components. Click an image to add an item. Hover to see its description.
Items go to carried storage (including pouches), then your hand, then the ground.

How it works:
"AnyMakerOverlay.exe" runs the catalog. "AnyMakerBridge.exe" uses Frida to hook the game's inventory and item functions. No installed game files are changed, but adding items changes your world. We inspected names, metadata, and native instructions in the ".gcl". That included disassembly.

Help:
If it doesn't connect, check "game-path.txt" and "catalog-diagnostic.log". Game updates can break the bridge. Antivirus may flag it because it hooks the game process.

About AI:
I have a programming background, so this isn't just vibe coded. AI helped build it; we tested and fixed it in game.
