#![cfg(windows)]

use std::env;
use std::ffi::{c_char, c_void, CString, OsStr, OsString};
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::mem;
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::ptr;
use std::thread;
use std::time::Duration;

const HOST_SWITCH: &str = "--aaemu-zone-host";
const DLL_OPTION: &str = "--native-dll";
const SAVE_DIRECTORY_OPTION: &str = "--native-save-dir";
const LOG_NAME_OPTION: &str = "--native-log-name";
const DLL_ENVIRONMENT: &str = "AAEMU_ZONE_DLL";
const SAVE_DIRECTORY_ENVIRONMENT: &str = "AAEMU_ZONE_SAVE_DIR";
const LOG_NAME_ENVIRONMENT: &str = "AAEMU_ZONE_LOG_NAME";
const STARTUP_PARAMETERS_SIZE: usize = 0xA00;
const COMMAND_LINE_OFFSET: usize = 0x40;
const COMMAND_LINE_CAPACITY: usize = 0x800;
const CRY_SYSTEM_PROMPT_RVA: usize = 0xAB1C0;
const CRY_SYSTEM_ENVIRONMENT_RVA: usize = 0x14D440;
const CRY_SYSTEM_DUMP_COMMANDS_RVA: usize = 0x9D570;
const SHIP_PHYSICALIZATION_HOOK_RVA: usize = 0x360862;
const SHIP_PHYSICALIZATION_RESUME_RVA: usize = 0x360869;
const SHIP_MODEL_LOOKUP_RVA: usize = 0x258830;
const SHIP_UNIT_MODEL_INTERFACE_VTABLE_RVA: usize = 0xFC5DA8;
const SHIP_PHYSICALIZATION_VIRTUAL_RVA: usize = 0x289AC0;
const SLAVE_MANAGER_ROOT_RVA: usize = 0x1738FB8;
const SHIP_PHYSICALIZATION_CAVE_RVA: usize = 0xE76C80;
const SHIP_PHYSICALIZATION_CAVE_SIZE: usize = 128;
const SHIP_PHYSICALIZATION_HOOK_BYTES: [u8; 7] = [0x48, 0x8B, 0x15, 0x4F, 0x87, 0x2D, 0x01];
const SHIP_MODEL_LOOKUP_BYTES: [u8; 8] = [0x48, 0x8B, 0x81, 0x48, 0x85, 0x00, 0x00, 0xC3];
const SHIP_PHYSICALIZATION_VIRTUAL_BYTES: [u8; 20] = [
    0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8B, 0xF9, 0x48, 0x8B, 0x89,
    0x60, 0x03, 0x00, 0x00,
];
const BUILD_IDENTIFIER: [u8; 16] = [
    0x82, 0xB4, 0x41, 0x5A, 0xA4, 0x55, 0xD2, 0x2A, 0x46, 0x59, 0xA3, 0x2C, 0x2E, 0x17, 0x97, 0x0B,
];

type Handle = *mut c_void;
type Module = *mut c_void;

#[repr(C)]
#[derive(Default)]
struct FileTime {
    low: u32,
    high: u32,
}

#[repr(C)]
#[derive(Default)]
struct FileAttributeData {
    attributes: u32,
    creation_time: FileTime,
    last_access_time: FileTime,
    last_write_time: FileTime,
    file_size_high: u32,
    file_size_low: u32,
}

#[link(name = "kernel32")]
extern "system" {
    fn AllocConsole() -> i32;
    fn CloseHandle(handle: Handle) -> i32;
    fn CreateEventW(
        security: *mut c_void,
        manual_reset: i32,
        initial_state: i32,
        name: *const u16,
    ) -> Handle;
    fn CreateFileW(
        name: *const u16,
        access: u32,
        share: u32,
        security: *mut c_void,
        creation: u32,
        flags: u32,
        template: Handle,
    ) -> Handle;
    fn FreeLibrary(module: Module) -> i32;
    fn GetConsoleWindow() -> Handle;
    fn GetCurrentProcessId() -> u32;
    fn GetCurrentProcess() -> Handle;
    fn GetLastError() -> u32;
    fn GetFileAttributesExW(name: *const u16, level: u32, data: *mut FileAttributeData) -> i32;
    fn GetModuleFileNameW(module: Module, name: *mut u16, size: u32) -> u32;
    fn GetModuleHandleW(name: *const u16) -> Module;
    fn GetProcAddress(module: Module, name: *const c_char) -> *mut c_void;
    fn LoadLibraryW(name: *const u16) -> Module;
    fn FlushInstructionCache(process: Handle, address: *const c_void, size: usize) -> i32;
    fn SetCurrentDirectoryW(path: *const u16) -> i32;
    fn SetDllDirectoryW(path: *const u16) -> i32;
    fn SetStdHandle(kind: u32, handle: Handle) -> i32;
    fn VirtualProtect(
        address: *mut c_void,
        size: usize,
        new_protection: u32,
        old_protection: *mut u32,
    ) -> i32;
    fn WaitForSingleObject(handle: Handle, milliseconds: u32) -> u32;
}

