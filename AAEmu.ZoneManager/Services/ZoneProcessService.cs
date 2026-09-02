using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using AAEmu.ZoneManager.Models;

namespace AAEmu.ZoneManager.Services;

public sealed class ZoneProcessService : IDisposable
{
    private readonly ConcurrentDictionary<uint, ProcessSession> _sessions = new();

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "LogWriter ownership transfers to ProcessSession and is released when the zone exits.")]
    public async Task LaunchAsync(ZoneRuntimeState state, ZoneManagerSettings settings)
    {
        if (state.IsRunning)
            return;
        if (!File.Exists(settings.NativeHostExecutablePath))
            throw new FileNotFoundException("The AAEmu native zone host was not found.", settings.NativeHostExecutablePath);
        if (!File.Exists(settings.NativeGameDllPath))
            throw new FileNotFoundException("The native zone game DLL was not found.", settings.NativeGameDllPath);
        if (!Directory.Exists(settings.WorkingDirectory))
            throw new DirectoryNotFoundException($"Working directory was not found: {settings.WorkingDirectory}");
        var hostExecutable = Path.GetFullPath(settings.NativeHostExecutablePath);

        Directory.CreateDirectory(settings.RuntimeRoot);
        var logDirectory = Path.Combine(
            settings.RuntimeRoot,
            "Logs",
            $"{state.Zone.ZoneKey}-{SanitizeFileName(state.Zone.Name)}");
        Directory.CreateDirectory(logDirectory);

        var logPath = Path.Combine(logDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var writer = new LogWriter(logPath);
        var nativeArguments = ZoneCommandLineBuilder.Build(state.Zone, settings);
        var nativeLogName = $"ArcheAge-{state.Zone.ZoneKey}-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        var nativeLogPath = Path.Combine(logDirectory, nativeLogName);
        var arguments = nativeArguments;
        var startInfo = new ProcessStartInfo
        {
            FileName = hostExecutable,
            WorkingDirectory = settings.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment[ZoneHostProtocol.DllEnvironment] = settings.NativeGameDllPath;
        startInfo.Environment[ZoneHostProtocol.SaveDirectoryEnvironment] = logDirectory;
        startInfo.Environment[ZoneHostProtocol.LogNameEnvironment] = nativeLogName;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var session = new ProcessSession(process, writer);
        if (!_sessions.TryAdd(state.Zone.ZoneKey, session))
        {
            writer.Dispose();
            process.Dispose();
            throw new InvalidOperationException($"Zone {state.Zone.ZoneKey} already has a managed process session.");
        }

        state.LogFilePath = logPath;
        state.CommandPreview = ZoneCommandLineBuilder.FormatPreview(hostExecutable, arguments);
        state.ExitCode = null;
        state.Status = "Starting";
        state.IsLogExpanded = true;
        Write(state, writer, "MANAGER", $"Launching zone {state.Zone.ZoneKey} ({state.Zone.Name})");
        Write(state, writer, "COMMAND", state.CommandPreview);

        if (Path.IsPathRooted(settings.DbLocation) && !File.Exists(settings.DbLocation))
            Write(state, writer, "WARNING", $"The configured native database was not found at {settings.DbLocation}");

        process.OutputDataReceived += (_, args) => Capture(state, writer, "OUT", args.Data);
        process.ErrorDataReceived += (_, args) => Capture(state, writer, "ERR", args.Data);
        process.Exited += (_, _) => OnExited(state, session);

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Windows did not start the integrated zone host.");

            state.ProcessId = process.Id;
            state.StartedAt = DateTimeOffset.Now;
            state.IsRunning = true;
            state.Status = "Running";
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            session.NativeLogTask = TailNativeLogAsync(state, session, nativeLogPath);
            Write(state, writer, "MANAGER", $"Integrated zone host started with PID {process.Id}");
            await Task.Yield();
        }
        catch
        {
            _sessions.TryRemove(state.Zone.ZoneKey, out _);
            state.IsRunning = false;
            state.Status = "Failed";
            session.Dispose();
            throw;
        }
    }

    public async Task StopAsync(ZoneRuntimeState state)
    {
        if (!_sessions.TryGetValue(state.Zone.ZoneKey, out var session))
            return;

        Write(state, session.Writer, "MANAGER", "Stop requested; terminating the zone process tree.");
        state.Status = "Stopping";

        try
        {
            if (!session.Process.HasExited)
                session.Process.Kill(entireProcessTree: true);
            await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and kill request.
        }
        catch (TimeoutException)
        {
            Write(state, session.Writer, "WARNING", "The zone process did not exit within 15 seconds.");
        }
    }

    public async Task RestartAsync(ZoneRuntimeState state, ZoneManagerSettings settings)
    {
        await StopAsync(state);
        for (var attempt = 0; attempt < 30 && _sessions.ContainsKey(state.Zone.ZoneKey); attempt++)
            await Task.Delay(100);
        await LaunchAsync(state, settings);
    }

    public void SendConsoleCommand(ZoneRuntimeState state, string command)
    {
        if (!_sessions.TryGetValue(state.Zone.ZoneKey, out var session) || session.Process.HasExited)
            throw new InvalidOperationException($"Zone {state.Zone.ZoneKey} is not running in this Zone Manager session.");

        ZoneConsoleInputService.Send(session.Process.Id, command);
        Write(state, session.Writer, "COMMAND", $"> {command}");
    }

    public void RequestConsoleCatalogDump(ZoneRuntimeState state)
    {
        if (!_sessions.TryGetValue(state.Zone.ZoneKey, out var session) || session.Process.HasExited)
            throw new InvalidOperationException($"Zone {state.Zone.ZoneKey} is not running in this Zone Manager session.");

        var eventName = $@"Local\AAEmu.ZoneHost.ConsoleCatalog.{session.Process.Id}";
        try
        {
            using var request = EventWaitHandle.OpenExisting(eventName);
            request.Set();
            Write(state, session.Writer, "COMMAND", "> native console catalog request");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Compatibility with an older Zone host. CrySystem may reject this
            // CHEAT-flagged command unless that process was launched in devmode.
            SendConsoleCommand(state, "DumpCommandsVars");
        }
    }

    public async Task StopAllAsync()
    {
        var sessions = _sessions.ToArray();
        foreach (var (zoneKey, session) in sessions)
        {
            try
            {
                if (!session.Process.HasExited)
                    session.Process.Kill(entireProcessTree: true);
                await session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Shutdown continues so every managed process receives a stop request.
            }
        }
    }

    private void OnExited(ZoneRuntimeState state, ProcessSession session)
    {
        int? exitCode = null;
        try { exitCode = session.Process.ExitCode; } catch { }

        Write(state, session.Writer, "MANAGER", $"Process exited with code {exitCode?.ToString() ?? "unknown"}");
        _sessions.TryRemove(state.Zone.ZoneKey, out _);
        session.Dispose();

        void ApplyExitState()
        {
            state.ExitCode = exitCode;
            state.ProcessId = null;
            state.IsRunning = false;
            state.Status = exitCode == 0 ? "Stopped" : "Exited";
        }

        // Process.Exited is raised on a thread-pool thread. ZoneRuntimeState is bound directly
        // by WPF, so mutating it there can terminate the manager during a launch/exit race.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            ApplyExitState();
        else
            _ = dispatcher.BeginInvoke(ApplyExitState);
    }

    private static void Capture(ZoneRuntimeState state, LogWriter writer, string source, string? line)
    {
        if (line is not null)
            Write(state, writer, source, line);
    }

    private static async Task TailNativeLogAsync(ZoneRuntimeState state, ProcessSession session, string path)
    {
        var token = session.TailCancellation.Token;
        try
        {
            for (var attempt = 0; attempt < 600 && !File.Exists(path); attempt++)
            {
                if (session.Process.HasExited || token.IsCancellationRequested)
                    return;
                await Task.Delay(100, token);
            }
            if (!File.Exists(path))
                return;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line is not null)
                {
                    Write(state, session.Writer, "NATIVE", line);
                    continue;
                }
                if (session.Process.HasExited)
                    return;
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Write(state, session.Writer, "WARNING", $"Native log tail stopped: {exception.Message}");
        }
    }

    private static void Write(ZoneRuntimeState state, LogWriter writer, string source, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{source}] {message}";
        try { writer.WriteLine(line); } catch { }
        state.AppendLog(line);
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }

    private sealed class ProcessSession(Process process, LogWriter writer) : IDisposable
    {
        public Process Process { get; } = process;
        public LogWriter Writer { get; } = writer;
        public CancellationTokenSource TailCancellation { get; } = new();
        public Task? NativeLogTask { get; set; }

        public void Dispose()
        {
            TailCancellation.Cancel();
            TailCancellation.Dispose();
            Writer.Dispose();
            Process.Dispose();
        }
    }

    private sealed class LogWriter : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly StreamWriter _writer;

        public LogWriter(string path) => _writer = new StreamWriter(path, append: false) { AutoFlush = true };

        public void WriteLine(string line)
        {
            lock (_lock)
                _writer.WriteLine(line);
        }

        public void Dispose()
        {
            lock (_lock)
                _writer.Dispose();
        }
    }
}
