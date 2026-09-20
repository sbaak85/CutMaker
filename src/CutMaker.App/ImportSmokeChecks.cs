using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CutMaker.Core;

namespace CutMaker.App;

/// <summary>Runs on the WPF dispatcher and exercises the real import path with generated media.</summary>
internal static class ImportSmokeChecks
{
    public static async Task RunAsync(MainWindow window, string folder)
    {
        var fixtureFolder = Path.Combine(folder, "fixtures");
        Directory.CreateDirectory(fixtureFolder);
        var reportPath = Path.Combine(folder, "import-result.txt");
        File.WriteAllText(reportPath, "RUNNING: media import integration checks.");

        var png = Path.Combine(fixtureFolder, "圖片匯入驗證_彩色測試圖與中文名稱.png");
        var jpeg = Path.Combine(fixtureFolder, "這是一個很長的素材名稱_確認檔案總管拖曳進素材庫後名稱完整顯示與換行_圖片素材.JPEG");
        var wav = Path.Combine(fixtureFolder, "音訊匯入驗證_一秒鐘的真實PCM音訊.wav");
        var fractionalVideo = Path.Combine(fixtureFolder, "影音匯入驗證_零點七秒影片.mp4");
        var unsupported = Path.Combine(fixtureFolder, "unsupported-import-fixture.txt");
        var broken = Path.Combine(fixtureFolder, "broken-import-fixture.png");
        var missing = Path.Combine(fixtureFolder, $"missing-{Guid.NewGuid():N}.mp4");
        WriteImage(png, new PngBitmapEncoder());
        WriteImage(jpeg, new JpegBitmapEncoder());
        WriteWave(wav);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            await MediaRenderService.RunToolAsync(MediaRenderService.FindTool("ffmpeg.exe"),
                ["-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=160x90:r=30",
                 "-t", "0.7", "-c:v", "libx264", "-pix_fmt", "yuv420p", fractionalVideo], timeout.Token);
        File.WriteAllText(unsupported, "This fixture is deliberately not a supported media file.");
        File.WriteAllText(broken, "This is not a PNG file.");

        var extraMedia = ReadExtraMedia();
        string[] validFiles = [png, jpeg, wav, .. extraMedia, fractionalVideo];
        var originals = validFiles.Concat([unsupported, broken]).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, Fingerprint, StringComparer.OrdinalIgnoreCase);

        var fileDrop = new DataObject();
        fileDrop.SetData(DataFormats.FileDrop, validFiles, autoConvert: false);
        Require(window.GetLibraryDropEffect(fileDrop, DragDropEffects.Copy | DragDropEffects.Move) == DragDropEffects.Copy,
            "Explorer FileDrop must negotiate Copy.");
        Require(window.GetLibraryDropEffect(fileDrop, DragDropEffects.Move) == DragDropEffects.None,
            "Move-only drags must not move original media.");
        var textDrop = new DataObject(DataFormats.UnicodeText, png);
        Require(window.GetLibraryDropEffect(textDrop, DragDropEffects.Copy) == DragDropEffects.None,
            "Text containing a path must not be treated as an Explorer file drop.");
        var unsupportedDrop = new DataObject(DataFormats.FileDrop, new[] { unsupported });
        Require(window.GetLibraryDropEffect(unsupportedDrop, DragDropEffects.Copy) == DragDropEffects.None,
            "A drag containing only unsupported files must not be accepted.");

