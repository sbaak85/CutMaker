using System.IO;
using System.Text.Json;
using CutMaker.Core;

namespace CutMaker.App;

internal enum PreviewQuality { Low, Medium, High }
internal sealed record EditorPreferences(PreviewQuality PreviewQuality = PreviewQuality.Medium, int CacheGiB = 4,
    ExportVideoQuality ExportQuality = ExportVideoQuality.Balanced, int AudioBitrate = 192,
    string ExportFolder = "", int ExportFormat = 1, bool UseVideoProxies = false,
    FadeSettings? FadeInPreset = null, FadeSettings? FadeOutPreset = null)
{
    internal static string Root => Environment.GetEnvironmentVariable("CUTMAKER_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutMaker");
    internal static EditorPreferences Current { get; private set; } = Load();
    internal long CacheBytes => (long)CacheGiB * 1024 * 1024 * 1024;
    internal int PreviewWidth => PreviewQuality switch { PreviewQuality.Low => 480, PreviewQuality.High => 1280, _ => 960 };
    private static EditorPreferences Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(Path.Combine(Root, "preferences.json"))) ?? new();
            return value with { CacheGiB = Math.Clamp(value.CacheGiB, 1, 32),
                PreviewQuality = Enum.IsDefined(value.PreviewQuality) ? value.PreviewQuality : PreviewQuality.Medium,
                ExportQuality = Enum.IsDefined(value.ExportQuality) ? value.ExportQuality : ExportVideoQuality.Balanced,
                AudioBitrate = value.AudioBitrate is 128 or 192 or 256 or 320 ? value.AudioBitrate : 192,
                ExportFormat = value.ExportFormat == 2 ? 2 : 1 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save()
    {
        Directory.CreateDirectory(Root);
        var target = Path.Combine(Root, "preferences.json");
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(this)); File.Move(temp, target, true); Current = this; }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
