using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace AAEmu.ZoneManager.Models;

public sealed record ZoneDefinition(
    uint ZoneKey,
    string Name,
    bool Closed,
    int? GroupId,
    int? DatabaseId = null);

public sealed record ZoneGroupDefinition(
    int Id,
    string Name,
    string MapFolderName,
    double X,
    double Y,
    double Width,
    double Height);

public sealed record WorldMapDefinition(
    string FolderName,
    double X,
    double Y,
    double Width,
    double Height,
    double ImageX,
    double ImageY,
    double ImageWidth,
    double ImageHeight);

public sealed record MapResourceDefinition(
    int Id,
    string Name,
    int TargetId,
    string TargetType,
    string FolderName,
    string? WorldOverImagePath,
    int? LinkedZoneGroupId);

public sealed record MapOverlaySegment(
    string Name,
    double OffsetX,
    double OffsetY,
    int SourceX,
    int SourceY,
    int Width,
    int Height);

/// <summary>
/// A horizontal run of 64-meter world sectors assigned to one native zone key.
/// The values are generated from the client world.xml data used by
/// WorldManager.ZoneKeyByRegions.
/// </summary>
public sealed record ZoneSectorRun(
    int SectorY,
    int StartSectorX,
    int SectorCount);

public sealed record ZoneCatalog(
    IReadOnlyList<ZoneDefinition> Zones,
    IReadOnlyList<ZoneGroupDefinition> Groups,
    WorldMapDefinition? WorldMap,
    IReadOnlyList<MapResourceDefinition> MapResources);

public enum ZoneConsoleEntryKind
{
    Command,
    Variable
}

public sealed record ZoneConsoleEntry(
    string Name,
    ZoneConsoleEntryKind Kind,
    string Syntax,
    string Help,
    string Script,
    string Type,
    string CurrentValue,
    string Flags)
{
    public string KindLabel => Kind == ZoneConsoleEntryKind.Command ? "COMMAND" : "CVAR";
}

/// <summary>Label/value pair backing the start-template dropdown.</summary>
public sealed record AutoZoneTemplateChoice(string Label, AutoZoneStartTemplate Value);

public sealed record WorldPlayerStatus(
    uint Id,
    uint ObjectId,
    string Name,
    byte Level,
    uint ZoneKey,
    string ZoneName,
    uint InstanceId,
    float X,
    float Y);

public sealed record WorldZoneConnectionStatus(
    uint SessionId,
    uint ZoneId,
    uint InstanceId,
    string State,
    string Ip,
    int UnitCount);

public sealed record WorldStatusSnapshot(
    DateTime ServerTimeUtc,
    int UptimeSeconds,
    int PlayerCount,
    IReadOnlyList<WorldPlayerStatus> Players,
    IReadOnlyList<WorldZoneConnectionStatus> Zones);

public sealed class ZoneRuntimeState : INotifyPropertyChanged
{
    private string _status = "Stopped";
    private bool _isRunning;
    private int? _processId;
    private DateTimeOffset? _startedAt;
    private int? _exitCode;
    private string? _logFilePath;
    private string _commandPreview = string.Empty;
    private bool _isLogExpanded;
    private bool _isWorldConnected;
    private string _worldConnectionStatus = "World status unavailable";

    public ZoneRuntimeState(ZoneDefinition zone) => Zone = zone;

    public ZoneDefinition Zone { get; }
    public ObservableCollection<string> LogLines { get; } = [];

    public string Status { get => _status; set => SetField(ref _status, value); }
    public bool IsRunning { get => _isRunning; set => SetField(ref _isRunning, value); }
    public int? ProcessId { get => _processId; set => SetField(ref _processId, value); }
    public DateTimeOffset? StartedAt { get => _startedAt; set => SetField(ref _startedAt, value); }
    public int? ExitCode { get => _exitCode; set => SetField(ref _exitCode, value); }
    public string? LogFilePath { get => _logFilePath; set => SetField(ref _logFilePath, value); }
    public string CommandPreview { get => _commandPreview; set => SetField(ref _commandPreview, value); }
    public bool IsLogExpanded { get => _isLogExpanded; set => SetField(ref _isLogExpanded, value); }
    public bool IsWorldConnected { get => _isWorldConnected; set => SetField(ref _isWorldConnected, value); }
    public string WorldConnectionStatus { get => _worldConnectionStatus; set => SetField(ref _worldConnectionStatus, value); }

    public void AppendLog(string line)
    {
        void Append()
        {
            LogLines.Add(line);
            while (LogLines.Count > 4_000)
                LogLines.RemoveAt(0);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Append();
        else
            dispatcher.BeginInvoke(Append);
    }

    public void ClearLog() => LogLines.Clear();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
