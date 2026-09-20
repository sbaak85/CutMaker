using System.IO;

namespace CutMaker.App;

/// <summary>Bounded local evidence for decode, native opening and audio-device failures.</summary>
internal static class PreviewDiagnostics
{
    private static readonly object Gate = new();

    internal static void Write(string stage, string details)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LayoutSettings.DataDirectory);
                var path = Path.Combine(LayoutSettings.DataDirectory, "preview-diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{stage}] {details}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
