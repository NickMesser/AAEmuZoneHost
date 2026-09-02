using System.IO;
using AAEmu.ZoneManager.Models;
using Microsoft.Data.Sqlite;

namespace AAEmu.ZoneManager.Services;

public sealed class ZoneCatalogService
{
    public ZoneCatalog Load(string catalogPath, string compactDatabasePath)
    {
        var zones = LoadTextCatalog(catalogPath).ToDictionary(zone => zone.ZoneKey);
        var groups = new List<ZoneGroupDefinition>();
        var mapResources = new List<MapResourceDefinition>();
        WorldMapDefinition? worldMap = null;

        if (File.Exists(compactDatabasePath))
            worldMap = MergeDatabase(compactDatabasePath, zones, groups, mapResources);

        if (zones.Count == 0)
            throw new InvalidOperationException("No zones were found in the zone catalog or compact database.");

        return new ZoneCatalog(
            zones.Values.OrderBy(zone => zone.ZoneKey).ToArray(),
            groups.OrderBy(group => group.Id).ToArray(),
            worldMap,
            mapResources.OrderBy(resource => resource.Id).ToArray());
    }

    private static IEnumerable<ZoneDefinition> LoadTextCatalog(string path)
    {
        if (!File.Exists(path))
            yield break;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var columns = rawLine.Split('\t');
            if (columns.Length < 3 || !uint.TryParse(columns[0], out var zoneKey))
                continue;

            int? groupId = columns.Length > 3 && int.TryParse(columns[3], out var parsedGroupId)
                ? parsedGroupId
                : null;

            yield return new ZoneDefinition(
                zoneKey,
                columns[1].Trim(),
                ParseBoolean(columns[2]),
                groupId);
        }
    }

    private static WorldMapDefinition? MergeDatabase(
        string path,
        Dictionary<uint, ZoneDefinition> zones,
        List<ZoneGroupDefinition> groups,
        List<MapResourceDefinition> mapResources)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, zone_key, group_id, closed FROM zones";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var zoneKey = checked((uint)reader.GetInt64(2));
                var databaseZone = new ZoneDefinition(
                    zoneKey,
                    reader.GetString(1),
                    ParseBoolean(reader.GetValue(4)?.ToString()),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.GetInt32(0));

                if (zones.TryGetValue(zoneKey, out var catalogZone))
                {
                    zones[zoneKey] = catalogZone with
                    {
                        GroupId = databaseZone.GroupId,
                        DatabaseId = databaseZone.DatabaseId,
                        Closed = databaseZone.Closed
                    };
                }
                else
                {
                    zones[zoneKey] = databaseZone;
                }
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT mr.id, mr.name, mr.map_target_id, mr.map_target_type, mr.folder_name,
                       mr.world_over_image_path,
                       CASE WHEN mr.map_target_type = 'SubZone'
                            THEN (SELECT sz.linked_zone_group_id
                                  FROM sub_zones sz
                                  WHERE sz.id = mr.map_target_id)
                            ELSE NULL END
                FROM map_resources mr
                WHERE mr.enable = 't' AND mr.folder_name IS NOT NULL AND mr.folder_name <> ''
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                mapResources.Add(new MapResourceDefinition(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6)));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT zg.id, zg.name,
                       COALESCE((SELECT mr.folder_name
                                 FROM map_resources mr
                                 WHERE mr.map_target_type = 'ZoneGroup'
                                   AND mr.map_target_id = zg.id
                                   AND mr.enable = 't'
                                 LIMIT 1), zg.name),
                       zg.x, zg.y, zg.w, zg.h
                FROM zone_groups zg
                WHERE zg.w > 0 AND zg.h > 0
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                groups.Add(new ZoneGroupDefinition(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6)));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name, x, y, w, h, image_x, image_y, image_w, image_h
                FROM world_groups
                WHERE name = 'main_world'
                LIMIT 1
                """;
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                return new WorldMapDefinition(
                    reader.GetString(0),
                    reader.GetDouble(1),
                    reader.GetDouble(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6),
                    reader.GetDouble(7),
                    reader.GetDouble(8));
            }
        }

        return null;
    }

    private static bool ParseBoolean(string? value) =>
        value is not null && (value == "1" || value.Equals("t", StringComparison.OrdinalIgnoreCase) ||
                              value.Equals("true", StringComparison.OrdinalIgnoreCase));
}