#[link(name = "user32")]
extern "system" {
    fn ShowWindow(window: Handle, command: i32) -> i32;
}

struct Options {
    native_dll: PathBuf,
    save_directory: PathBuf,
    log_name: OsString,
    native_arguments: Vec<OsString>,
}

struct ModuleGuard(Module);

impl Drop for ModuleGuard {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe { FreeLibrary(self.0) };
        }
    }
}

struct ExceptionHandlerGuard {
    cleanup: unsafe extern "system" fn(),
}

impl Drop for ExceptionHandlerGuard {
    fn drop(&mut self) {
        unsafe { (self.cleanup)() };
    }
}

struct StartupGuard {
    startup: *mut c_void,
    shutdown: unsafe extern "system" fn(*mut c_void),
    release: unsafe extern "system" fn(*mut c_void),
    initialized: bool,
}

enum PatchDisposition {
    Applied,
    AlreadyApplied,
}

impl Drop for StartupGuard {
    fn drop(&mut self) {
        unsafe {
            if self.initialized {
                (self.shutdown)(self.startup);
            }
            (self.release)(self.startup);
        }
    }
}

fn wide(value: &OsStr) -> Vec<u16> {
    value.encode_wide().chain(Some(0)).collect()
}

fn ansi(value: &OsStr) -> Result<CString, String> {
    CString::new(value.to_string_lossy().as_bytes())
        .map_err(|_| "ANSI string contains a null byte".to_owned())
}

fn parse_options() -> Result<Options, String> {
    let arguments: Vec<OsString> = env::args_os().collect();
    let private_mode = arguments
        .get(1)
        .and_then(|x| x.to_str())
        .map(|x| x.eq_ignore_ascii_case(HOST_SWITCH))
        == Some(true);
    if !private_mode {
        let executable =
            env::current_exe().map_err(|error| format!("Current executable: {error}"))?;
        let native_dll = env::var_os(DLL_ENVIRONMENT)
            .map(PathBuf::from)
            .unwrap_or_else(|| {
                executable
                    .parent()
                    .unwrap_or(Path::new("."))
                    .join("x2game-dev_dedicate.dll")
            });
        let save_directory = env::var_os(SAVE_DIRECTORY_ENVIRONMENT)
            .map(PathBuf::from)
            .ok_or_else(|| format!("{SAVE_DIRECTORY_ENVIRONMENT} is required"))?;
        let log_name =
            env::var_os(LOG_NAME_ENVIRONMENT).unwrap_or_else(|| OsString::from("ArcheAge.log"));
        return Ok(Options {
            native_dll: fs::canonicalize(native_dll)
                .map_err(|error| format!("Native DLL: {error}"))?,
            save_directory,
            log_name,
            native_arguments: arguments[1..].to_vec(),
        });
    }
    let mut native_dll = None;
    let mut save_directory = None;
    let mut log_name = OsString::from("ArcheAge.log");
    let mut native_arguments = Vec::new();
    let mut index = 2;
    while index < arguments.len() {
        let argument = arguments[index].to_string_lossy();
        if argument == "--" {
            native_arguments.extend_from_slice(&arguments[index + 1..]);
            break;
        }
        let value = arguments
            .get(index + 1)
            .ok_or_else(|| "Incomplete zone-host option".to_owned())?;
        if argument.eq_ignore_ascii_case(DLL_OPTION) {
            native_dll =
                Some(fs::canonicalize(value).map_err(|error| format!("Native DLL: {error}"))?);
        } else if argument.eq_ignore_ascii_case(SAVE_DIRECTORY_OPTION) {
            let path = PathBuf::from(value);
            save_directory = Some(if path.is_absolute() {
                path
            } else {
                env::current_dir().unwrap().join(path)
            });
        } else if argument.eq_ignore_ascii_case(LOG_NAME_OPTION) {
            log_name = Path::new(value)
                .file_name()
                .unwrap_or_else(|| OsStr::new("ArcheAge.log"))
                .to_owned();
        } else {
            return Err(format!("Unknown zone-host option: {argument}"));
        }
        index += 2;
    }
    Ok(Options {
        native_dll: native_dll.ok_or_else(|| format!("{DLL_OPTION} is required"))?,
        save_directory: save_directory
            .ok_or_else(|| format!("{SAVE_DIRECTORY_OPTION} is required"))?,
        log_name,
        native_arguments,
    })
}

