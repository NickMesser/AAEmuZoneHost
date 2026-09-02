# AAEmu Zone Host

A replacement zone-server bootstrap for ArcheAge 10.0.2.13, and the desktop application that
drives it.

ArcheAge ships `dedicatedserver.exe`, a thin native bootstrap whose only job is to load
`x2game-dev_dedicate.dll` and run one zone. This repository replaces that bootstrap and puts a
control surface on top of it, so zones can be launched, inspected, and stopped without touching a
command line.

| Project | What it is |
| --- | --- |
| [`AAEmu.ZoneHost.Native`](AAEmu.ZoneHost.Native/) | The bootstrap itself. A dependency-free Rust executable that reproduces the original startup ABI, loads the native zone DLL, and keeps one selected zone running. Builds to `AAEmu.ZoneHost.exe`. |
| [`AAEmu.ZoneManager`](AAEmu.ZoneManager/) | A WPF desktop application that launches and supervises zone hosts: zone selection on the real client map, native console-command discovery, live log tailing, and World handshake/player status. |

Neither project runs an ArcheAge server on its own. Both attach to an AAEmu World server, which
owns gameplay authority; the zone host provides the native simulation for one zone at a time.

## What the host actually replaces

The entire non-CRT bootstrap in the CN 10.0.2.13 `dedicatedserver.exe` is a single function,
`FUN_140001000`. `AAEmu.ZoneHost.Native` mirrors its observable startup ABI field for field: the
0xA00-byte startup structure, the preserved command line at offset `0x40`, the
`lastWriteFileTime + fileSize` value computed for `game_pak` at offset `0x9A0`, and the exact
startup/game vtable slot ordering. The per-field comparison table is in
[`AAEmu.ZoneHost.Native/README.md`](AAEmu.ZoneHost.Native/README.md#verified-native-parity).

Three things are deliberate additions rather than original behavior: a configurable DLL path, an
explicit external save directory in place of the original `Documents` junction, and a hidden
console the manager uses to inject commands.

## Requirements

Both projects target Windows x64 only. The native host calls the Windows API directly and the
manager is WPF.

- **Zone Manager** — .NET 10 SDK.
- **Zone Host** — Rust stable `x86_64-pc-windows-msvc`, plus Visual Studio 2022 (or Build Tools)
  with *Desktop development with C++* and a Windows 10/11 SDK. The GNU Rust target will not work.

An ArcheAge 10.0.2.13 installation supplies `x2game-dev_dedicate.dll`, the `game_pak`, and a
decrypted game database. None of that is distributed here.

## Build

```powershell
dotnet build .\AAEmu.ZoneHost.slnx -c Release
```

The native host builds from the **x64 Native Tools Command Prompt for Visual Studio**, with Cargo
output kept outside the working tree:

```powershell
$env:CARGO_TARGET_DIR = 'C:\AAEmuRuntime\ZoneHostBuild'
cargo +stable-x86_64-pc-windows-msvc build --locked `
  --manifest-path .\AAEmu.ZoneHost.Native\Cargo.toml `
  --release --target x86_64-pc-windows-msvc
```

Cargo names the binary `aaemu-zone-host.exe`; deploy it beside `x2game-dev_dedicate.dll` as
`AAEmu.ZoneHost.exe`, the name Zone Manager expects. `XlSetWorkingDir(true)` derives the game root
from the executable's own location, so running it out of Cargo's target directory makes native
startup look for `game`, `game_pak`, and the database in the wrong place.

Full prerequisites, deployment, hash verification, and a troubleshooting table for the usual
`link.exe` / `msvcrt.lib` / `kernel32.lib` failures are in
[`AAEmu.ZoneHost.Native/README.md`](AAEmu.ZoneHost.Native/README.md#build-aaemuzonehostexe).

## Run

1. Start MySQL, Login, and World. World must be listening on port `1240`.
2. Start `AAEmu.ZoneManager.exe`.
3. Point it at `AAEmu.ZoneHost.exe` and `x2game-dev_dedicate.dll`, then select a decrypted game
   database and the extracted client `map` directory.
4. Select a zone and click **Launch zone**.

Settings, per-zone logs, map assets, and the generated console-command catalog are written under
`C:\AAEmuRuntime\ZoneManager` by default — outside the working tree. Client map images are never
stored in the repository or the application package; the manager reads them from the extracted
client tree you select.

Per-feature detail, runtime paths, and the World status API contract are documented in
[`AAEmu.ZoneManager/README.md`](AAEmu.ZoneManager/README.md).

## License

GPL, matching upstream AAEmu. See [LICENSE](LICENSE).
