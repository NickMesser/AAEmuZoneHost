# AAEmu Zone Host

`AAEmu.ZoneHost.exe` replaces the original `dedicatedserver.exe` bootstrap. It loads
`x2game-dev_dedicate.dll` and keeps one selected zone running. The preferred way to launch it is through
**AAEmu Zone Manager**, which supplies the native arguments, tracks the process, and captures its logs.

## Build `AAEmu.ZoneHost.exe`

The Rust project intentionally has no third-party crate dependencies. It still needs Microsoft's 64-bit
linker and Windows SDK because the host calls the native Windows API.

### Prerequisites

Install the following on the Windows machine doing the build:

1. [Rustup](https://rustup.rs/) with the stable 64-bit MSVC toolchain.
2. Visual Studio 2022 Community or Visual Studio Build Tools 2022 with **Desktop development with C++**.
3. In the Visual Studio Installer's individual components, verify that an MSVC x64/x86 build-tools
   component and a Windows 10 or Windows 11 SDK are selected.

Visual Studio 2019 Build Tools also works. Do not use the GNU Rust target for this executable.

Run these once from PowerShell to install and verify the Rust target:

```powershell
rustup toolchain install stable-x86_64-pc-windows-msvc
rustup target add x86_64-pc-windows-msvc --toolchain stable-x86_64-pc-windows-msvc
rustc +stable-x86_64-pc-windows-msvc --version
```

### Build from the 64-bit Visual Studio environment

1. Open **x64 Native Tools Command Prompt for VS 2022** from the Windows Start menu. Use the VS 2019
   version if that is the installed Build Tools version.
2. Type `powershell` and press Enter. The PowerShell process inherits the required compiler, linker, and
   Windows SDK paths.
3. Run the following commands, replacing the repository path if needed:

```powershell
Set-Location 'C:\path\to\AAEmu.ZoneHost'

# Cargo intermediates and the executable stay outside the repository.
$env:CARGO_TARGET_DIR = 'C:\AAEmuRuntime\ZoneHostBuild'

cargo +stable-x86_64-pc-windows-msvc build `
  --locked `
  --manifest-path .\AAEmu.ZoneHost.Native\Cargo.toml `
  --release `
  --target x86_64-pc-windows-msvc
```

The successful build prints `Finished release profile` and creates:

```text
C:\AAEmuRuntime\ZoneHostBuild\x86_64-pc-windows-msvc\release\aaemu-zone-host.exe
```

The Cargo binary is named `aaemu-zone-host.exe` because that is the `[[bin]]` name in `Cargo.toml`.
Zone Manager expects the deployed file to be named `AAEmu.ZoneHost.exe`; copying it under that name is
the final build step.

### Deploy the executable

Stop any running zone hosts before replacing the executable. The following keeps the previous binary
in the external runtime directory and then deploys the new one:

```powershell
$source = 'C:\AAEmuRuntime\ZoneHostBuild\x86_64-pc-windows-msvc\release\aaemu-zone-host.exe'
$destination = 'C:\AA\Bin64\AAEmu.ZoneHost.exe'
$backupDirectory = 'C:\AAEmuRuntime\ZoneHostBackup'

if (-not (Test-Path -LiteralPath $source)) {
    throw "Build output was not found: $source"
}

New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
if (Test-Path -LiteralPath $destination) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    Copy-Item -LiteralPath $destination -Destination "$backupDirectory\AAEmu.ZoneHost-$stamp.exe"
}

Copy-Item -LiteralPath $source -Destination $destination -Force
Get-FileHash -LiteralPath $source, $destination
```

The two SHA-256 hashes should match. An elevated PowerShell window may be required if the selected game
directory is protected. A Rust installation is not required on the machine that runs the resulting
executable.

The deployed executable must remain beside `x2game-dev_dedicate.dll`. Like the original
`dedicatedserver.exe`, `XlSetWorkingDir(true)` derives the ArcheAge game root from the executable's own
location; running the executable directly from Cargo's target directory will make native startup look
for `game`, `game_pak`, and the database beside that target directory.

### Common build problems

| Error or symptom | Cause and fix |
| --- | --- |
| `link.exe not found` | The Visual C++ workload is missing or the build is running in an ordinary terminal. Install **Desktop development with C++**, then use the x64 Native Tools prompt. |
| `LNK1104: cannot open file 'msvcrt.lib'` | The MSVC libraries are missing from the active environment. Repair/add the MSVC x64/x86 build-tools component and reopen the x64 Native Tools prompt. |
| `LNK1104: cannot open file 'kernel32.lib'` | A Windows SDK is missing or was added after the terminal opened. Install a Windows 10/11 SDK and open a new x64 Native Tools prompt. |
| `can't find crate for std` or target-not-installed errors | Run the `rustup target add` command above and make sure the target is `x86_64-pc-windows-msvc`. |
| Build succeeds but no `AAEmu.ZoneHost.exe` is found | Cargo creates `aaemu-zone-host.exe` in the exact output path above. Copy and rename it during deployment. |
| `Access denied` while copying | Stop the running zone in Zone Manager, confirm no `AAEmu.ZoneHost` process remains, and retry from an elevated PowerShell window if needed. |
| The executable reports a missing native DLL at launch | Compilation succeeded; this is a runtime setup issue. Put `x2game-dev_dedicate.dll` in the game directory and configure its path in Zone Manager. |

Avoid hard-coding `LIB` or `PATH` to a specific Visual Studio or Windows SDK version. The Visual Studio
developer prompt selects the versions actually installed on the build machine.

## Verified native parity

The entire non-CRT bootstrap is `FUN_140001000` at `0x140001000` in the CN 10.0.2.13
`dedicatedserver.exe`. The replacement mirrors its observable startup ABI:

| Native behavior | AAEmu Zone Host behavior |
| --- | --- |
| Copies the complete command line into startup offset `0x40` (capacity `0x800`) | Rebuilds the complete quoted command line into the same field and rejects overflow |
| Reads the first save-directory `devmode.cfg` line when it contains `-devmode` | Appends that line before checking the default zone |
| Adds `+zone w_gweonid_forest_1` when no case-insensitive `+zone` substring exists | Uses the same fallback; Zone Manager normally supplies the selected zone |
| Passes `lastWriteFileTime + fileSize` for `game_pak` at offset `0x9A0` | Computes the same wrapping 64-bit value |
| Calls startup vtable slots `0`, `6`, and `1`, and game vtable slot `8` | Uses the same initialize, shutdown, release, and run slots |
| Cleans the exception handler and unloads x2game after startup release | Uses scoped guards so success and error returns keep that order |

The startup block also matches the original values at offsets `0x940`, `0x948`, `0x950`, `0x960`,
`0x970`, `0x977..0x980`, and `0x9AC`. The host's configurable DLL path, explicit external save
directory, nonzero fatal exit code, status log, and hidden command console are intentional manager
extensions rather than original-bootstrap behavior.

## Launch with Zone Manager

1. Start MySQL, Login, and World. World must be listening on port `1240`.
2. Start `AAEmu.ZoneManager.exe`.
3. Verify the host path is `C:\AA\Bin64\AAEmu.ZoneHost.exe` and the native DLL path is
   `C:\AA\Bin64\x2game-dev_dedicate.dll`.
4. Select a zone on the map or in the zone list and click **Launch zone**.
5. Keep Zone Manager open while the zone is running. Use **Stop** or **Restart** from the same zone panel.

Zone Manager stores settings and per-zone logs under `C:\AAEmuRuntime\ZoneManager`. Build and temporary
files should also remain outside the repository.

## Manual launch

For diagnostics, set the host-only environment variables and pass normal x2game arguments directly:

```powershell
Set-Location C:\AA\Bin64
$env:AAEMU_ZONE_DLL = 'C:\AA\Bin64\x2game-dev_dedicate.dll'
$env:AAEMU_ZONE_SAVE_DIR = 'C:\AAEmuRuntime\ZoneManager\Logs\182-w_gweonid_forest_3'
$env:AAEMU_ZONE_LOG_NAME = 'ArcheAge-182.log'

.\AAEmu.ZoneHost.exe -dedicated `
  +world_ip 127.0.0.1 +world_port 1240 `
  +world_serveraddr 127.0.0.1 +world_serverport 1240 `
  +zone w_gweonid_forest_3 +sv_map w_gweonid_forest_3 `
  +db_location game/db/game_decrypted.sqlite3
```

Stopping this process takes its zone offline. The native DLL and decrypted `game/db/game_decrypted.sqlite3` must be
available from the selected game installation.

## Console catalog integration

For each running host, Zone Manager can signal
`Local\AAEmu.ZoneHost.ConsoleCatalog.<process-id>`. The host then invokes the native CrySystem catalog
routine verified for the supported `CrySystem.dll` build and writes `consolecommandsandvars.txt` in the
game working directory. The host first verifies the exported `Prompt` function RVA, refusing the
internal call when an incompatible CrySystem build is loaded. The manager copies the result to its
external runtime directory and parses it for the selected-zone command browser.