unsafe fn export(
    module: Module,
    decorated: &str,
    fallback: Option<&str>,
) -> Result<*mut c_void, String> {
    let decorated_name = CString::new(decorated).unwrap();
    let mut address = GetProcAddress(module, decorated_name.as_ptr());
    if address.is_null() {
        if let Some(fallback) = fallback {
            let fallback_name = CString::new(fallback).unwrap();
            address = GetProcAddress(module, fallback_name.as_ptr());
        }
    }
    if address.is_null() {
        Err(format!("Missing native export: {decorated}"))
    } else {
        Ok(address)
    }
}

fn append_rel32(
    code: &mut Vec<u8>,
    instruction_rva: usize,
    instruction: &[u8],
    target_rva: usize,
) -> Result<(), String> {
    code.extend_from_slice(instruction);
    let next_rva = instruction_rva
        .checked_add(instruction.len())
        .and_then(|value| value.checked_add(4))
        .ok_or_else(|| "Ship patch relative instruction overflow".to_owned())?;
    let displacement = target_rva as i64 - next_rva as i64;
    let displacement = i32::try_from(displacement)
        .map_err(|_| "Ship patch relative target is out of range".to_owned())?;
    code.extend_from_slice(&displacement.to_le_bytes());
    Ok(())
}

fn build_ship_physicalization_stub() -> Result<Vec<u8>, String> {
    let mut code = Vec::with_capacity(SHIP_PHYSICALIZATION_CAVE_SIZE);

    // FUN_39360590 has already established an aligned stack. Its following
    // instructions overwrite RAX, RCX, RDX, R8, and flags before reading them;
    // R9-R11 and XMM0-XMM5 are dead through the return. The callees preserve
    // the live nonvolatile RDI (unit), RSI, and RBX values.
    code.extend_from_slice(&[0x48, 0x83, 0xEC, 0x20]); // sub rsp, 0x20 (shadow space)
    code.extend_from_slice(&[0x48, 0x8B, 0xCF]); // mov rcx, rdi
    let instruction_rva = SHIP_PHYSICALIZATION_CAVE_RVA + code.len();
    append_rel32(&mut code, instruction_rva, &[0xE8], SHIP_MODEL_LOOKUP_RVA)?; // call FUN_39258830
    code.extend_from_slice(&[0x48, 0x85, 0xC0]); // test rax, rax
    code.extend_from_slice(&[0x74, 0x00]); // jz replay
    let null_jump = code.len() - 1;

    let instruction_rva = SHIP_PHYSICALIZATION_CAVE_RVA + code.len();
    append_rel32(
        &mut code,
        instruction_rva,
        &[0x48, 0x8D, 0x15],
        SHIP_UNIT_MODEL_INTERFACE_VTABLE_RVA,
    )?; // lea rdx, [ShipUnitModel interface vtable]
    code.extend_from_slice(&[0x48, 0x39, 0x10]); // cmp [rax], rdx
    code.extend_from_slice(&[0x75, 0x00]); // jne replay
    let type_jump = code.len() - 1;
    code.extend_from_slice(&[0x48, 0x8B, 0xC8]); // mov rcx, rax
    code.extend_from_slice(&[0xBA, 0x01, 0x00, 0x00, 0x00]); // mov edx, 1
    code.extend_from_slice(&[0x4C, 0x8B, 0x00]); // mov r8, [rax]
    code.extend_from_slice(&[0x41, 0xFF, 0x90, 0xB8, 0x03, 0x00, 0x00]); // call [r8+0x3b8]

    let replay_offset = code.len();
    for jump in [null_jump, type_jump] {
        let displacement = replay_offset as isize - (jump + 1) as isize;
        code[jump] = i8::try_from(displacement)
            .map_err(|_| "Ship patch internal branch is out of range".to_owned())?
            as u8;
    }
    code.extend_from_slice(&[0x48, 0x83, 0xC4, 0x20]); // add rsp, 0x20
    let instruction_rva = SHIP_PHYSICALIZATION_CAVE_RVA + code.len();
    append_rel32(
        &mut code,
        instruction_rva,
        &[0x48, 0x8B, 0x15],
        SLAVE_MANAGER_ROOT_RVA,
    )?; // replay mov rdx, [DAT_3A638FB8]
    let instruction_rva = SHIP_PHYSICALIZATION_CAVE_RVA + code.len();
    append_rel32(
        &mut code,
        instruction_rva,
        &[0xE9],
        SHIP_PHYSICALIZATION_RESUME_RVA,
    )?;

    if code.len() > SHIP_PHYSICALIZATION_CAVE_SIZE {
        return Err(format!(
            "Ship patch stub is {} bytes but the verified cave is only {} bytes",
            code.len(),
            SHIP_PHYSICALIZATION_CAVE_SIZE
        ));
    }
    Ok(code)
}

