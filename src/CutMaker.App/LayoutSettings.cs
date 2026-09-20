using System.IO;
using System.Text.Json;

namespace CutMaker.App;

public sealed record LayoutSettings(
    double WindowWidth = 1440, double WindowHeight = 920,
    double LibraryWidth = 390, double InspectorWidth = 290,
    double TimelineHeight = 310, double TrackHeaderWidth = 250, double AssetNameWidth = 260)
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("CUTMAKER_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutMaker");

    public static LayoutSettings Load()
    {
        try
        {
            var path = Path.Combine(DataDirectory, "layout.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<LayoutSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var path = Path.Combine(DataDirectory, "layout.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    public static double Bounded(double value, double minimum, double maximum, double fallback) =>
        Math.Clamp(double.IsFinite(value) ? value : fallback, minimum, Math.Max(minimum, maximum));
}
