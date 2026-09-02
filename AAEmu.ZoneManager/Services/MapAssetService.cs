using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AAEmu.ZoneManager.Models;
using Pfim;

namespace AAEmu.ZoneManager.Services;

public sealed class MapAssetService
{
    public ImageSource? LoadWorldMap(string mapAssetsPath, string databaseFolderName, string locale)
    {
        var mapPath = ResolveDatabaseAssetPath(
            mapAssetsPath,
            Path.Combine(databaseFolderName, "world.dds"),
            locale);
        return mapPath is null ? null : LoadTexture(mapPath);
    }

    public BitmapSource? LoadOverlayAtlas(
        string mapAssetsPath,
        string? worldOverImagePath,
        string locale)
    {
        var mapPath = ResolveDatabaseAssetPath(mapAssetsPath, worldOverImagePath, locale);
        return mapPath is null ? null : LoadTexture(mapPath);
    }

    public IReadOnlyDictionary<string, MapOverlaySegment> LoadOverlaySegments(
        string mapAssetsPath,
        string? worldOverImagePath)
    {
        if (string.IsNullOrWhiteSpace(worldOverImagePath))
            return new Dictionary<string, MapOverlaySegment>(StringComparer.OrdinalIgnoreCase);

        var segmentReference = Path.ChangeExtension(worldOverImagePath, ".g");
        var path = ResolveDatabaseAssetPath(mapAssetsPath, segmentReference, null);
        if (path is null)
            return new Dictionary<string, MapOverlaySegment>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, MapOverlaySegment>(StringComparer.OrdinalIgnoreCase);
        var pattern = new Regex(
            @"(?m)^(?<name>\S+)\s*\r?\n\s*offset\s*\(\s*(?<ox>-?\d+)\s*,\s*(?<oy>-?\d+)\s*\)\s*\r?\n\s*coords\s*\(\s*(?<sx>\d+)\s*,\s*(?<sy>\d+)\s*,\s*(?<w>\d+)\s*,\s*(?<h>\d+)\s*\)",
            RegexOptions.CultureInvariant);
        foreach (Match match in pattern.Matches(File.ReadAllText(path)))
        {
            var segment = new MapOverlaySegment(
                match.Groups["name"].Value,
                double.Parse(match.Groups["ox"].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(match.Groups["oy"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["sx"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["sy"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture));
            result[segment.Name] = segment;
        }
        return result;
    }

    public static BitmapSource? CropOverlay(BitmapSource atlas, MapOverlaySegment segment)
    {
        if (segment.SourceX < 0 || segment.SourceY < 0 || segment.Width <= 0 || segment.Height <= 0 ||
            segment.SourceX > atlas.PixelWidth - segment.Width ||
            segment.SourceY > atlas.PixelHeight - segment.Height)
        {
            return null;
        }

        try
        {
            var crop = new CroppedBitmap(atlas, new System.Windows.Int32Rect(
                segment.SourceX,
                segment.SourceY,
                segment.Width,
                segment.Height));
            crop.Freeze();
            return crop;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? ResolveDatabaseAssetPath(
        string mapAssetsPath,
        string? databasePath,
        string? locale)
    {
        if (string.IsNullOrWhiteSpace(mapAssetsPath) || string.IsNullOrWhiteSpace(databasePath))
            return null;

        var normalizedReference = databasePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedReference))
            return null;

        var fileName = Path.GetFileName(normalizedReference);
        var relativeDirectory = Path.GetDirectoryName(normalizedReference) ?? string.Empty;
        if (fileName.Length == 0)
            return null;

        var resourceRoot = Path.GetFullPath(Path.Combine(mapAssetsPath, "map_resources"));
        var referencedDirectory = Path.GetFullPath(Path.Combine(resourceRoot, relativeDirectory));
        if (!IsWithinRoot(resourceRoot, referencedDirectory))
            return null;

        var directories = string.IsNullOrWhiteSpace(locale)
            ? [referencedDirectory]
            : new[]
            {
                Path.Combine(referencedDirectory, locale),
                Path.Combine(referencedDirectory, "en_us"),
                referencedDirectory
            }.Distinct(StringComparer.OrdinalIgnoreCase);
        var candidates = directories.Select(directory => Path.Combine(directory, fileName));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relativePath) &&
               !relativePath.Equals("..", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static BitmapSource LoadTexture(string path)
    {
        using var image = Pfimage.FromFile(path);
        var source = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            GetPixelFormat(image.Format),
            null,
            image.Data,
            image.Stride);
        source.Freeze();
        return source;
    }

    private static PixelFormat GetPixelFormat(ImageFormat format) => format switch
    {
        ImageFormat.Rgb24 => PixelFormats.Bgr24,
        ImageFormat.Rgba32 => PixelFormats.Bgra32,
        ImageFormat.Rgb8 => PixelFormats.Gray8,
        ImageFormat.R5g5b5a1 or ImageFormat.R5g5b5 => PixelFormats.Bgr555,
        ImageFormat.R5g6b5 => PixelFormats.Bgr565,
        _ => throw new NotSupportedException($"Unsupported map texture pixel format: {format}")
    };
}