fn build_ship_physicalization_hook() -> Result<[u8; 7], String> {
    let next_rva = SHIP_PHYSICALIZATION_HOOK_RVA + 5;
    let displacement = SHIP_PHYSICALIZATION_CAVE_RVA as i64 - next_rva as i64;
    let displacement = i32::try_from(displacement)
        .map_err(|_| "Ship patch cave is outside near-jump range".to_owned())?;
    let mut hook = [0x90; 7];
    hook[0] = 0xE9;
    hook[1..5].copy_from_slice(&displacement.to_le_bytes());
    Ok(hook)
}

unsafe fn module_image_size(module: Module) -> Result<usize, String> {
    let base = module as *const u8;
    if ptr::read_unaligned(base as *const u16) != 0x5A4D {
        return Err("x2game does not have a valid DOS header".to_owned());
    }
    let pe_offset = ptr::read_unaligned(base.add(0x3C) as *const u32) as usize;
    if pe_offset < 0x40 || pe_offset > 0x1000 {
        return Err("x2game has an invalid PE header offset".to_owned());
    }
    let pe = base.add(pe_offset);
    if ptr::read_unaligned(pe as *const u32) != 0x0000_4550 {
        return Err("x2game does not have a valid PE signature".to_owned());
    }
    let optional_header = pe.add(24);
    if ptr::read_unaligned(optional_header as *const u16) != 0x20B {
        return Err("x2game is not a 64-bit PE image".to_owned());
    }
    let size = ptr::read_unaligned(optional_header.add(0x38) as *const u32) as usize;
    if size == 0 {
        return Err("x2game PE image size is zero".to_owned());
    }
    Ok(size)
}

fn ensure_image_range(image_size: usize, rva: usize, size: usize) -> Result<(), String> {
    let end = rva
        .checked_add(size)
        .ok_or_else(|| "Ship patch image range overflow".to_owned())?;
    if end > image_size {
        return Err(format!(
            "Ship patch RVA 0x{rva:X}..0x{end:X} exceeds x2game image size 0x{image_size:X}"
        ));
    }
    Ok(())
}

fn bytes_hex(bytes: &[u8]) -> String {
    bytes
        .iter()
        .map(|value| format!("{value:02X}"))
        .collect::<Vec<_>>()
        .join(" ")
}

unsafe fn verify_image_signature(
    base: *const u8,
    image_size: usize,
    rva: usize,
    expected: &[u8],
    label: &str,
) -> Result<(), String> {
    ensure_image_range(image_size, rva, expected.len())?;
    let actual = std::slice::from_raw_parts(base.add(rva), expected.len());
    if actual != expected {
        return Err(format!(
            "unsupported x2game {label} signature at RVA 0x{rva:X}: expected [{}], found [{}]",
            bytes_hex(expected),
            bytes_hex(actual)
        ));
    }
    Ok(())
}

unsafe fn write_executable(address: *mut u8, bytes: &[u8], label: &str) -> Result<(), String> {
    const PAGE_EXECUTE_READWRITE: u32 = 0x40;
    let mut old_protection = 0u32;
    if VirtualProtect(
        address as *mut c_void,
        bytes.len(),
        PAGE_EXECUTE_READWRITE,
        &mut old_protection,
    ) == 0
    {
        return Err(format!(
            "VirtualProtect({label}) failed with Windows error {}",
            GetLastError()
        ));
    }
    ptr::copy_nonoverlapping(bytes.as_ptr(), address, bytes.len());
    let mut ignored = 0u32;
    if VirtualProtect(
        address as *mut c_void,
        bytes.len(),
        old_protection,
        &mut ignored,
    ) == 0
    {
        return Err(format!(
            "Restoring protection for {label} failed with Windows error {}",
            GetLastError()
        ));
    }
    if FlushInstructionCache(GetCurrentProcess(), address as *const c_void, bytes.len()) == 0 {
        return Err(format!(
            "FlushInstructionCache({label}) failed with Windows error {}",
            GetLastError()
        ));
    }
    Ok(())
}

