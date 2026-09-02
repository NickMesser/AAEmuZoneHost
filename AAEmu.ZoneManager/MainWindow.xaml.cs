using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AAEmu.ZoneManager.Models;
using AAEmu.ZoneManager.Services;
using Microsoft.Win32;

namespace AAEmu.ZoneManager;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ZoneCatalogService _catalogService = new();
    private readonly ZoneProcessService _processService = new();
    private readonly ZoneConsoleCatalogService _consoleCatalogService = new();
    private readonly MapAssetService _mapAssetService = new();
    private readonly ZoneSectorCoverageService _zoneSectorCoverageService = new();
    private readonly WorldStatusService _worldStatusService = new();
    private readonly AutoZoneService _autoZoneService = new();
    private readonly CancellationTokenSource _worldMonitorCancellation = new();
    private bool _autoZoneRunning;
    private bool _autoZoneTickBusy;
    private ZoneRuntimeState? _defaultZone;
    private readonly Dictionary<uint, Button> _mapMarkers = [];
    private IReadOnlyList<ZoneGroupDefinition> _groups = [];
    private IReadOnlyList<MapResourceDefinition> _mapResources = [];
    private Dictionary<uint, IReadOnlyList<ZoneSectorRun>> _zoneCoverage = [];
    private WorldMapDefinition? _worldMap;
    private string _currentMapFolder = "main_world";
    private Rect? _selectedCoverageBounds;
    private ZoneRuntimeState? _selectedZone;
    private ImageSource? _worldMapSource;
    private ImageSource? _selectedZoneMapSource;
    private string _searchText = string.Empty;
    private string _zoneCountText = string.Empty;
    private string _mapTitleText = "World map / main_world";
    private string _consoleCommandSearchText = string.Empty;
    private string _consoleCommandText = string.Empty;
    private string _consoleCommandStatus = "Launch a zone to query its native command catalog.";
    private bool _showConsoleVariables;
    private bool _isRefreshingConsoleCatalog;
    private string _worldConnectionStatus = "WORLD OFFLINE";
    private string _worldConnectionDetails = "Waiting for World status...";
    private Brush _worldConnectionBrush = new SolidColorBrush(Color.FromRgb(126, 139, 153));
    private Task? _worldMonitorTask;
    private bool _allowClose;
    private bool _isClosing;
    private readonly uint? _startupZoneKey;

    public MainWindow(uint? startupZoneKey = null)
    {
        _startupZoneKey = startupZoneKey;
        Settings = ZoneManagerSettingsStore.Load();
        InitializeComponent();
        DataContext = this;
        ZoneView = CollectionViewSource.GetDefaultView(Zones);
        ZoneView.Filter = FilterZone;

        // Sorted view over the live roster, backing the follow-player dropdown.
        WorldPlayersView = new CollectionViewSource { Source = WorldPlayers }.View;
        WorldPlayersView.SortDescriptions.Add(
            new SortDescription(nameof(WorldPlayerStatus.Name), ListSortDirection.Ascending));
        ConsoleEntryView = CollectionViewSource.GetDefaultView(ConsoleEntries);
        ConsoleEntryView.Filter = FilterConsoleEntry;
        LoadCachedConsoleCatalog();
    }

    public ZoneManagerSettings Settings { get; }
    public ObservableCollection<ZoneRuntimeState> Zones { get; } = [];
    public ObservableCollection<ZoneRuntimeState> LogZones { get; } = [];
    public ObservableCollection<ZoneConsoleEntry> ConsoleEntries { get; } = [];
    public ObservableCollection<WorldPlayerStatus> WorldPlayers { get; } = [];
    public ICollectionView ZoneView { get; }

    /// <summary>Online characters, name-sorted, for the follow-player dropdown.</summary>
    public ICollectionView WorldPlayersView { get; }
    public ICollectionView ConsoleEntryView { get; }

    /// <summary>Zone started first and held for the whole auto-managed session.</summary>
    public ZoneRuntimeState? DefaultZone
    {
        get => _defaultZone;
        set
        {
            if (ReferenceEquals(_defaultZone, value))
                return;
            _defaultZone = value;
            Settings.DefaultZoneKey = value?.Zone.ZoneKey ?? 0;
            OnPropertyChanged(nameof(DefaultZone));
        }
    }

    public string AutoZoneButtonText => _autoZoneRunning ? "Stop auto" : "Start auto";

    /// <summary>Start-template presets offered in the header dropdown.</summary>
    public IReadOnlyList<AutoZoneTemplateChoice> StartTemplates { get; } =
    [
        new("Default zone", AutoZoneStartTemplate.DefaultZone),
        new("All zones with players", AutoZoneStartTemplate.ZonesWithPlayers),
        new("New player start zones", AutoZoneStartTemplate.NewPlayerStartZones),
        new("Follow a player", AutoZoneStartTemplate.SpecifiedPlayer)
    ];

    private string _autoZoneStatus = "Auto zone management off";

    public string AutoZoneStatus
    {
        get => _autoZoneStatus;
        private set { _autoZoneStatus = value; OnPropertyChanged(nameof(AutoZoneStatus)); }
    }

    public ZoneRuntimeState? SelectedZone
    {
        get => _selectedZone;
        set
        {
            if (ReferenceEquals(_selectedZone, value))
                return;
            _selectedZone = value;
            OnPropertyChanged(nameof(SelectedZone));
            LoadSelectedZoneMap();
            HighlightSelectedMarker();
            if (IsLoaded)
                Dispatcher.BeginInvoke(FocusSelectedZoneOnMap, DispatcherPriority.Loaded);
        }
    }

    public ImageSource? WorldMapSource
    {
        get => _worldMapSource;
        private set { _worldMapSource = value; OnPropertyChanged(nameof(WorldMapSource)); }
    }

    public ImageSource? SelectedZoneMapSource
    {
        get => _selectedZoneMapSource;
        private set { _selectedZoneMapSource = value; OnPropertyChanged(nameof(SelectedZoneMapSource)); }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            _searchText = value;
            OnPropertyChanged(nameof(SearchText));
            ZoneView.Refresh();
            ZoneCountText = $"{ZoneView.Cast<object>().Count()} / {Zones.Count}";
        }
    }

    public string ZoneCountText
    {
        get => _zoneCountText;
        private set { _zoneCountText = value; OnPropertyChanged(nameof(ZoneCountText)); }
    }

    public string MapTitleText
    {
        get => _mapTitleText;
        private set { _mapTitleText = value; OnPropertyChanged(nameof(MapTitleText)); }
    }

    public string ConsoleCommandSearchText
    {
        get => _consoleCommandSearchText;
        set
        {
            _consoleCommandSearchText = value;
            OnPropertyChanged(nameof(ConsoleCommandSearchText));
            ConsoleEntryView.Refresh();
            UpdateConsoleCommandStatus();
        }
    }

    public string ConsoleCommandText
    {
        get => _consoleCommandText;
        set { _consoleCommandText = value; OnPropertyChanged(nameof(ConsoleCommandText)); }
    }

    public string ConsoleCommandStatus
    {
        get => _consoleCommandStatus;
        private set { _consoleCommandStatus = value; OnPropertyChanged(nameof(ConsoleCommandStatus)); }
    }

    public bool ShowConsoleVariables
    {
        get => _showConsoleVariables;
        set
        {
            _showConsoleVariables = value;
            OnPropertyChanged(nameof(ShowConsoleVariables));
            ConsoleEntryView.Refresh();
            UpdateConsoleCommandStatus();
        }
    }

    public bool IsRefreshingConsoleCatalog
    {
        get => _isRefreshingConsoleCatalog;
        private set { _isRefreshingConsoleCatalog = value; OnPropertyChanged(nameof(IsRefreshingConsoleCatalog)); }
    }

    public string WorldConnectionStatus
    {
        get => _worldConnectionStatus;
        private set { _worldConnectionStatus = value; OnPropertyChanged(nameof(WorldConnectionStatus)); }
    }

    public string WorldConnectionDetails
    {
        get => _worldConnectionDetails;
        private set { _worldConnectionDetails = value; OnPropertyChanged(nameof(WorldConnectionDetails)); }
    }

    public Brush WorldConnectionBrush
    {
        get => _worldConnectionBrush;
        private set { _worldConnectionBrush = value; OnPropertyChanged(nameof(WorldConnectionBrush)); }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ReloadCatalog();
        _worldMonitorTask = MonitorWorldAsync(_worldMonitorCancellation.Token);
        if (_startupZoneKey is not { } zoneKey)
            return;
        SelectedZone = Zones.FirstOrDefault(zone => zone.Zone.ZoneKey == zoneKey);
        if (SelectedZone is null)
        {
            MessageBox.Show(this, $"Zone {zoneKey} was not found in the catalog.", "Unable to launch zone",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        ZoneList.ScrollIntoView(SelectedZone);
        await LaunchSelectedZoneAsync();
    }

    private void ReloadCatalog_Click(object sender, RoutedEventArgs e) => ReloadCatalog();

    private void ReloadCatalog()
    {
        if (!EnsureCompactDatabaseConfigured())
            return;

        try
        {
            ZoneManagerSettingsStore.Save(Settings);
            var catalog = _catalogService.Load(Settings.ZoneCatalogPath, Settings.CompactDatabasePath);
            var priorStates = Zones.ToDictionary(item => item.Zone.ZoneKey);
            Zones.Clear();
            foreach (var zone in catalog.Zones)
                Zones.Add(priorStates.GetValueOrDefault(zone.ZoneKey) ?? new ZoneRuntimeState(zone));

            _groups = catalog.Groups;
            _mapResources = catalog.MapResources;
            _worldMap = catalog.WorldMap;
            _zoneCoverage = _zoneSectorCoverageService.Load(
                System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "main_world_zone_sectors.tsv"));
            ZoneView.Refresh();
            ZoneCountText = $"{ZoneView.Cast<object>().Count()} / {Zones.Count}";
            NavigateToMap(_worldMap?.FolderName ?? "main_world");

            SelectedZone ??= Zones.FirstOrDefault(zone => zone.Zone.ZoneKey == 182) ?? Zones.FirstOrDefault();
            // Rebind the saved default to the fresh state instance the catalog reload just created.
            DefaultZone = Zones.FirstOrDefault(zone => zone.Zone.ZoneKey == Settings.DefaultZoneKey)
                          ?? DefaultZone;
            ZoneList.ScrollIntoView(SelectedZone);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Unable to load zones", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool EnsureCompactDatabaseConfigured()
    {
        if (!string.IsNullOrWhiteSpace(Settings.CompactDatabasePath) &&
            File.Exists(Settings.CompactDatabasePath))
        {
            Settings.CompactDatabaseConfigured = true;
            return true;
        }

        Settings.CompactDatabaseConfigured = false;
        Settings.CompactDatabasePath = string.Empty;
        OnPropertyChanged(nameof(Settings));
        return SelectCompactDatabase();
    }

    private bool SelectCompactDatabase()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the decrypted game database",
            Filter = "Decrypted game database|*.sqlite3;*.sqlite;*.db|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(Settings.CompactDatabasePath))
            dialog.FileName = Settings.CompactDatabasePath;
        if (dialog.ShowDialog(this) != true)
            return false;

        Settings.CompactDatabasePath = dialog.FileName;
        Settings.CompactDatabaseConfigured = true;
        OnPropertyChanged(nameof(Settings));
        ZoneManagerSettingsStore.Save(Settings);
        return true;
    }

    private bool FilterZone(object item)
    {
        if (item is not ZoneRuntimeState state || string.IsNullOrWhiteSpace(SearchText))
            return true;

        var search = SearchText.Trim();
        return state.Zone.ZoneKey.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
               state.Zone.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               (state.Zone.GroupId?.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void RenderMap()
    {
        WorldMapCanvas.Children.Clear();
        _mapMarkers.Clear();
        _selectedCoverageBounds = null;
        if (WorldMapSource is BitmapSource worldSource)
        {
            WorldMapCanvas.Width = worldSource.PixelWidth;
            WorldMapCanvas.Height = worldSource.PixelHeight;
            var mapImage = new Image
            {
                Source = WorldMapSource,
                Width = worldSource.PixelWidth,
                Height = worldSource.PixelHeight,
                Stretch = Stretch.Fill,
                Opacity = 1,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(mapImage, -10);
            WorldMapCanvas.Children.Add(mapImage);
        }

        if (_currentMapFolder.Equals("main_world", StringComparison.OrdinalIgnoreCase) &&
            WorldMapSource is BitmapSource overviewSource &&
            RenderWorldGroupOverview(overviewSource))
        {
            return;
        }

        var detailGroup = _groups.FirstOrDefault(group =>
            group.MapFolderName.Equals(_currentMapFolder, StringComparison.OrdinalIgnoreCase));
        if (detailGroup is not null && WorldMapSource is BitmapSource detailSource)
        {
            RenderZoneCoverage(detailGroup, detailSource);
            return;
        }

        var children = _mapResources
            .Where(resource => ParentMapFolder(resource.WorldOverImagePath)
                .Equals(_currentMapFolder, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var overImagePath = children
            .Select(resource => resource.WorldOverImagePath)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        var atlas = _mapAssetService.LoadOverlayAtlas(
            Settings.MapAssetsPath,
            overImagePath,
            Settings.Locale);
        var segments = _mapAssetService.LoadOverlaySegments(Settings.MapAssetsPath, overImagePath);
        if (atlas is null || segments.Count == 0 || children.Length == 0)
        {
            if (WorldMapSource is null)
                RenderFallbackMap();
            return;
        }

        foreach (var resource in children)
        {
            if (!segments.TryGetValue($"{resource.FolderName}_over", out var segment))
                continue;

            var groupId = ResourceGroupId(resource);
            var groupZones = groupId is { } linkedGroupId
                ? Zones.Where(zone => zone.Zone.GroupId == linkedGroupId).ToArray()
                : [];
            var croppedOverlay = MapAssetService.CropOverlay(atlas, segment);
            if (croppedOverlay is null)
                continue;
            var selected = IsSelectedResource(resource);
            var overlay = new Image
            {
                Source = croppedOverlay,
                Width = segment.Width,
                Height = segment.Height,
                Stretch = Stretch.Fill,
                Opacity = selected ? 0.82 : 0.16,
                Cursor = Cursors.Hand,
                ToolTip = BuildMapResourceToolTip(resource, groupZones.Length),
                Effect = selected
                    ? new DropShadowEffect
                    {
                        Color = Color.FromRgb(255, 177, 66),
                        ShadowDepth = 0,
                        BlurRadius = 12,
                        Opacity = 1
                    }
                    : null
            };
            overlay.MouseEnter += (_, _) => overlay.Opacity = 0.72;
            overlay.MouseLeave += (_, _) => overlay.Opacity = IsSelectedResource(resource) ? 0.82 : 0.16;
            overlay.MouseLeftButtonDown += (_, args) =>
            {
                ActivateMapResource(resource, groupZones);
                args.Handled = true;
            };
            Canvas.SetLeft(overlay, segment.OffsetX);
            Canvas.SetTop(overlay, segment.OffsetY);
            Panel.SetZIndex(overlay, 0);
            WorldMapCanvas.Children.Add(overlay);

            if (selected)
            {
                var zoomBoundary = new Rectangle
                {
                    Width = segment.Width + 8,
                    Height = segment.Height + 8,
                    Fill = Brushes.Transparent,
                    Stroke = new SolidColorBrush(Color.FromRgb(255, 177, 66)),
                    StrokeThickness = 2,
                    StrokeDashArray = [5, 3],
                    IsHitTestVisible = false,
                    ToolTip = $"Zone {SelectedZone?.Zone.ZoneKey} boundary"
                };
                Canvas.SetLeft(zoomBoundary, segment.OffsetX - 4);
                Canvas.SetTop(zoomBoundary, segment.OffsetY - 4);
                Panel.SetZIndex(zoomBoundary, 4);
                WorldMapCanvas.Children.Add(zoomBoundary);
            }

            if (segment.Width > 90 && segment.Height > 34)
            {
                var label = new TextBlock
                {
                    Text = resource.FolderName,
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromArgb(210, 225, 244, 250)),
                    Background = new SolidColorBrush(Color.FromArgb(125, 8, 24, 40)),
                    Padding = new Thickness(3, 1, 3, 1),
                    IsHitTestVisible = false,
                    MaxWidth = Math.Max(20, segment.Width - 8),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Canvas.SetLeft(label, segment.OffsetX + 4);
                Canvas.SetTop(label, segment.OffsetY + 4);
                Panel.SetZIndex(label, 2);
                WorldMapCanvas.Children.Add(label);
            }

            for (var index = 0; index < groupZones.Length; index++)
            {
                var marker = CreateMarker(groupZones[index]);
                var columns = Math.Max(1, (int)Math.Sqrt(groupZones.Length));
                var markerLeft = segment.OffsetX + 5 + (index % columns) * 13;
                var markerTop = segment.OffsetY + Math.Min(segment.Height - 15, 23 + (index / columns) * 13);
                Canvas.SetLeft(marker, Math.Min(segment.OffsetX + segment.Width - 13, markerLeft));
                Canvas.SetTop(marker, Math.Min(segment.OffsetY + segment.Height - 13, markerTop));
                WorldMapCanvas.Children.Add(marker);
            }
        }

        HighlightSelectedMarker();
    }

    private bool RenderWorldGroupOverview(BitmapSource mapSource)
    {
        if (_worldMap is null || _worldMap.Width <= 0 || _worldMap.Height <= 0)
            return false;

        var rendered = false;
        foreach (var group in _groups)
        {
            var groupZones = Zones.Where(zone => zone.Zone.GroupId == group.Id).ToArray();
            if (groupZones.Length == 0 || !groupZones.Any(zone => _zoneCoverage.ContainsKey(zone.Zone.ZoneKey)))
                continue;

            var bounds = WorldToOverviewMap(group, _worldMap, mapSource.PixelWidth, mapSource.PixelHeight);
            bounds.Intersect(new Rect(0, 0, mapSource.PixelWidth, mapSource.PixelHeight));
            if (bounds.IsEmpty || bounds.Width < 2 || bounds.Height < 2)
                continue;

            var selected = group.Id == SelectedZone?.Zone.GroupId;
            var overlay = new Rectangle
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Fill = new SolidColorBrush(Color.FromArgb(selected ? (byte)76 : (byte)24, 47, 196, 219)),
                Stroke = new SolidColorBrush(selected
                    ? Color.FromRgb(255, 177, 66)
                    : Color.FromArgb(170, 85, 217, 233)),
                StrokeThickness = selected ? 2.5 : 1,
                Cursor = Cursors.Hand,
                ToolTip = $"{group.MapFolderName}\nGroup {group.Id}, {groupZones.Length} zone partition(s)"
            };
            overlay.MouseEnter += (_, _) => overlay.Fill = new SolidColorBrush(Color.FromArgb(92, 47, 196, 219));
            overlay.MouseLeave += (_, _) => overlay.Fill = new SolidColorBrush(Color.FromArgb(
                group.Id == SelectedZone?.Zone.GroupId ? (byte)76 : (byte)24, 47, 196, 219));
            overlay.MouseLeftButtonDown += (_, args) =>
            {
                var zone = groupZones.FirstOrDefault(item => item.Zone.ZoneKey == SelectedZone?.Zone.ZoneKey)
                           ?? groupZones[0];
                SelectZone(zone);
                args.Handled = true;
            };
            Canvas.SetLeft(overlay, bounds.Left);
            Canvas.SetTop(overlay, bounds.Top);
            Panel.SetZIndex(overlay, 1);
            WorldMapCanvas.Children.Add(overlay);
            rendered = true;
        }

        MapTitleText = "World map / main_world - click a zone group to inspect its partitions";
        return rendered;
    }

    private void RenderZoneCoverage(ZoneGroupDefinition group, BitmapSource mapSource)
    {
        const double sectorSize = 64;
        var groupZones = Zones
            .Where(zone => zone.Zone.GroupId == group.Id)
            .OrderBy(zone => zone.Zone.ZoneKey)
            .ToArray();
        var availableZones = groupZones
            .Where(zone => _zoneCoverage.ContainsKey(zone.Zone.ZoneKey))
            .ToArray();

        if (availableZones.Length == 0)
        {
            RenderUnavailableCoverage(groupZones, mapSource);
            return;
        }

        var palette = new[]
        {
            Color.FromRgb(46, 203, 224),
            Color.FromRgb(99, 214, 137),
            Color.FromRgb(178, 126, 245),
            Color.FromRgb(244, 213, 91),
            Color.FromRgb(237, 112, 174),
            Color.FromRgb(92, 145, 245)
        };

        for (var zoneIndex = 0; zoneIndex < availableZones.Length; zoneIndex++)
        {
            var state = availableZones[zoneIndex];
            var runs = _zoneCoverage[state.Zone.ZoneKey];
            var selected = state.Zone.ZoneKey == SelectedZone?.Zone.ZoneKey;
            var color = selected ? Color.FromRgb(255, 166, 52) : palette[zoneIndex % palette.Length];
            var occupied = new HashSet<(int X, int Y)>();
            Rect? zoneBounds = null;

            foreach (var run in runs)
            for (var x = run.StartSectorX; x < run.StartSectorX + run.SectorCount; x++)
                occupied.Add((x, run.SectorY));

            foreach (var block in MergeCoverageRuns(runs))
            {
                var worldX = block.StartSectorX * sectorSize;
                var worldY = block.StartSectorY * sectorSize;
                var runBounds = WorldToZoneMap(
                    group,
                    worldX,
                    worldY,
                    block.SectorCountX * sectorSize,
                    block.SectorCountY * sectorSize,
                    mapSource.PixelWidth,
                    mapSource.PixelHeight);
                runBounds.Intersect(new Rect(0, 0, mapSource.PixelWidth, mapSource.PixelHeight));
                if (runBounds.IsEmpty)
                    continue;

                var fill = new Rectangle
                {
                    Width = runBounds.Width + 0.5,
                    Height = runBounds.Height + 0.5,
                    Fill = new SolidColorBrush(Color.FromArgb(selected ? (byte)105 : (byte)58, color.R, color.G, color.B)),
                    Cursor = Cursors.Hand,
                    ToolTip = BuildCoverageToolTip(state, occupied.Count)
                };
                fill.MouseLeftButtonDown += (_, args) =>
                {
                    SelectZone(state);
                    args.Handled = true;
                };
                Canvas.SetLeft(fill, runBounds.Left);
                Canvas.SetTop(fill, runBounds.Top);
                Panel.SetZIndex(fill, selected ? 3 : 1);
                WorldMapCanvas.Children.Add(fill);
                zoneBounds = zoneBounds is null ? runBounds : Rect.Union(zoneBounds.Value, runBounds);
            }

            var outline = BuildSectorOutline(group, occupied, mapSource.PixelWidth, mapSource.PixelHeight);
            if (!outline.IsEmpty())
            {
                var path = new System.Windows.Shapes.Path
                {
                    Data = outline,
                    Fill = Brushes.Transparent,
                    Stroke = new SolidColorBrush(color),
                    StrokeThickness = selected ? 3 : 1.35,
                    Effect = selected
                        ? new DropShadowEffect
                        {
                            Color = color,
                            ShadowDepth = 0,
                            BlurRadius = 7,
                            Opacity = 0.85
                        }
                        : null,
                    IsHitTestVisible = false
                };
                Panel.SetZIndex(path, selected ? 6 : 4);
                WorldMapCanvas.Children.Add(path);
            }

            if (zoneBounds is { } labelBounds)
            {
                AddCoverageLabel(state, labelBounds, color, selected);
                if (selected)
                    _selectedCoverageBounds = labelBounds;
            }
        }

        var groupFrame = new Rectangle
        {
            Width = mapSource.PixelWidth - 2,
            Height = mapSource.PixelHeight - 2,
            Stroke = new SolidColorBrush(Color.FromArgb(180, 211, 236, 241)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(groupFrame, 1);
        Canvas.SetTop(groupFrame, 1);
        Panel.SetZIndex(groupFrame, 8);
        WorldMapCanvas.Children.Add(groupFrame);

        MapTitleText = $"Zone map / {group.MapFolderName} - zone {SelectedZone?.Zone.ZoneKey} highlighted ({availableZones.Length} partition(s))";
    }

    private void RenderUnavailableCoverage(ZoneRuntimeState[] groupZones, BitmapSource mapSource)
    {
        if (groupZones.Length == 1)
        {
            var state = groupZones[0];
            var bounds = new Rect(2, 2, mapSource.PixelWidth - 4, mapSource.PixelHeight - 4);
            var frame = new Rectangle
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Fill = new SolidColorBrush(Color.FromArgb(42, 255, 166, 52)),
                Stroke = new SolidColorBrush(Color.FromRgb(255, 166, 52)),
                StrokeThickness = 3,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(frame, bounds.Left);
            Canvas.SetTop(frame, bounds.Top);
            WorldMapCanvas.Children.Add(frame);
            AddCoverageLabel(state, bounds, Color.FromRgb(255, 166, 52), true);
            _selectedCoverageBounds = bounds;
            MapTitleText = $"Zone map / {_currentMapFolder} - single zone {state.Zone.ZoneKey}";
            return;
        }

        var warning = new TextBlock
        {
            Text = "This map has multiple zone keys, but no authoritative sector data is bundled for this world.",
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(210, 94, 45, 27)),
            Padding = new Thickness(10),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Max(220, mapSource.PixelWidth - 40)
        };
        Canvas.SetLeft(warning, 20);
        Canvas.SetTop(warning, 20);
        WorldMapCanvas.Children.Add(warning);
        MapTitleText = $"Zone map / {_currentMapFolder} - boundary data unavailable";
    }

    private static StreamGeometry BuildSectorOutline(
        ZoneGroupDefinition group,
        HashSet<(int X, int Y)> occupied,
        double mapWidth,
        double mapHeight)
    {
        const double sectorSize = 64;
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        foreach (var (x, y) in occupied)
        {
            var cell = WorldToZoneMap(group, x * sectorSize, y * sectorSize, sectorSize, sectorSize, mapWidth, mapHeight);
            cell.Intersect(new Rect(0, 0, mapWidth, mapHeight));
            if (cell.IsEmpty)
                continue;

            if (!occupied.Contains((x - 1, y)))
                AddGeometryLine(context, new Point(cell.Left, cell.Top), new Point(cell.Left, cell.Bottom));
            if (!occupied.Contains((x + 1, y)))
                AddGeometryLine(context, new Point(cell.Right, cell.Top), new Point(cell.Right, cell.Bottom));
            if (!occupied.Contains((x, y - 1)))
                AddGeometryLine(context, new Point(cell.Left, cell.Bottom), new Point(cell.Right, cell.Bottom));
            if (!occupied.Contains((x, y + 1)))
                AddGeometryLine(context, new Point(cell.Left, cell.Top), new Point(cell.Right, cell.Top));
        }
        geometry.Freeze();
        return geometry;
    }

    private static IEnumerable<(int StartSectorX, int StartSectorY, int SectorCountX, int SectorCountY)>
        MergeCoverageRuns(IReadOnlyList<ZoneSectorRun> runs)
    {
        foreach (var spanGroup in runs.GroupBy(run => (run.StartSectorX, run.SectorCount)))
        {
            var orderedRows = spanGroup.Select(run => run.SectorY).Distinct().OrderBy(y => y).ToArray();
            if (orderedRows.Length == 0)
                continue;

            var startY = orderedRows[0];
            var previousY = startY;
            foreach (var sectorY in orderedRows.Skip(1))
            {
                if (sectorY == previousY + 1)
                {
                    previousY = sectorY;
                    continue;
                }

                yield return (spanGroup.Key.StartSectorX, startY, spanGroup.Key.SectorCount,
                    previousY - startY + 1);
                startY = previousY = sectorY;
            }

            yield return (spanGroup.Key.StartSectorX, startY, spanGroup.Key.SectorCount,
                previousY - startY + 1);
        }
    }

    private static void AddGeometryLine(StreamGeometryContext context, Point start, Point end)
    {
        context.BeginFigure(start, false, false);
        context.LineTo(end, true, false);
    }

    private void AddCoverageLabel(ZoneRuntimeState state, Rect bounds, Color color, bool selected)
    {
        var label = new TextBlock
        {
            Text = $"{state.Zone.ZoneKey}  {state.Zone.Name}",
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(selected ? (byte)230 : (byte)185, 7, 18, 29)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = selected ? 12 : 10,
            FontWeight = selected ? FontWeights.Bold : FontWeights.Normal,
            Padding = new Thickness(4, 2, 4, 2),
            IsHitTestVisible = false,
            Effect = selected
                ? new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 6, Opacity = 0.9 }
                : null
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, Math.Clamp(bounds.Left + bounds.Width / 2 - label.DesiredSize.Width / 2, 2, Math.Max(2, WorldMapCanvas.Width - label.DesiredSize.Width - 2)));
        Canvas.SetTop(label, Math.Clamp(bounds.Top + bounds.Height / 2 - label.DesiredSize.Height / 2, 2, Math.Max(2, WorldMapCanvas.Height - label.DesiredSize.Height - 2)));
        Panel.SetZIndex(label, selected ? 10 : 7);
        WorldMapCanvas.Children.Add(label);
    }

    private static string BuildCoverageToolTip(ZoneRuntimeState state, int sectorCount) =>
        $"Zone {state.Zone.ZoneKey}\n{state.Zone.Name}\n{sectorCount} authoritative 64 m sector(s)";

    private static Rect WorldToOverviewMap(
        ZoneGroupDefinition group,
        WorldMapDefinition world,
        double mapWidth,
        double mapHeight)
    {
        var imageScaleX = mapWidth / 928d;
        var imageScaleY = mapHeight / 556d;
        var left = (world.ImageX + (group.X - world.X) / world.Width * world.ImageWidth) * imageScaleX;
        var right = (world.ImageX + (group.X + group.Width - world.X) / world.Width * world.ImageWidth) * imageScaleX;
        var top = (world.ImageY + (world.Y + world.Height - (group.Y + group.Height)) / world.Height * world.ImageHeight) * imageScaleY;
        var bottom = (world.ImageY + (world.Y + world.Height - group.Y) / world.Height * world.ImageHeight) * imageScaleY;
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    private static Rect WorldToZoneMap(
        ZoneGroupDefinition group,
        double worldX,
        double worldY,
        double worldWidth,
        double worldHeight,
        double mapWidth,
        double mapHeight)
    {
        var left = (worldX - group.X) / group.Width * mapWidth;
        var right = (worldX + worldWidth - group.X) / group.Width * mapWidth;
        var top = (group.Y + group.Height - (worldY + worldHeight)) / group.Height * mapHeight;
        var bottom = (group.Y + group.Height - worldY) / group.Height * mapHeight;
        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    private void SelectZone(ZoneRuntimeState state)
    {
        SelectedZone = state;
        ZoneList.SelectedItem = state;
        ZoneList.ScrollIntoView(state);
    }

    private void NavigateToMap(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return;

        var mapResource = FindMapResource(folderName);
        if (mapResource is null)
            return;

        var source = _mapAssetService.LoadWorldMap(
            Settings.MapAssetsPath,
            mapResource.FolderName,
            Settings.Locale);
        if (source is null)
            return;

        var changedMap = !_currentMapFolder.Equals(folderName, StringComparison.OrdinalIgnoreCase);
        _currentMapFolder = folderName;
        WorldMapSource = source;
        MapTitleText = $"World map / {folderName}";
        RenderMap();
        if (changedMap)
        {
            MapZoom.Value = 0.65;
            MapScrollViewer.ScrollToHorizontalOffset(0);
            MapScrollViewer.ScrollToVerticalOffset(0);
        }
    }

    private void FocusSelectedZoneOnMap()
    {
        if (SelectedZone?.Zone.GroupId is not { } groupId)
            return;

        var resource = _mapResources.FirstOrDefault(item =>
            item.TargetType.Equals("ZoneGroup", StringComparison.OrdinalIgnoreCase) &&
            item.TargetId == groupId);
        if (resource is null)
            return;

        if (!resource.FolderName.Equals(_currentMapFolder, StringComparison.OrdinalIgnoreCase))
            NavigateToMap(resource.FolderName);
        else
            RenderMap();
        if (!resource.FolderName.Equals(_currentMapFolder, StringComparison.OrdinalIgnoreCase))
            return;

        if (_selectedCoverageBounds is { } bounds)
            Dispatcher.BeginInvoke(() => ZoomToBounds(bounds), DispatcherPriority.Loaded);
    }

    private void ZoomToBounds(Rect bounds)
    {
        var viewportWidth = MapScrollViewer.ViewportWidth;
        var viewportHeight = MapScrollViewer.ViewportHeight;
        if (viewportWidth <= 1 || viewportHeight <= 1 || bounds.IsEmpty)
            return;

        const double padding = 72;
        var scale = Math.Min(
            viewportWidth / (bounds.Width + padding),
            viewportHeight / (bounds.Height + padding));
        MapZoom.Value = Math.Clamp(scale, MapZoom.Minimum, MapZoom.Maximum);
        MapScrollViewer.UpdateLayout();

        var centerX = (bounds.Left + bounds.Width / 2d) * MapZoom.Value;
        var centerY = (bounds.Top + bounds.Height / 2d) * MapZoom.Value;
        MapScrollViewer.ScrollToHorizontalOffset(Math.Max(0, centerX - MapScrollViewer.ViewportWidth / 2d));
        MapScrollViewer.ScrollToVerticalOffset(Math.Max(0, centerY - MapScrollViewer.ViewportHeight / 2d));
    }

    private void ZoomToSegment(MapOverlaySegment segment)
    {
        var viewportWidth = MapScrollViewer.ViewportWidth;
        var viewportHeight = MapScrollViewer.ViewportHeight;
        if (viewportWidth <= 1 || viewportHeight <= 1)
            return;

        const double padding = 96;
        var scale = Math.Min(
            viewportWidth / (segment.Width + padding),
            viewportHeight / (segment.Height + padding));
        MapZoom.Value = Math.Clamp(scale, MapZoom.Minimum, MapZoom.Maximum);
        MapScrollViewer.UpdateLayout();

        var centerX = (segment.OffsetX + segment.Width / 2d) * MapZoom.Value;
        var centerY = (segment.OffsetY + segment.Height / 2d) * MapZoom.Value;
        MapScrollViewer.ScrollToHorizontalOffset(Math.Max(0, centerX - MapScrollViewer.ViewportWidth / 2d));
        MapScrollViewer.ScrollToVerticalOffset(Math.Max(0, centerY - MapScrollViewer.ViewportHeight / 2d));
    }

    private void ActivateMapResource(MapResourceDefinition resource, IReadOnlyList<ZoneRuntimeState> groupZones)
    {
        if (groupZones.FirstOrDefault() is { } state)
        {
            SelectedZone = state;
            ZoneList.SelectedItem = state;
            ZoneList.ScrollIntoView(state);
        }

        if (resource.TargetType.Equals("ZoneGroup", StringComparison.OrdinalIgnoreCase))
        {
            NavigateToMap(resource.FolderName);
        }
        else if (resource.TargetType.Equals("WorldGroup", StringComparison.OrdinalIgnoreCase) ||
            _mapResources.Any(child => ParentMapFolder(child.WorldOverImagePath)
                .Equals(resource.FolderName, StringComparison.OrdinalIgnoreCase)))
        {
            NavigateToMap(resource.FolderName);
        }
    }

    private bool IsSelectedResource(MapResourceDefinition resource) =>
        ResourceGroupId(resource) == SelectedZone?.Zone.GroupId;

    private static string BuildMapResourceToolTip(MapResourceDefinition resource, int zoneCount) =>
        resource.TargetType.Equals("WorldGroup", StringComparison.OrdinalIgnoreCase)
            ? $"Open {resource.FolderName}"
            : $"{resource.FolderName}\nGroup {ResourceGroupId(resource)?.ToString() ?? "unmapped"}, {zoneCount} zone(s)";

    private static int? ResourceGroupId(MapResourceDefinition resource) =>
        resource.TargetType.Equals("ZoneGroup", StringComparison.OrdinalIgnoreCase)
            ? resource.TargetId
            : resource.LinkedZoneGroupId;

    private static string ParentMapFolder(string? overImagePath)
    {
        if (string.IsNullOrWhiteSpace(overImagePath))
            return string.Empty;
        var normalized = overImagePath.Replace('\\', '/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex < 0 ? string.Empty : normalized[..separatorIndex];
    }

    private MapResourceDefinition? FindMapResource(string folderName) =>
        _mapResources.FirstOrDefault(resource =>
            resource.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));

    private void MapBack_Click(object sender, RoutedEventArgs e)
    {
        NavigateToMap("main_world");
    }

    private void LoadSelectedZoneMap()
    {
        var group = _groups.FirstOrDefault(item => item.Id == SelectedZone?.Zone.GroupId);
        var mapResource = group is null ? null : FindMapResource(group.MapFolderName);
        SelectedZoneMapSource = mapResource is null
            ? null
            : _mapAssetService.LoadWorldMap(Settings.MapAssetsPath, mapResource.FolderName, Settings.Locale);
    }

    private void RenderFallbackMap()
    {
        DrawGrid();
        const int columns = 20;
        const double cellWidth = 49;
        const double cellHeight = 38;
        for (var index = 0; index < Zones.Count; index++)
        {
            var marker = CreateMarker(Zones[index], 28);
            marker.Content = Zones[index].Zone.ZoneKey;
            Canvas.SetLeft(marker, 25 + (index % columns) * cellWidth);
            Canvas.SetTop(marker, 25 + (index / columns) * cellHeight);
            WorldMapCanvas.Children.Add(marker);
        }
        HighlightSelectedMarker();
    }

    private Button CreateMarker(ZoneRuntimeState state, double size = 12)
    {
        var marker = new Button
        {
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(109, 225, 240)),
            Background = new SolidColorBrush(Color.FromRgb(32, 150, 179)),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 9,
            Tag = state,
            ToolTip = $"{state.Zone.ZoneKey} · {state.Zone.Name}\nGroup {state.Zone.GroupId?.ToString() ?? "unmapped"}"
        };
        marker.Click += MapMarker_Click;
        _mapMarkers[state.Zone.ZoneKey] = marker;
        return marker;
    }

    private void DrawGrid()
    {
        for (double x = 0; x < WorldMapCanvas.Width; x += 70)
        {
            var line = new Line { X1 = x, X2 = x, Y1 = 0, Y2 = WorldMapCanvas.Height, Stroke = new SolidColorBrush(Color.FromArgb(24, 105, 160, 190)), StrokeThickness = 1, IsHitTestVisible = false };
            WorldMapCanvas.Children.Add(line);
        }
        for (double y = 0; y < WorldMapCanvas.Height; y += 70)
        {
            var line = new Line { X1 = 0, X2 = WorldMapCanvas.Width, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromArgb(24, 105, 160, 190)), StrokeThickness = 1, IsHitTestVisible = false };
            WorldMapCanvas.Children.Add(line);
        }
    }

    private void MapMarker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ZoneRuntimeState state })
        {
            SelectedZone = state;
            ZoneList.SelectedItem = state;
            ZoneList.ScrollIntoView(state);
        }
    }

    private void HighlightSelectedMarker()
    {
        foreach (var (zoneKey, marker) in _mapMarkers)
        {
            var selected = zoneKey == SelectedZone?.Zone.ZoneKey;
            marker.Background = selected
                ? new SolidColorBrush(Color.FromRgb(255, 176, 65))
                : new SolidColorBrush(Color.FromRgb(32, 150, 179));
            marker.BorderBrush = selected
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(109, 225, 240));
            Panel.SetZIndex(marker, selected ? 10 : 1);
        }
    }

    private async void LaunchZone_Click(object sender, RoutedEventArgs e)
    {
        await LaunchSelectedZoneAsync();
    }

    private async Task LaunchSelectedZoneAsync()
    {
        var state = SelectedZone;
        if (state is null)
            return;
        try
        {
            ZoneManagerSettingsStore.Save(Settings);
            AddLogZone(state);
            await _processService.LaunchAsync(state, Settings);
            state.WorldConnectionStatus = "Waiting for World handshake";
            ConsoleCommandStatus = "Zone is running. Wait for 'Load Zone Finished', then refresh the native list.";
        }
        catch (Exception exception)
        {
            state.Status = "Failed";
            state.AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [ERROR] {exception.Message}");
            MessageBox.Show(this, exception.Message, "Unable to launch zone", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopZone_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedZone is not null)
            await _processService.StopAsync(SelectedZone);
    }

    private async void RestartZone_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedZone is null)
            return;
        try
        {
            ZoneManagerSettingsStore.Save(Settings);
            AddLogZone(SelectedZone);
            await _processService.RestartAsync(SelectedZone, Settings);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Unable to restart zone", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddLogZone(ZoneRuntimeState state)
    {
        if (!LogZones.Contains(state))
            LogZones.Insert(0, state);
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedZone is null)
            return;
        var nativeArguments = ZoneCommandLineBuilder.Build(SelectedZone.Zone, Settings);
        SelectedZone.CommandPreview = ZoneCommandLineBuilder.FormatPreview(
            Settings.NativeHostExecutablePath,
            nativeArguments);
        Clipboard.SetText(SelectedZone.CommandPreview);
    }

    private void LoadCachedConsoleCatalog()
    {
        try
        {
            ReplaceConsoleEntries(_consoleCatalogService.LoadCached(Settings.RuntimeRoot));
        }
        catch (Exception exception)
        {
            ConsoleCommandStatus = $"Cached command catalog could not be read: {exception.Message}";
        }
    }

    private async void RefreshConsoleCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedZone is not { IsRunning: true } state || IsRefreshingConsoleCatalog)
            return;

        IsRefreshingConsoleCatalog = true;
        ConsoleCommandStatus = $"Requesting {ZoneConsoleCatalogService.DumpCommand} from zone {state.Zone.ZoneKey}...";
        try
        {
            var entries = await _consoleCatalogService.RefreshAsync(state, Settings, _processService);
            ReplaceConsoleEntries(entries);
            UpdateConsoleCommandStatus("Native catalog refreshed");
        }
        catch (Exception exception)
        {
            ConsoleCommandStatus = exception.Message;
            state.AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [ERROR] Console catalog: {exception.Message}");
        }
        finally
        {
            IsRefreshingConsoleCatalog = false;
        }
    }

    private void SendConsoleCommand_Click(object sender, RoutedEventArgs e) => SendSelectedConsoleCommand();

    private void ConsoleCommandInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        SendSelectedConsoleCommand();
    }

    private void SendSelectedConsoleCommand()
    {
        if (SelectedZone is not { IsRunning: true } state)
            return;

        var command = ConsoleCommandText.Trim();
        if (command.Length == 0)
            return;
        if (RequiresConsoleConfirmation(command))
        {
            var result = MessageBox.Show(this,
                $"Run this potentially destructive native command on zone {state.Zone.ZoneKey}?\n\n{command}",
                "Confirm native console command", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;
        }

        try
        {
            _processService.SendConsoleCommand(state, command);
            ConsoleCommandStatus = $"Sent to zone {state.Zone.ZoneKey} at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception exception)
        {
            ConsoleCommandStatus = exception.Message;
            MessageBox.Show(this, exception.Message, "Unable to run console command",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ConsoleCommandList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ZoneConsoleEntry entry })
            ConsoleCommandText = entry.Name;
    }

    private void ReplaceConsoleEntries(IEnumerable<ZoneConsoleEntry> entries)
    {
        ConsoleEntries.Clear();
        foreach (var entry in entries)
            ConsoleEntries.Add(entry);
        ConsoleEntryView.Refresh();
        UpdateConsoleCommandStatus();
    }

    private bool FilterConsoleEntry(object item)
    {
        if (item is not ZoneConsoleEntry entry)
            return false;
        if (!ShowConsoleVariables && entry.Kind != ZoneConsoleEntryKind.Command)
            return false;
        if (string.IsNullOrWhiteSpace(ConsoleCommandSearchText))
            return true;

        var search = ConsoleCommandSearchText.Trim();
        return entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.Syntax.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.Help.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateConsoleCommandStatus(string? prefix = null)
    {
        var commands = ConsoleEntries.Count(entry => entry.Kind == ZoneConsoleEntryKind.Command);
        var variables = ConsoleEntries.Count - commands;
        var visible = ConsoleEntryView.Cast<object>().Count();
        ConsoleCommandStatus = $"{(prefix is null ? string.Empty : prefix + ". ")}" +
            $"{visible} shown; {commands} commands and {variables} variables cached outside the repository.";
    }

    private static bool RequiresConsoleConfirmation(string command)
    {
        var name = command.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.ToLowerInvariant() ?? string.Empty;
        return name is "quit" or "exit" or "shutdown" or "crash" or "reset" ||
               name.Contains("delete", StringComparison.Ordinal) ||
               name.Contains("remove", StringComparison.Ordinal) ||
               name.Contains("demolish", StringComparison.Ordinal) ||
               name.Contains("destroy", StringComparison.Ordinal);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ZoneRuntimeState state })
            state.ClearLog();
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ZoneRuntimeState state } && state.LogFilePath is { } path)
            OpenPath(System.IO.Path.GetDirectoryName(path)!);
    }

    private void OpenRuntime_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Settings.RuntimeRoot);
        OpenPath(Settings.RuntimeRoot);
    }

    private void OpenMapFiles_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(Settings.MapAssetsPath))
            OpenPath(Settings.MapAssetsPath);
    }

    private void BrowseMapFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the extracted client UI map root containing map_resources",
            InitialDirectory = Settings.MapAssetsPath
        };
        if (dialog.ShowDialog(this) != true)
            return;

        Settings.MapAssetsPath = dialog.FolderName;
        OnPropertyChanged(nameof(Settings));
        ZoneManagerSettingsStore.Save(Settings);
        NavigateToMap(_currentMapFolder);
        LoadSelectedZoneMap();
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Native zone game library|x2game-dev_dedicate.dll|Dynamic libraries|*.dll|All files|*.*",
            FileName = Settings.NativeGameDllPath
        };
        if (dialog.ShowDialog(this) == true)
        {
            Settings.NativeGameDllPath = dialog.FileName;
            Settings.WorkingDirectory = System.IO.Path.GetDirectoryName(dialog.FileName)!;
            OnPropertyChanged(nameof(Settings));
        }
    }

    private void BrowseHost_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "AAEmu native zone host|AAEmu.ZoneHost.exe|Executables|*.exe|All files|*.*",
            FileName = Settings.NativeHostExecutablePath
        };
        if (dialog.ShowDialog(this) == true)
        {
            Settings.NativeHostExecutablePath = dialog.FileName;
            OnPropertyChanged(nameof(Settings));
        }
    }

    private void BrowseDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (SelectCompactDatabase())
            ReloadCatalog();
    }

    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });

    private void ZoneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZoneList.SelectedItem is ZoneRuntimeState selected &&
            !ReferenceEquals(SelectedZone, selected))
        {
            SelectedZone = selected;
        }
        HighlightSelectedMarker();
        FocusSelectedZoneOnMap();
    }

    private void ToggleAutoZone_Click(object sender, RoutedEventArgs e)
    {
        if (_autoZoneRunning)
        {
            _autoZoneRunning = false;
            _autoZoneService.Reset();
            Settings.AutoZoneManagement = false;
            ConsoleCommandStatus = "Auto zone management stopped. Running zones were left alone.";
            OnPropertyChanged(nameof(AutoZoneButtonText));
            ZoneManagerSettingsStore.Save(Settings);
            return;
        }

        var problem = Settings.StartTemplate switch
        {
            AutoZoneStartTemplate.DefaultZone when DefaultZone is null =>
                "Pick a zone for the 'Default zone' template. It is started immediately and kept running.",
            AutoZoneStartTemplate.SpecifiedPlayer when string.IsNullOrWhiteSpace(Settings.FollowPlayerName) =>
                "Enter the character name to follow for the 'Follow a player' template.",
            AutoZoneStartTemplate.NewPlayerStartZones when (Settings.StartZoneKeys?.Count ?? 0) == 0 =>
                "No start zones configured. Set StartZoneKeys in settings.json for this template.",
            _ => null
        };

        if (problem is not null)
        {
            MessageBox.Show(this, problem, "Auto zone management", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var anchor = Settings.StartTemplate switch
        {
            AutoZoneStartTemplate.DefaultZone => DefaultZone!.Zone.Name,
            AutoZoneStartTemplate.SpecifiedPlayer => $"player {Settings.FollowPlayerName.Trim()}",
            AutoZoneStartTemplate.NewPlayerStartZones => $"{Settings.StartZoneKeys!.Count} start zone(s)",
            _ => "players online"
        };

        _autoZoneService.Reset();
        _autoZoneRunning = true;
        Settings.AutoZoneManagement = true;
        ConsoleCommandStatus =
            $"Auto zone management started on {anchor}, ceiling {Math.Max(1, Settings.MaxActiveZones)} zones.";
        OnPropertyChanged(nameof(AutoZoneButtonText));
        ZoneManagerSettingsStore.Save(Settings);
    }

    /// <summary>
    /// Applies one auto-management decision. Runs on the world poll so it sees the same snapshot
    /// the UI does; re-entry is guarded because a cold start takes far longer than the poll.
    /// </summary>
    private async Task ApplyAutoZoneAsync(WorldStatusSnapshot? snapshot)
    {
        if (!_autoZoneRunning || _autoZoneTickBusy)
            return;

        _autoZoneTickBusy = true;
        try
        {
            var running = Zones.Where(zone => zone.IsRunning).Select(zone => zone.Zone.ZoneKey).ToArray();
            var decision = _autoZoneService.Evaluate(
                Settings, _zoneCoverage, snapshot, running, DateTimeOffset.UtcNow);

            foreach (var zoneKey in decision.ToStart)
            {
                var state = Zones.FirstOrDefault(zone => zone.Zone.ZoneKey == zoneKey);
                if (state is null || state.IsRunning)
                    continue;
                AddLogZone(state);
                await _processService.LaunchAsync(state, Settings);
                state.WorldConnectionStatus = "Waiting for World handshake";
                state.AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [AUTO] Started by auto zone management.");
            }

            foreach (var zoneKey in decision.ToStop)
            {
                var state = Zones.FirstOrDefault(zone => zone.Zone.ZoneKey == zoneKey);
                if (state is null || !state.IsRunning)
                    continue;
                state.AppendLog($"[{DateTime.Now:HH:mm:ss.fff}] [AUTO] Stopped: empty and out of range.");
                await _processService.StopAsync(state);
            }

            AutoZoneStatus = decision.Summary;
        }
        catch (Exception exception)
        {
            AutoZoneStatus = $"Auto zone management error: {exception.Message}";
        }
        finally
        {
            _autoZoneTickBusy = false;
        }
    }

    private async Task MonitorWorldAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _worldStatusService.GetAsync(Settings, cancellationToken);
                ApplyWorldStatus(snapshot);
                await ApplyAutoZoneAsync(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ApplyWorldUnavailable(exception.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ApplyWorldStatus(WorldStatusSnapshot snapshot)
    {
        WorldPlayers.Clear();
        foreach (var player in snapshot.Players.OrderBy(player => player.Name))
            WorldPlayers.Add(player);

        var loadedZones = snapshot.Zones.Count(zone =>
            zone.State.Equals("ZoneLoaded", StringComparison.OrdinalIgnoreCase));
        WorldConnectionStatus = "WORLD ONLINE";
        WorldConnectionDetails = $"{snapshot.PlayerCount} player{(snapshot.PlayerCount == 1 ? string.Empty : "s")} · " +
                                 $"{loadedZones} ZoneLoaded · API {Settings.WorldIp}:{Settings.WorldApiPort}";
        WorldConnectionBrush = new SolidColorBrush(Color.FromRgb(79, 209, 139));

        var connections = snapshot.Zones
            .Where(zone => zone.ZoneId != 0)
            .GroupBy(zone => zone.ZoneId)
            .ToDictionary(group => group.Key, group => group.Last());
        foreach (var zone in Zones)
        {
            if (connections.TryGetValue(zone.Zone.ZoneKey, out var connection))
            {
                zone.IsWorldConnected = connection.State.Equals("ZoneLoaded", StringComparison.OrdinalIgnoreCase);
                var ownership = zone.IsRunning ? string.Empty : " · unmanaged process";
                zone.WorldConnectionStatus = $"World: {connection.State} · session {connection.SessionId} · {connection.UnitCount} units{ownership}";
                continue;
            }

            zone.IsWorldConnected = false;
            zone.WorldConnectionStatus = zone.IsRunning
                ? "World: no Zone session"
                : "World: no managed process";
        }
    }

    private void ApplyWorldUnavailable(string reason)
    {
        WorldPlayers.Clear();
        WorldConnectionStatus = "WORLD OFFLINE";
        WorldConnectionDetails = $"Cannot reach {Settings.WorldIp}:{Settings.WorldApiPort} · {reason}";
        WorldConnectionBrush = new SolidColorBrush(Color.FromRgb(224, 92, 92));
        foreach (var zone in Zones)
        {
            zone.IsWorldConnected = false;
            zone.WorldConnectionStatus = zone.IsRunning
                ? "World: status unavailable"
                : "World: zone is not running";
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;
        if (_isClosing)
        {
            e.Cancel = true;
            return;
        }
        if (Zones.All(zone => !zone.IsRunning))
        {
            _processService.Dispose();
            return;
        }

        var result = MessageBox.Show(this,
            "Stop all zones launched by Zone Manager before closing?",
            "Managed zones are running",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }
        if (result == MessageBoxResult.No)
            return;

        e.Cancel = true;
        _isClosing = true;
        IsEnabled = false;
        try
        {
            await _processService.StopAllAsync();
            _processService.Dispose();
            _allowClose = true;

            // Let the cancelled Closing event return before initiating the final close. Calling
            // Close() inline from this async handler can hit WPF's "Window is closing" guard.
            _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.ApplicationIdle);
        }
        catch (Exception exception)
        {
            _isClosing = false;
            IsEnabled = true;
            MessageBox.Show(this, exception.Message, "Unable to stop zones", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _worldMonitorCancellation.Cancel();
        _worldMonitorCancellation.Dispose();
        _worldStatusService.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
