using AAEmu.ZoneManager.Models;

namespace AAEmu.ZoneManager.Services;

/// <summary>What auto-management wants changed on this tick.</summary>
public sealed record AutoZoneDecision(
    IReadOnlyList<uint> ToStart,
    IReadOnlyList<uint> ToStop,
    string Summary);

/// <summary>Why a zone is wanted, lowest value wins when trimming to the zone ceiling.</summary>
internal enum AutoZoneReason
{
    Default = 0,
    Occupied = 1,
    Approaching = 2
}

/// <summary>
/// Decides which zones should be hosted, from where the players actually are.
///
/// A zone is wanted when it is the configured default, when a player stands in it, or when a
/// player is within <see cref="ZoneManagerSettings.AutoZonePrestartMeters"/> of its border —
/// zone coverage is a set of sector runs, so border distance is the nearest run rectangle.
/// Zones that fall out of that set are stopped only after they have been unwanted continuously
/// for <see cref="ZoneManagerSettings.AutoZoneIdleSeconds"/>, which stops a player pacing over a
/// border from cycling a host process.
///
/// Pure: it reads state and returns intent, so the caller owns all process control.
/// </summary>
public sealed class AutoZoneService
{
    /// <summary>World units per sector — WorldManager.REGION_SIZE.</summary>
    private const double SectorSize = 64d;

    private readonly Dictionary<uint, DateTimeOffset> _unwantedSince = [];

    /// <summary>Forgets idle tracking; call when auto-management is switched off.</summary>
    public void Reset() => _unwantedSince.Clear();

    public AutoZoneDecision Evaluate(
        ZoneManagerSettings settings,
        IReadOnlyDictionary<uint, IReadOnlyList<ZoneSectorRun>> coverage,
        WorldStatusSnapshot? snapshot,
        IReadOnlyCollection<uint> runningZoneKeys,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(runningZoneKeys);

        var allPlayers = snapshot?.Players ?? [];

        // The template decides the anchors (always hosted) and whose position drives proximity.
        var players = allPlayers;
        var wanted = new Dictionary<uint, AutoZoneReason>();
        switch (settings.StartTemplate)
        {
            case AutoZoneStartTemplate.DefaultZone:
                if (settings.DefaultZoneKey != 0)
                    wanted[settings.DefaultZoneKey] = AutoZoneReason.Default;
                break;

            case AutoZoneStartTemplate.NewPlayerStartZones:
                foreach (var zoneKey in settings.StartZoneKeys ?? [])
                {
                    if (zoneKey != 0)
                        Want(wanted, zoneKey, AutoZoneReason.Default);
                }
                break;

            case AutoZoneStartTemplate.SpecifiedPlayer:
                players = allPlayers
                    .Where(player => string.Equals(
                        player.Name, settings.FollowPlayerName?.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                break;

            case AutoZoneStartTemplate.ZonesWithPlayers:
            default:
                break;
        }

        foreach (var player in players)
        {
            if (player.ZoneKey != 0)
                Want(wanted, player.ZoneKey, AutoZoneReason.Occupied);
        }

        var prestart = Math.Max(0d, settings.AutoZonePrestartMeters);
        if (prestart > 0)
        {
            foreach (var (zoneKey, runs) in coverage)
            {
                if (wanted.ContainsKey(zoneKey))
                    continue;
                foreach (var player in players)
                {
                    if (DistanceToZoneMeters(runs, player.X, player.Y) > prestart)
                        continue;
                    Want(wanted, zoneKey, AutoZoneReason.Approaching);
                    break;
                }
            }
        }

        // Trim to the ceiling: the default zone and occupied zones outrank approach pre-starts.
        var ceiling = Math.Max(1, settings.MaxActiveZones);
        var ranked = wanted
            .OrderBy(pair => (int)pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(ceiling)
            .Select(pair => pair.Key)
            .ToHashSet();

        var running = runningZoneKeys.ToHashSet();
        var toStart = new List<uint>();
        var budget = ceiling - running.Count;
        foreach (var zoneKey in ranked)
        {
            if (running.Contains(zoneKey))
                continue;
            if (budget <= 0)
                break;
            toStart.Add(zoneKey);
            budget--;
        }

        var idleFor = TimeSpan.FromSeconds(Math.Max(0, settings.AutoZoneIdleSeconds));
        var toStop = new List<uint>();
        var pinned = settings.StartTemplate switch
        {
            AutoZoneStartTemplate.DefaultZone => [settings.DefaultZoneKey],
            AutoZoneStartTemplate.NewPlayerStartZones => (settings.StartZoneKeys ?? []).ToHashSet(),
            _ => new HashSet<uint>()
        };

        foreach (var zoneKey in running)
        {
            if (ranked.Contains(zoneKey) || pinned.Contains(zoneKey))
            {
                _unwantedSince.Remove(zoneKey);
                continue;
            }

            if (!_unwantedSince.TryGetValue(zoneKey, out var since))
            {
                _unwantedSince[zoneKey] = now;
                continue;
            }

            if (now - since >= idleFor)
            {
                toStop.Add(zoneKey);
                _unwantedSince.Remove(zoneKey);
            }
        }

        foreach (var zoneKey in _unwantedSince.Keys.Where(key => !running.Contains(key)).ToArray())
            _unwantedSince.Remove(zoneKey);

        var summary =
            $"{players.Count} player(s) · {running.Count}/{ceiling} hosted · " +
            $"{ranked.Count} wanted · +{toStart.Count} / -{toStop.Count}";
        return new AutoZoneDecision(toStart, toStop, summary);
    }

    private static void Want(Dictionary<uint, AutoZoneReason> wanted, uint zoneKey, AutoZoneReason reason)
    {
        if (!wanted.TryGetValue(zoneKey, out var existing) || reason < existing)
            wanted[zoneKey] = reason;
    }

    /// <summary>
    /// Metres from a world position to the nearest sector covered by the zone, zero when inside.
    /// A run is one row of sectors, so this is a point-to-rectangle distance per run.
    /// </summary>
    internal static double DistanceToZoneMeters(IReadOnlyList<ZoneSectorRun> runs, double worldX, double worldY)
    {
        if (runs is null || runs.Count == 0)
            return double.PositiveInfinity;

        var sectorX = worldX / SectorSize;
        var sectorY = worldY / SectorSize;
        var nearest = double.PositiveInfinity;
        foreach (var run in runs)
        {
            if (run.SectorCount <= 0)
                continue;
            var dx = Math.Max(Math.Max(run.StartSectorX - sectorX, sectorX - (run.StartSectorX + run.SectorCount)), 0d);
            var dy = Math.Max(Math.Max(run.SectorY - sectorY, sectorY - (run.SectorY + 1)), 0d);
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < nearest)
                nearest = distance;
            if (nearest <= 0d)
                return 0d;
        }

        return nearest * SectorSize;
    }
}
