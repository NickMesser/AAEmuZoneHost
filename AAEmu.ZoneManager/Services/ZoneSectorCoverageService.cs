using System.Globalization;
using System.IO;
using AAEmu.ZoneManager.Models;

namespace AAEmu.ZoneManager.Services;

public sealed class ZoneSectorCoverageService
{
    public Dictionary<uint, IReadOnlyList<ZoneSectorRun>> Load(string path)
    {
        var result = new Dictionary<uint, List<ZoneSectorRun>>();
        if (!File.Exists(path))
            return [];

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var columns = line.Split('\t');
            if (columns.Length != 4 ||
                !uint.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out var zoneKey) ||
                !int.TryParse(columns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sectorY) ||
                !int.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startSectorX) ||
                !int.TryParse(columns[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sectorCount) ||
                sectorCount <= 0)
            {
                continue;
            }

            if (!result.TryGetValue(zoneKey, out var runs))
            {
                runs = [];
                result.Add(zoneKey, runs);
            }
            runs.Add(new ZoneSectorRun(sectorY, startSectorX, sectorCount));
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ZoneSectorRun>)pair.Value);
    }
}