        var projectPath = Path.Combine(folder, "import-roundtrip.cutmaker");
        window.LoadSmokeProject(CutProject.CreateEmpty("CutMaker · 檔案總管素材匯入驗證"), projectPath);
        Require(!window.HasUnsavedChanges, "A loaded empty project must start clean.");
        var imported = await window.ImportFilesAsync(
            [.. validFiles, png, unsupported, broken, missing, fixtureFolder]);
        Require(!imported.Cancelled && imported.ImportedCount == validFiles.Length,
            $"Mixed import expected {validFiles.Length} successful files, got {imported.ImportedCount}: " +
            string.Join("; ", imported.Problems.Select(problem => $"{problem.Path}: {problem.Reason}")));
        Require(imported.Problems.Count == 5,
            $"Mixed import must report duplicate, unsupported, corrupt, missing and directory entries; got {imported.Problems.Count}.");
        foreach (var rejected in new[] { png, unsupported, broken, missing, fixtureFolder })
            Require(imported.Problems.Any(problem => string.Equals(problem.Path, rejected, StringComparison.OrdinalIgnoreCase)),
                $"Missing problem report for {Path.GetFileName(rejected)}.");
        Require(window.HasUnsavedChanges, "Successful import must mark the project dirty.");
        Require(window.CurrentProject.MediaAssets.Count == validFiles.Length, "Rejected files must not create assets.");
        foreach (var path in new[] { png, jpeg })
        {
            var asset = FindAsset(window.CurrentProject, path);
            Require(asset.Kind == MediaKind.Image && asset.Duration == 5, "Decoded images must use the five-second still duration.");
        }
        var waveAsset = FindAsset(window.CurrentProject, wav);
        Require(waveAsset.Kind == MediaKind.Audio && Math.Abs(waveAsset.Duration - 1) <= 0.025,
            $"PCM WAV must use its measured one-second duration; got {waveAsset.Duration:R}.");
        var fractionalAsset = FindAsset(window.CurrentProject, fractionalVideo);
        Require(fractionalAsset.Kind == MediaKind.Video && Math.Abs(fractionalAsset.Duration - 0.7) <= 0.001,
            $"Sub-second MP4 must retain its precise 0.7-second duration; got {fractionalAsset.Duration:R}.");
        foreach (var path in extraMedia)
        {
            var asset = FindAsset(window.CurrentProject, path);
            Require(asset.Kind is MediaKind.Audio or MediaKind.Video && double.IsFinite(asset.Duration) && asset.Duration > 0,
                $"Optional media did not produce usable measured media: {Path.GetFileName(path)}.");
        }
        VerifyOriginals(originals);
        Require(!File.Exists(missing) && Directory.Exists(fixtureFolder), "Import must not create missing files or remove folders.");

        var expectedAssets = window.CurrentProject.MediaAssets.ToArray();
        ProjectStore.Save(projectPath, window.CurrentProject);
        var loaded = ProjectStore.Load(projectPath);
        Require(loaded.MediaAssets.SequenceEqual(expectedAssets), "Imported asset paths, kinds and durations must survive save/load.");
        window.LoadSmokeProject(loaded, projectPath);
        Require(!window.HasUnsavedChanges, "Reloaded saved project must be clean.");
        var duplicates = await window.ImportFilesAsync(validFiles);
        Require(!duplicates.Cancelled && duplicates.ImportedCount == 0 && duplicates.Problems.Count == validFiles.Length,
            "Reimport after save/load must skip every existing source.");
        Require(window.CurrentProject.MediaAssets.SequenceEqual(expectedAssets) && !window.HasUnsavedChanges,
            "A duplicate-only import must preserve assets and clean state.");

