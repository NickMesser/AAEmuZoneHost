using System.IO;
using System.Text.Json;

namespace AAEmu.ZoneManager.Models;

/// <summary>Preset that decides which zones auto-management treats as anchors.</summary>
public enum AutoZoneStartTemplate
{
    /// <summary>One chosen zone, always hosted.</summary>
    DefaultZone,

    /// <summary>Purely reactive — whatever zones players are standing in, and their approaches.</summary>
    ZonesWithPlayers,

    /// <summary>The maps new characters spawn into, so a fresh login always lands somewhere hosted.</summary>
    NewPlayerStartZones,

    /// <summary>Follow one named character; other players are ignored for proximity.</summary>
    SpecifiedPlayer
}

public sealed class ZoneManagerSettings
{
    public string NativeHostExecutablePath { get; set; } = @"C:\AA\Bin64\AAEmu.ZoneHost.exe";
    public string NativeGameDllPath { get; set; } = @"C:\AA\Bin64\x2game-dev_dedicate.dll";
    public string WorkingDirectory { get; set; } = @"C:\AA\Bin64";
    public string ZoneCatalogPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Data", "zone_map_names.txt");
    public string CompactDatabasePath { get; set; } = string.Empty;
    public bool CompactDatabaseConfigured { get; set; }
    public string MapAssetsPath { get; set; } = ZoneManagerSettingsStore.DefaultMapAssetsPath();
    public string RuntimeRoot { get; set; } = @"C:\AAEmuRuntime\ZoneManager";

    public bool Dedicated { get; set; } = true;
    public bool DevMode { get; set; }
    public bool FullDump { get; set; }
    public string WorldIp { get; set; } = "127.0.0.1";
    public int WorldPort { get; set; } = 1240;
    public int WorldApiPort { get; set; } = 1280;
    public string DbLocation { get; set; } = "game/db/game_decrypted.sqlite3";
    public string LocalizedTextsDbLocation { get; set; } = string.Empty;
    public string Locale { get; set; } = "zh_cn";
    public bool SystemDedicatedServer { get; set; } = true;
    public bool DisableRendering { get; set; } = true;
    public string Renderer { get; set; } = "Null";
    public int RenderWidth { get; set; } = 64;
    public int RenderHeight { get; set; } = 64;
    public int PakPriority { get; set; }
    public bool UseFpsLimit { get; set; }
    public int MaxFps { get; set; }
    public int NpcMoveSkipStanding { get; set; }
    public int NpcMoveSkipDisabledAi { get; set; }
    public int NpcMovementSkip { get; set; }
    public int AiSystemUpdate { get; set; } = 1;
    public int LogVerbosity { get; set; } = 3;
    public string ExtraArguments { get; set; } = string.Empty;

    /// <summary>Follow players and start/stop zones automatically.</summary>
    public bool AutoZoneManagement { get; set; }

    /// <summary>Which zones auto-management anchors on. See <see cref="AutoZoneStartTemplate"/>.</summary>
    public AutoZoneStartTemplate StartTemplate { get; set; } = AutoZoneStartTemplate.DefaultZone;

    /// <summary>Zone started first and kept running, for <see cref="AutoZoneStartTemplate.DefaultZone"/>.</summary>
    public uint DefaultZoneKey { get; set; }

    /// <summary>Character followed by <see cref="AutoZoneStartTemplate.SpecifiedPlayer"/>.</summary>
    public string FollowPlayerName { get; set; } = string.Empty;

    /// <summary>
    /// Anchors for <see cref="AutoZoneStartTemplate.NewPlayerStartZones"/>: the exact native
    /// partitions in which freshly created characters of the six playable races spawn.
    /// </summary>
    public List<uint> StartZoneKeys { get; set; } = [179, 129, 184, 187, 328, 157];

    /// <summary>Hard ceiling on concurrently hosted zones while auto-managing.</summary>
    public int MaxActiveZones { get; set; } = 6;

    /// <summary>
    /// How close a player must get to a zone's border before it is started, in metres. A cold
    /// start takes roughly a minute, so this wants to be comfortably more than a minute of travel.
    /// </summary>
    public double AutoZonePrestartMeters { get; set; } = 512;

    /// <summary>
    /// How long a zone must stay empty and out of range before it is stopped. Prevents thrashing
    /// when a player walks back and forth across a border.
    /// </summary>
    public int AutoZoneIdleSeconds { get; set; } = 120;
}

public static class ZoneManagerSettingsStore
{
    public const string SettingsDirectory = @"C:\AAEmuRuntime\ZoneManager";
    public static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static string DefaultMapAssetsPath()
        => Path.Combine(SettingsDirectory, "MapAssets", "Raw", "map");

    public static ZoneManagerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<ZoneManagerSettings>(File.ReadAllText(SettingsPath)) ?? new ZoneManagerSettings();
                // Older settings inherited a developer-machine default. Require one explicit
                // selection after upgrading instead of silently continuing to use that path.
                if (!settings.CompactDatabaseConfigured)
                    settings.CompactDatabasePath = string.Empty;
                var bundledCatalog = Path.Combine(AppContext.BaseDirectory, "Data", "zone_map_names.txt");
                if (File.Exists(bundledCatalog) &&
                    (!File.Exists(settings.ZoneCatalogPath) || IsGeneratedCatalogPath(settings.ZoneCatalogPath)))
                {
                    settings.ZoneCatalogPath = bundledCatalog;
                }
                if (IsLegacyBundledMapAssetsPath(settings.MapAssetsPath))
                    settings.MapAssetsPath = DefaultMapAssetsPath();
                if (settings.StartZoneKeys.SequenceEqual([142u, 129u, 156u]))
                    settings.StartZoneKeys = [179, 129, 184, 187, 328, 157];
                return settings;
            }
        }
        catch
        {
            // The UI remains usable with known-good defaults and can overwrite invalid settings.
        }

        return new ZoneManagerSettings();
    }

    private static bool IsGeneratedCatalogPath(string path) =>
        path.StartsWith(SettingsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyBundledMapAssetsPath(string path)
    {
        var legacyMainWorld = Path.Combine(path, "map_resources", "main_world", "en_us", "world.jpg");
        var rawMainWorld = Path.Combine(path, "map_resources", "main_world", "en_us", "world.dds");
        return File.Exists(legacyMainWorld) && !File.Exists(rawMainWorld);
    }

    public static void Save(ZoneManagerSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
