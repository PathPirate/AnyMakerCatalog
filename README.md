
The catalog reads local creations from the current user's `%APPDATA%\Anymaker\creations` and Workshop creations from the installed Steam library. It supports inventory items, components, S01/S02/S03 keycards, and creation browsing and placement. Multiplayer keycard behavior has not been independently confirmed. The reader must have a matching `key` string; a reader without a key does not accept those cards.

Logs: `%LOCALAPPDATA%\AnyMakerCatalogDll\catalog.log`. To build a diagnostic DLL, run `./build.ps1 -Diagnostics`.