unsafe fn apply_ship_physicalization_patch(module: Module) -> Result<PatchDisposition, String> {
    let image_size = module_image_size(module)?;
    ensure_image_range(
        image_size,
        SHIP_PHYSICALIZATION_HOOK_RVA,
        SHIP_PHYSICALIZATION_HOOK_BYTES.len(),
    )?;
    ensure_image_range(
        image_size,
        SHIP_PHYSICALIZATION_CAVE_RVA,
        SHIP_PHYSICALIZATION_CAVE_SIZE,
    )?;

    let base = module as *mut u8;
    verify_image_signature(
        base,
        image_size,
        SHIP_MODEL_LOOKUP_RVA,
        &SHIP_MODEL_LOOKUP_BYTES,
        "FUN_39258830 model lookup",
    )?;
    verify_image_signature(
        base,
        image_size,
        SHIP_PHYSICALIZATION_VIRTUAL_RVA,
        &SHIP_PHYSICALIZATION_VIRTUAL_BYTES,
        "FUN_39289AC0 ship physicalization virtual",
    )?;
    let virtual_slot_rva = SHIP_UNIT_MODEL_INTERFACE_VTABLE_RVA + 0x3B8;
    ensure_image_range(image_size, virtual_slot_rva, mem::size_of::<usize>())?;
    let actual_virtual = ptr::read_unaligned(base.add(virtual_slot_rva) as *const usize);
    let expected_virtual = base as usize + SHIP_PHYSICALIZATION_VIRTUAL_RVA;
    if actual_virtual != expected_virtual {
        return Err(format!(
            "unsupported x2game ShipUnitModel vtable entry at RVA 0x{virtual_slot_rva:X}: expected module+0x{SHIP_PHYSICALIZATION_VIRTUAL_RVA:X} (0x{expected_virtual:X}), found 0x{actual_virtual:X}"
        ));
    }

    let stub = build_ship_physicalization_stub()?;
    let patched_hook = build_ship_physicalization_hook()?;
    let mut patched_cave = [0xCC; SHIP_PHYSICALIZATION_CAVE_SIZE];
    patched_cave[..stub.len()].copy_from_slice(&stub);

    let hook_address = base.add(SHIP_PHYSICALIZATION_HOOK_RVA);
    let cave_address = base.add(SHIP_PHYSICALIZATION_CAVE_RVA);
    let current_hook = std::slice::from_raw_parts(hook_address, patched_hook.len());
    let current_cave = std::slice::from_raw_parts(cave_address, SHIP_PHYSICALIZATION_CAVE_SIZE);

    if current_hook == patched_hook {
        if current_cave == patched_cave {
            return Ok(PatchDisposition::AlreadyApplied);
        }
        return Err(
            "x2game has the ship hook jump but not the exact expected cave; refusing a partial or foreign patch"
                .to_owned(),
        );
    }
    if current_hook != SHIP_PHYSICALIZATION_HOOK_BYTES {
        let actual = bytes_hex(current_hook);
        return Err(format!(
            "unsupported x2game ship hook signature at RVA 0x{SHIP_PHYSICALIZATION_HOOK_RVA:X}: {actual}"
        ));
    }
    if current_cave.iter().any(|value| *value != 0xCC) {
        return Err(format!(
            "x2game ship cave at RVA 0x{SHIP_PHYSICALIZATION_CAVE_RVA:X} is not the verified {}-byte 0xCC region; refusing to overwrite it",
            SHIP_PHYSICALIZATION_CAVE_SIZE
        ));
    }

    write_executable(cave_address, &patched_cave, "ship physicalization cave")?;
    write_executable(hook_address, &patched_hook, "ship physicalization hook")?;
    Ok(PatchDisposition::Applied)
}

unsafe fn ensure_console() -> Result<(), String> {
    let allocated = GetConsoleWindow().is_null();
    if allocated && AllocConsole() == 0 {
        return Err("AllocConsole failed".to_owned());
    }
    let input_name = wide(OsStr::new("CONIN$"));
    let output_name = wide(OsStr::new("CONOUT$"));
    let input = CreateFileW(
        input_name.as_ptr(),
        0xC000_0000,
        3,
        ptr::null_mut(),
        3,
        0,
        ptr::null_mut(),
    );
    let output = CreateFileW(
        output_name.as_ptr(),
        0xC000_0000,
        3,
        ptr::null_mut(),
        3,
        0,
        ptr::null_mut(),
    );
    if input as isize == -1 || output as isize == -1 {
        return Err("Opening the console screen buffer failed".to_owned());
    }
    SetStdHandle(-10i32 as u32, input);
    SetStdHandle(-11i32 as u32, output);
    SetStdHandle(-12i32 as u32, output);
    let window = GetConsoleWindow();
    if allocated && !window.is_null() {
        ShowWindow(window, 0);
    }
    Ok(())
}

fn quote(value: &str) -> String {
    if !value.is_empty()
        && !value
            .chars()
            .any(|character| character.is_whitespace() || character == '"')
    {
        return value.to_owned();
    }
    let mut result = String::from("\"");
    let mut slashes = 0;
    for character in value.chars() {
        if character == '\\' {
            slashes += 1;
            continue;
        }
        if character == '"' {
            result.push_str(&"\\".repeat(slashes * 2 + 1));
            result.push(character);
            slashes = 0;
            continue;
        }
        result.push_str(&"\\".repeat(slashes));
        slashes = 0;
        result.push(character);
    }
    result.push_str(&"\\".repeat(slashes * 2));
    result.push('"');
    result
}

