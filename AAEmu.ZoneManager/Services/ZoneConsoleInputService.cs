using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AAEmu.ZoneManager.Services;

/// <summary>
/// Writes raw key events into a managed Zone host's private console. CrySystem's
/// CUNIXConsole input thread consumes ReadConsoleInputA records, so redirected
/// StandardInput cannot be used here.
/// </summary>
internal static class ZoneConsoleInputService
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const ushort KeyEvent = 1;
    private const uint MapVkToVsc = 0;
    private const int MaximumCommandLength = 2_048;
    private static readonly Lock ConsoleLock = new();

    public static void Send(int processId, string command)
    {
        command = command.Trim();
        if (command.Length == 0)
            throw new ArgumentException("Enter a console command.", nameof(command));
        if (command.Length > MaximumCommandLength)
            throw new ArgumentException($"Console commands are limited to {MaximumCommandLength} characters.", nameof(command));
        if (command.Contains('\r') || command.Contains('\n'))
            throw new ArgumentException("Only one console command can be sent at a time.", nameof(command));

        lock (ConsoleLock)
        {
            // Zone Manager is a GUI process and normally owns no console. A
            // defensive detach also makes recovery deterministic after a prior
            // failed send.
            FreeConsole();
            if (!AttachConsole((uint)processId))
                throw LastError($"Unable to attach to Zone process {processId}");

            try
            {
                using var input = CreateFileW("CONIN$", GenericRead | GenericWrite, 3,
                    nint.Zero, OpenExisting, 0, nint.Zero);
                if (input.IsInvalid)
                    throw LastError("Unable to open the Zone console input buffer");

                if (!FlushConsoleInputBuffer(input))
                    throw LastError("Unable to clear the Zone console input buffer");

                var records = new InputRecord[(command.Length + 1) * 2];
                var index = 0;
                foreach (var character in command)
                {
                    var virtualKey = VirtualKey(character, out var controlState);
                    records[index++] = CreateKeyRecord(true, character, virtualKey, controlState);
                    records[index++] = CreateKeyRecord(false, character, virtualKey, controlState);
                }
                records[index++] = CreateKeyRecord(true, '\r', 0x0d, 0);
                records[index] = CreateKeyRecord(false, '\r', 0x0d, 0);

                if (!WriteConsoleInputW(input, records, (uint)records.Length, out var written))
                    throw LastError("Unable to write to the Zone console input buffer");
                if (written != records.Length)
                    throw new IOException($"Only {written} of {records.Length} console input events were written.");
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    private static InputRecord CreateKeyRecord(bool keyDown, char character, ushort virtualKey, uint controlState)
    {
        var scanCode = (ushort)MapVirtualKeyW(virtualKey, MapVkToVsc);
        return new InputRecord
        {
            EventType = KeyEvent,
            KeyEvent = new KeyEventRecord
            {
                KeyDown = keyDown,
                RepeatCount = 1,
                VirtualKeyCode = virtualKey,
                VirtualScanCode = scanCode,
                UnicodeChar = character <= byte.MaxValue ? character : '?',
                ControlKeyState = controlState
            }
        };
    }

    private static ushort VirtualKey(char character, out uint controlState)
    {
        var translated = VkKeyScanW(character);
        controlState = translated != -1 && ((translated >> 8) & 1) != 0 ? 0x10u : 0;
        return translated == -1 ? (ushort)0 : (ushort)(translated & 0xff);
    }

    private static Win32Exception LastError(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyEventRecord
    {
        [MarshalAs(UnmanagedType.Bool)] public bool KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KeyEventRecord KeyEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushConsoleInputBuffer(SafeFileHandle input);

    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleInputW(SafeFileHandle input,
        [In] InputRecord[] records, uint count, out uint written);

    [DllImport("user32.dll", EntryPoint = "VkKeyScanW", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char character);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);
}
