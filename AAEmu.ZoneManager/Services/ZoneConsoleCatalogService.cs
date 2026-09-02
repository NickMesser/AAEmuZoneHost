using System.Text;
using System.IO;
using AAEmu.ZoneManager.Models;

namespace AAEmu.ZoneManager.Services;

public sealed class ZoneConsoleCatalogService
{
    public const string DumpCommand = "dumpcommandsvars";
    public const string NativeDumpFileName = "consolecommandsandvars.txt";

    public IReadOnlyList<ZoneConsoleEntry> LoadCached(string runtimeRoot)
    {
        var path = CachedPath(runtimeRoot);
        return File.Exists(path) ? Parse(path) : [];
    }

    public async Task<IReadOnlyList<ZoneConsoleEntry>> RefreshAsync(
        ZoneRuntimeState state,
        ZoneManagerSettings settings,
        ZoneProcessService processService,
        CancellationToken cancellationToken = default)
    {
        var candidateDirectories = new[]
        {
            settings.WorkingDirectory,
            Directory.GetParent(settings.WorkingDirectory)?.FullName
        }.Where(path => !string.IsNullOrWhiteSpace(path))
         .Distinct(StringComparer.OrdinalIgnoreCase)
         .ToArray();
        var candidates = candidateDirectories
            .Select(directory => Path.Combine(directory!, NativeDumpFileName))
            .ToArray();
        var priorWrites = candidates.ToDictionary(path => path,
            path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue,
            StringComparer.OrdinalIgnoreCase);
        var requestedAt = DateTime.UtcNow;

        processService.RequestConsoleCatalogDump(state);

        for (var attempt = 0; attempt < 150; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var nativePath in candidates)
            {
                if (!File.Exists(nativePath))
                    continue;
                var writeTime = File.GetLastWriteTimeUtc(nativePath);
                if (writeTime > priorWrites[nativePath] && writeTime >= requestedAt.AddSeconds(-2))
                {
                    var cachedPath = CachedPath(settings.RuntimeRoot);
                    Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
                    File.Copy(nativePath, cachedPath, overwrite: true);
                    return Parse(cachedPath);
                }
            }
            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException(
            $"The Zone accepted {DumpCommand}, but {NativeDumpFileName} was not updated in " +
            $"{string.Join(" or ", candidateDirectories)} within 15 seconds. Wait for 'Load Zone Finished' and try again.");
    }

    public IReadOnlyList<ZoneConsoleEntry> Parse(string path)
    {
        var result = new List<ZoneConsoleEntry>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Commit()
        {
            if (fields.TryGetValue("Command", out var command))
            {
                var (name, syntax) = SplitFirst(command);
                if (name.Length != 0)
                    result.Add(new ZoneConsoleEntry(name, ZoneConsoleEntryKind.Command, syntax,
                        Get("help"), Get("script"), string.Empty, string.Empty, string.Empty));
            }
            else if (fields.TryGetValue("variable", out var variable))
            {
                var (name, flags) = SplitFirst(variable);
                if (name.Length != 0)
                    result.Add(new ZoneConsoleEntry(name, ZoneConsoleEntryKind.Variable, string.Empty,
                        Get("help"), string.Empty, Get("type"), Get("current"), flags));
            }
            fields.Clear();
        }

        string Get(string key) => fields.GetValueOrDefault(key, string.Empty).Trim();

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                Commit();
                continue;
            }
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            fields[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        Commit();

        return result
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (string Name, string Remaining) SplitFirst(string value)
    {
        value = value.Trim();
        var separator = value.IndexOfAny([' ', '\t']);
        return separator < 0
            ? (value, string.Empty)
            : (value[..separator], value[(separator + 1)..].Trim());
    }

    private static string CachedPath(string runtimeRoot) =>
        Path.Combine(runtimeRoot, "CommandCatalog", NativeDumpFileName);
}