unsafe fn build_command_line(
    source: &[OsString],
    save_directory: &Path,
) -> Result<(CString, bool), String> {
    let mut executable = [0u16; 32768];
    let length = GetModuleFileNameW(
        ptr::null_mut(),
        executable.as_mut_ptr(),
        executable.len() as u32,
    );
    if length == 0 || length as usize == executable.len() {
        return Err("GetModuleFileNameW failed".to_owned());
    }
    let mut command = quote(&String::from_utf16_lossy(&executable[..length as usize]));
    for argument in source {
        command.push(' ');
        command.push_str(&quote(&argument.to_string_lossy()));
    }

    // dedicatedserver.exe FUN_140001000 @ 0x140001000 checks the complete command
    // line, then appends the first devmode.cfg line verbatim when it contains -devmode.
    if !command.contains("-devmode") {
        if let Ok(config) = fs::read_to_string(save_directory.join("devmode.cfg")) {
            if let Some(first_line) = config.lines().next() {
                if first_line.contains("-devmode") {
                    command.push(' ');
                    command.push_str(first_line);
                }
            }
        }
    }

    if !command.to_ascii_lowercase().contains("+zone") {
        command.push_str(" +zone w_gweonid_forest_1");
    }
    let full_dump = command.to_ascii_lowercase().contains("-fulldump");
    let command =
        CString::new(command).map_err(|_| "Native command line contains a null byte".to_owned())?;
    Ok((command, full_dump))
}

unsafe fn game_pak_signature(runtime_directory: &Path) -> i64 {
    let local = runtime_directory.join("game_pak");
    let parent = runtime_directory
        .parent()
        .unwrap_or(runtime_directory)
        .join("game_pak");
    let path = if local.exists() { local } else { parent };
    let path_wide = wide(path.as_os_str());
    let mut attributes = FileAttributeData::default();
    if GetFileAttributesExW(path_wide.as_ptr(), 0, &mut attributes) == 0 {
        return 0;
    }
    let last_write =
        (attributes.last_write_time.high as u64) << 32 | attributes.last_write_time.low as u64;
    let file_size = (attributes.file_size_high as u64) << 32 | attributes.file_size_low as u64;
    last_write.wrapping_add(file_size) as i64
}

unsafe fn write_value<T: Copy>(buffer: &mut [u8], offset: usize, value: T) {
    ptr::copy_nonoverlapping(
        &value as *const T as *const u8,
        buffer.as_mut_ptr().add(offset),
        mem::size_of::<T>(),
    );
}

fn status(save_directory: &Path, message: &str) {
    if let Ok(mut file) = OpenOptions::new()
        .create(true)
        .append(true)
        .open(save_directory.join("AAEmu.ZoneHost.status.log"))
    {
        let _ = writeln!(file, "{message}");
    }
}

unsafe fn dump_console_catalog() -> Result<PathBuf, String> {
    let cry_system = GetModuleHandleW(wide(OsStr::new("CrySystem.dll")).as_ptr());
    if cry_system.is_null() {
        return Err("CrySystem.dll is not loaded".to_owned());
    }

    // Version guard the private RVAs against a stable exported function before
    // calling the Ghidra-verified CrySystem 10.0.2.13 catalog routine.
    let prompt = export(cry_system, "?Prompt@CUNIXConsole@@QEAADPEBD0@Z", None)?;
    let base = cry_system as usize;
    if prompt as usize != base + CRY_SYSTEM_PROMPT_RVA {
        return Err("Unsupported CrySystem build; console catalog RVAs do not match".to_owned());
    }

    let environment = *((base + CRY_SYSTEM_ENVIRONMENT_RVA) as *const *mut c_void);
    if environment.is_null() {
        return Err("CrySystem environment is not initialized".to_owned());
    }
    let console = *((environment as *const u8).add(0x38) as *const *mut c_void);
    if console.is_null() {
        return Err("CrySystem console is not initialized".to_owned());
    }

    type DumpCommands =
        unsafe extern "system" fn(*mut c_void, *const c_char, *mut c_void, *mut c_void);
    let dump: DumpCommands = mem::transmute(base + CRY_SYSTEM_DUMP_COMMANDS_RVA);
    dump(console, c"".as_ptr(), ptr::null_mut(), ptr::null_mut());

    let path = env::current_dir()
        .map_err(|error| format!("Current directory: {error}"))?
        .join("consolecommandsandvars.txt");
    if !path.is_file() {
        return Err(format!("CrySystem did not create {}", path.display()));
    }
    Ok(path)
}

