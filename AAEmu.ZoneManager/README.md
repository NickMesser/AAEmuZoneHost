# AAEmu Zone Manager

Windows desktop control surface for the process-isolated `AAEmu.ZoneHost.exe`. The original
`dedicatedserver.exe` bootstrap is not executed or required.

## Features

- Loads every zone key and map name from the bundled `AAEmu.ZoneManager/BundledData/zone_map_names.txt`, deployed beside the executable as `Data/zone_map_names.txt`.
- Reads `zones`, `zone_groups`, and `map_resources` from the decrypted client database.
- Loads the selected client's raw map textures from an external extracted map tree. The selected database's `map_resources.folder_name` and `world_over_image_path` fields choose the files; no client map images are stored in the repository or application package.
- Opens the selected zone group's own map, colors every sibling zone partition, and zooms to the selected partition.
- Uses the same 64-meter `world.xml` sector assignments that populate `WorldManager.ZoneKeyByRegions`, so irregular boundaries such as Gweonid's three zones are not approximated.
- Exposes the verified native switches and the AAEmu zone launch CVars.
- Launches, stops, and restarts multiple zones independently.
- Discovers the active Zone build's native console commands and variables, shows their native help text, and sends selected commands to a running zone.
- Polls World's read-only status API, shows each native Zone's real `Connected`/`Joined`/`ZoneLoaded` handshake state, and lists online players with their current zone.
- Tails the native ArcheAge log into one expandable live view and keeps persistent files per zone.
- Stores settings, logs, builds, and runtime state outside the repository by default under `C:\AAEmuRuntime\ZoneManager`.

## Native launch evidence

Complete Ghidra analysis of the CN 10.0.2.13 `dedicatedserver.exe` application function
(`FUN_140001000` at `0x140001000`) confirmed that it:

1. initializes `xlcommon`, selects the ArcheAge working/save directories, and initializes the exception handler;
2. preserves and forwards the complete process command line, including the first `devmode.cfg` line when applicable;
3. appends `+zone w_gweonid_forest_1` only when `+zone` is absent and recognizes `-fulldump`;
4. loads `x2game-dev_dedicate.dll` and resolves `CreateGameStartup`;
5. supplies the verified 0xA00-byte startup structure, including `lastWriteFileTime + fileSize` for `game_pak` at offset `0x9A0`; and
6. runs native startup initialization, execution, shutdown, release, exception cleanup, and DLL unload in that order.

The native AAEmu host reproduces that bootstrap, loads `x2game-dev_dedicate.dll` directly, and
supplies `+zone` and `+sv_map` with the selected `zones.name`. It also supplies both native World
endpoint aliases (`world_serveraddr`/`world_serverport` and `world_ip`/`world_port`). World registers
the server with `zones.zone_key` from `ZWJoin.id`. The replacement intentionally uses the manager's
explicit external save directory instead of the original `Documents` junction and adds a private hidden
console for command injection. All startup-interface and DLL resources are released on failure paths.

## Build