        var originalProbe = window.ProbeMediaAsync;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingProbe = new TaskCompletionSource<ProbedMedia>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.ProbeMediaAsync = async (_, cancellationToken) =>
            {
                started.TrySetResult(true);
                return await pendingProbe.Task.WaitAsync(cancellationToken);
            };
            var oldProject = CutProject.CreateEmpty("Cancelled import source");
            window.LoadSmokeProject(oldProject, Path.Combine(folder, "cancelled-import.cutmaker"));
            var pendingImport = window.ImportFilesAsync([png]);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(window.GetLibraryDropEffect(fileDrop, DragDropEffects.Copy) == DragDropEffects.None,
                "The library must reject a second drop while an import is active.");
            var replacementProject = CutProject.CreateEmpty("Replacement project");
            window.LoadSmokeProject(replacementProject, Path.Combine(folder, "replacement.cutmaker"));
            // A completion queued by the old media reader must not append to the replacement project.
            pendingProbe.TrySetResult(new(MediaKind.Image, 5));
            var cancelled = await pendingImport.WaitAsync(TimeSpan.FromSeconds(5));
            Require(cancelled.Cancelled && cancelled.ImportedCount == 0, "Switching projects must cancel the old import.");
            Require(ReferenceEquals(window.CurrentProject, replacementProject) && replacementProject.MediaAssets.Count == 0 &&
                oldProject.MediaAssets.Count == 0 && !window.HasUnsavedChanges,
                "Cancelled old import must not change either project or dirty the replacement.");
            Require(window.GetLibraryDropEffect(fileDrop, DragDropEffects.Copy) == DragDropEffects.Copy,
                "The replacement project must accept file drops after cancellation.");
        }
        finally
        {
            pendingProbe.TrySetCanceled();
            window.ProbeMediaAsync = originalProbe;
        }

        // Leave real, successfully imported media visible for workspace-imported.png.
        window.LoadSmokeProject(ProjectStore.Load(projectPath), projectPath);
        VerifyOriginals(originals);
        var report = new StringBuilder()
            .AppendLine("PASS: Explorer FileDrop Copy; text/unsupported/Move-only rejection; mixed batch with five reported skips;")
            .AppendLine("real PNG/JPEG decode; five-second stills; measured one-second PCM WAV; precise 0.7-second MP4; original bytes unchanged; file locks released;")
            .AppendLine("dirty state; project save/load; duplicate-only reimport; project-switch cancellation; stale import isolation.")
            .AppendLine($"Imported fixtures: {validFiles.Length}; optional MP3/MP4 fixtures: {extraMedia.Length}.");
        foreach (var asset in window.CurrentProject.MediaAssets)
            report.AppendLine($"{asset.Kind} | {asset.Duration:R} seconds | {asset.Path}");
        File.WriteAllText(reportPath, report.ToString());
    }

    private static string[] ReadExtraMedia()
    {
        var json = Environment.GetEnvironmentVariable("CUTMAKER_SMOKE_EXTRA_MEDIA");
        if (string.IsNullOrWhiteSpace(json)) return [];
        var files = JsonSerializer.Deserialize<string[]>(json)
            ?? throw new InvalidDataException("CUTMAKER_SMOKE_EXTRA_MEDIA must be a JSON array of file paths.");
        foreach (var path in files)
            Require(!string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                (string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)),
                "Optional smoke media must name existing MP3/MP4 files.");
        return files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static MediaAsset FindAsset(CutProject project, string path) => project.MediaAssets.Single(asset =>
        string.Equals(asset.Path, path, StringComparison.OrdinalIgnoreCase));

    private static string Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void VerifyOriginals(IReadOnlyDictionary<string, string> originals)
    {
        foreach (var (path, expectedHash) in originals)
        {
            Require(File.Exists(path) && Fingerprint(path) == expectedHash,
                $"Import changed or removed source bytes: {Path.GetFileName(path)}.");
            // An exclusive read catches a decoder retaining a source handle, without requesting write access.
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
    }

    private static void WriteImage(string path, BitmapEncoder encoder)
    {
        const int width = 64, height = 40, stride = width * 3;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = y * stride + x * 3;
            pixels[offset] = 140;
            pixels[offset + 1] = (byte)(80 + y * 3);
            pixels[offset + 2] = (byte)(60 + x * 2);
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WriteWave(string path)
    {
        const int sampleRate = 44100, sampleCount = sampleRate, sampleBytes = 2;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + sampleCount * sampleBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM.
        writer.Write((short)1); // Mono.
        writer.Write(sampleRate);
        writer.Write(sampleRate * sampleBytes);
        writer.Write((short)sampleBytes);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(sampleCount * sampleBytes);
        for (var index = 0; index < sampleCount; index++)
            writer.Write((short)(2048 * Math.Sin(2 * Math.PI * 440 * index / sampleRate)));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Import smoke check failed: " + message);
    }
}