fn start_console_catalog_worker(save_directory: PathBuf) {
    thread::spawn(move || unsafe {
        let event_name = format!(
            "Local\\AAEmu.ZoneHost.ConsoleCatalog.{}",
            GetCurrentProcessId()
        );
        let event = CreateEventW(
            ptr::null_mut(),
            0,
            0,
            wide(OsStr::new(&event_name)).as_ptr(),
        );
        if event.is_null() {
            status(&save_directory, "Console catalog event creation failed.");
            return;
        }
        status(
            &save_directory,
            &format!("Console catalog event ready: {event_name}"),
        );
        loop {
            match WaitForSingleObject(event, 1000) {
                0 => match dump_console_catalog() {
                    Ok(path) => status(
                        &save_directory,
                        &format!("Native console catalog written: {}", path.display()),
                    ),
                    Err(error) => status(
                        &save_directory,
                        &format!("Native console catalog failed: {error}"),
                    ),
                },
                0x102 => thread::sleep(Duration::from_millis(10)),
                code => {
                    status(
                        &save_directory,
                        &format!("Console catalog event wait failed: {code}"),
                    );
                    break;
                }
            }
        }
        CloseHandle(event);
    });
}

unsafe fn run(options: Options) -> Result<i32, String> {
    fs::create_dir_all(&options.save_directory)
        .map_err(|error| format!("Save directory: {error}"))?;
    status(&options.save_directory, "Host bootstrap started.");
    let runtime_directory = options
        .native_dll
        .parent()
        .ok_or_else(|| "Native DLL has no parent".to_owned())?;
    let common_path = runtime_directory.join("xlcommon.dll");
    if !common_path.is_file() {
        return Err("xlcommon.dll was not found beside x2game".to_owned());
    }
    if SetCurrentDirectoryW(wide(runtime_directory.as_os_str()).as_ptr()) == 0 {
        return Err("SetCurrentDirectory failed".to_owned());
    }
    SetDllDirectoryW(wide(runtime_directory.as_os_str()).as_ptr());
    println!("[ZONE HOST] Native DLL: {}", options.native_dll.display());
    println!(
        "[ZONE HOST] Save directory: {}",
        options.save_directory.display()
    );
    ensure_console()?;

    let common = LoadLibraryW(wide(common_path.as_os_str()).as_ptr());
    if common.is_null() {
        return Err("LoadLibrary(xlcommon.dll) failed".to_owned());
    }
    let _common_guard = ModuleGuard(common);
    let random_init: unsafe extern "system" fn() = mem::transmute(export(
        common,
        "?XlRandomInit@@YAXXZ",
        Some("XlRandomInit"),
    )?);
    let set_product: unsafe extern "system" fn(*const c_char) = mem::transmute(export(
        common,
        "?XlSetProduct@@YAXPEBD@Z",
        Some("XlSetProduct"),
    )?);
    let set_working_directory: unsafe extern "system" fn(bool) -> bool = mem::transmute(export(
        common,
        "?XlSetWorkingDir@@YA_N_N@Z",
        Some("XlSetWorkingDir"),
    )?);
    let set_save_directory: unsafe extern "system" fn(*const c_char) = mem::transmute(export(
        common,
        "?XlSetSaveGameDir@@YAXPEBD@Z",
        Some("XlSetSaveGameDir"),
    )?);
    let get_save_directory: unsafe extern "system" fn(*mut c_char, i32) -> bool =
        mem::transmute(export(
            common,
            "?XlGetSaveGameDir@@YA_NPEADH@Z",
            Some("XlGetSaveGameDir"),
        )?);
    let init_exception: unsafe extern "system" fn(*const c_char, *const c_char, bool) -> bool =
        mem::transmute(export(
            common,
            "?InitExceptionHandler@@YA_NPEBD0_N@Z",
            Some("InitExceptionHandler"),
        )?);
    let cleanup_exception: unsafe extern "system" fn() = mem::transmute(export(
        common,
        "?CleanupExceptionHandler@@YAXXZ",
        Some("CleanupExceptionHandler"),
    )?);
    random_init();
    set_product(CString::new("ArcheAge").unwrap().as_ptr());
    if !set_working_directory(true) {
        return Err("XlSetWorkingDir failed".to_owned());
    }
    let requested_save = ansi(options.save_directory.as_os_str())?;
    set_save_directory(requested_save.as_ptr());
    let mut resolved_save = [0 as c_char; 0x104];
    if !get_save_directory(resolved_save.as_mut_ptr(), resolved_save.len() as i32) {
        return Err("XlGetSaveGameDir failed".to_owned());
    }
    let log_name = ansi(&options.log_name)?;
    let (command, full_dump) =
        build_command_line(&options.native_arguments, &options.save_directory)?;
    let command_bytes = command.as_bytes_with_nul();
    if command_bytes.len() > COMMAND_LINE_CAPACITY {
        return Err("Native command line exceeds 2047 bytes".to_owned());
    }
    init_exception(log_name.as_ptr(), resolved_save.as_ptr(), full_dump);
    let _exception_guard = ExceptionHandlerGuard {
        cleanup: cleanup_exception,
    };

    let game_module = LoadLibraryW(wide(options.native_dll.as_os_str()).as_ptr());
    if game_module.is_null() {
        return Err("LoadLibrary(x2game) failed".to_owned());
    }
    let _game_module_guard = ModuleGuard(game_module);
    let patch = apply_ship_physicalization_patch(game_module).map_err(|error| {
        let error = format!("Ship physicalization patch failed: {error}");
        status(&options.save_directory, &error);
        error
    })?;
    let patch_status = match patch {
        PatchDisposition::Applied => "applied",
        PatchDisposition::AlreadyApplied => "already applied and verified",
    };
    println!("[ZONE HOST] Ship physicalization patch {patch_status}.");
    status(
        &options.save_directory,
        &format!("Ship physicalization patch {patch_status}."),
    );
    let create_startup: unsafe extern "system" fn() -> *mut c_void =
        mem::transmute(export(game_module, "CreateGameStartup", None)?);
    let startup = create_startup();
    if startup.is_null() {
        return Err("CreateGameStartup returned null".to_owned());
    }
    status(
        &options.save_directory,
        &format!("CreateGameStartup returned {startup:p}."),
    );

    let mut parameters = [0u8; STARTUP_PARAMETERS_SIZE];
    let module = GetModuleHandleW(ptr::null());
    write_value(&mut parameters, 0x00, module);
    write_value(&mut parameters, 0x28, resolved_save.as_ptr());
    write_value(&mut parameters, 0x30, log_name.as_ptr());
    parameters[COMMAND_LINE_OFFSET..COMMAND_LINE_OFFSET + command_bytes.len()]
        .copy_from_slice(command_bytes);
    write_value(&mut parameters, 0x940, 2i32);
    write_value(&mut parameters, 0x948, 0x23Fi64);
    parameters[0x950..0x960].copy_from_slice(&BUILD_IDENTIFIER);
    parameters[0x960] = 1;
    parameters[0x970] = 1;
    parameters[0x977..0x981].fill(1);
    let input_pack_signature = game_pak_signature(runtime_directory);
    write_value(&mut parameters, 0x9A0, input_pack_signature);
    parameters[0x9AC] = 1;

    type Initialize = unsafe extern "system" fn(*mut c_void, *mut *mut c_void, *mut c_void);
    type Method = unsafe extern "system" fn(*mut c_void);
    let startup_vtable = *(startup as *mut *mut *mut c_void);
    let initialize: Initialize = mem::transmute(*startup_vtable.add(0));
    let mut startup_guard = StartupGuard {
        startup,
        shutdown: mem::transmute::<*mut c_void, Method>(*startup_vtable.add(6)),
        release: mem::transmute::<*mut c_void, Method>(*startup_vtable.add(1)),
        initialized: false,
    };
    let mut game: *mut c_void = ptr::null_mut();
    status(
        &options.save_directory,
        &format!("Calling native startup initialize with pack signature={input_pack_signature}."),
    );
    initialize(startup, &mut game, parameters.as_mut_ptr() as *mut c_void);
    status(
        &options.save_directory,
        &format!(
            "Initialize returned game={game:p}, pack_flag={}, signature={}",
            parameters[0x9A8],
            i64::from_le_bytes(parameters[0x9A0..0x9A8].try_into().unwrap())
        ),
    );
    if game.is_null() || (*(game as *mut *mut c_void)).is_null() {
        status(
            &options.save_directory,
            "Native game initialization returned null.",
        );
        return Err("Native game initialization returned null".to_owned());
    }
    startup_guard.initialized = true;
    let game_object = *(game as *mut *mut c_void);
    println!("[ZONE HOST] Native game initialized; entering the zone run loop.");
    status(
        &options.save_directory,
        &format!("Entering native zone run loop with object={game_object:p}."),
    );
    let game_vtable = *(game_object as *mut *mut *mut c_void);
    let run: Method = mem::transmute(*game_vtable.add(8));
    start_console_catalog_worker(options.save_directory.clone());
    run(game_object);
    status(&options.save_directory, "Native zone run loop returned.");
    Ok(0)
}

fn main() {
    let result = parse_options().and_then(|options| unsafe { run(options) });
    match result {
        Ok(code) => std::process::exit(code),
        Err(error) => {
            eprintln!("[ZONE HOST] Fatal bootstrap error: {error}");
            std::process::exit(1);
        }
    }
}