Build the dependency-free native host from the **x64 Native Tools Command Prompt for Visual Studio**.
The complete prerequisite, build, deployment, verification, and troubleshooting guide is in
[`AAEmu.ZoneHost.Native/README.md`](../AAEmu.ZoneHost.Native/README.md#build-aaemuzonehostexe).
Keep Cargo output outside the repository and copy only the final executable beside x2game:

```powershell
$env:CARGO_TARGET_DIR = 'C:\AAEmuRuntime\ZoneManager\NativeHostRust'
cargo +stable-x86_64-pc-windows-msvc build `
  --locked `
  --manifest-path .\AAEmu.ZoneHost.Native\Cargo.toml `
  --release `
  --target x86_64-pc-windows-msvc

Copy-Item `
  C:\AAEmuRuntime\ZoneManager\NativeHostRust\x86_64-pc-windows-msvc\release\aaemu-zone-host.exe `
  C:\AA\Bin64\AAEmu.ZoneHost.exe
```

Do not add a version-specific `LIB` value manually. If Cargo cannot find `link.exe`, `msvcrt.lib`, or
`kernel32.lib`, install/repair the Visual C++ workload and Windows SDK, then reopen the x64 Native Tools
prompt before rebuilding.

Use the repository's .NET 10 SDK and route artifacts outside the working tree:

```powershell
dotnet build .\AAEmu.ZoneManager\AAEmu.ZoneManager.csproj `
  --artifacts-path C:\AAEmuRuntime\ZoneManager\Artifacts
```

For a directly runnable Windows distribution that includes the runtime:

```powershell
dotnet publish .\AAEmu.ZoneManager\AAEmu.ZoneManager.csproj `
  -c Release -r win-x64 --self-contained true `
  --artifacts-path C:\AAEmuRuntime\ZoneManager\PublishArtifacts
```

The publish output does not contain client map images. Use **Set map assets** to select the extracted
client `map` directory that contains `map_resources`.

## Game database

The launcher does not contain a default game-database path. On first launch, the user must select a
decrypted game database (`.sqlite3`, `.sqlite`, or `.db`). The selected location is retained in the
user's external `C:\AAEmuRuntime\ZoneManager\settings.json` file and can be changed with **Set database**.

## Runtime paths

The initial defaults match the current local AAEmu development layout:

- AAEmu native host: `C:\AA\Bin64\AAEmu.ZoneHost.exe`;
- native zone DLL and working directory: `C:\AA\Bin64\x2game-dev_dedicate.dll`;
- decrypted map/game metadata: selected by the user on first launch;
- extracted client maps: selected with **Set map assets**, defaulting to `C:\AAEmuRuntime\ZoneManager\MapAssets\Raw\map`;
- World Zone endpoint: `127.0.0.1:1240`;
- World status API: `127.0.0.1:1280`; and
- zone logs/settings: `C:\AAEmuRuntime\ZoneManager`.

Paths and flags can be changed in the UI. Additional CryEngine CVars can be entered in **Extra arguments** and are passed through unchanged.

The map loader resolves each base texture as
`map_resources\<map_resources.folder_name>\<locale>\world.dds`. Parent overlay textures use the
database's exact `map_resources.world_over_image_path` reference, with the selected locale inserted
before the filename when that localized file exists. It falls back to `en_us` and then the unlocalized
asset, matching the extracted client layout. References that escape the selected map root are rejected.

The launcher and native zone host use the decrypted database directly. The default native argument is
`+db_location game/db/game_decrypted.sqlite3`; a `compact.sqlite3` hard-link is not required.

## Native console commands

Select a running zone and expand **Native console commands**. **Refresh native list** asks that Zone
host to generate CrySystem's own command and variable catalog, so the displayed names, types, current
values, scripts, and help text match the loaded native DLL instead of a maintained copy. Use the search
box to filter it, optionally include console variables, select a command to prefill it, and click **Run**.
Commands that look destructive require confirmation.

The generated native file is copied to
`C:\AAEmuRuntime\ZoneManager\CommandCatalog\consolecommandsandvars.txt`; it is runtime data and is not
stored in the repository. Console commands are sent as native Windows console input records because
the Zone's CrySystem console reads `ReadConsoleInputA` rather than redirected standard input.

## World status and players

The header polls `http://<world-ip>:<world-api-port>/api/world/zone-manager-status` every two seconds.
It reports whether World is reachable, the number of online players, and the number of native Zone
connections that completed `ZWZoneLoaded`. The selected Zone panel shows its exact World session state,
session ID, and registered unit count. The player table is sourced from World's live character registry
and displays each character's current `Transform.ZoneId` resolved to the server's zone name.

The API port defaults to `1280` and can be changed under **World connection**. This endpoint is
read-only; the Zone Manager does not query the character database to guess who is online.
