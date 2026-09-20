using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CutMaker.Core;

namespace CutMaker.App;

/// <summary>Real recovery-file and native-media relink checks without dialogs or changing source files.</summary>
internal static class RecoverySmokeChecks
{
    internal static async Task RunAsync(MainWindow window, string folder)
    {
        var report = Path.Combine(folder, "recovery-result.txt");
        File.WriteAllText(report, "RUNNING: recovery and media relinking checks.");
        var original = RecoveryStore.Capture(window.CurrentProject, window.CurrentProjectPath);
        var originalPath = window.CurrentProjectPath ?? Path.Combine(folder, "recovery-return.cutmaker");
        var fixtureRoot = Path.Combine(folder, "recovery-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var sourcePaths = original.MediaAssets.Select(asset => asset.Path).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sourceHashes = sourcePaths.ToDictionary(path => path, Hash, StringComparer.OrdinalIgnoreCase);
        VerifyStore(original, fixtureRoot);
        try
        {
            window.LoadSmokeProject(original, originalPath);
            Require(!await window.SaveRecoveryNowAsync(), "Clean projects must not produce automatic snapshots.");
            window.AddSmokeTrack();
            Require(await window.SaveRecoveryNowAsync(), "Dirty projects must produce a recovery snapshot.");
            var ownSnapshot = window.CurrentRecoverySnapshotPath!;
            Require(File.Exists(ownSnapshot), "The snapshot must be present after a successful write.");
            Require(!RecoveryStore.Discover(MainWindow.RecoveryDirectory, out _).Any(entry => entry.SessionId == Path.GetFileName(ownSnapshot)[..32]),
                "A live editing session must not be offered for recovery.");
            window.RecoveryProjectSaved();
            Require(!File.Exists(ownSnapshot), "Successful save cleanup must remove the current recovery only.");

            string restoreId;
            using (var orphan = new RecoveryStore(MainWindow.RecoveryDirectory))
            {
                restoreId = orphan.SessionId;
                orphan.Save(original, originalPath);
            }
            Require(window.RestoreRecovery(restoreId, promptForChanges: false), "Orphan recovery must restore without dialogs in checks.");
            Require(window.HasUnsavedChanges && window.CurrentProjectPath is null, "Restored recovery must be a dirty separate working copy.");
            Require(window.CurrentProject.Clips.SequenceEqual(original.Clips) && window.CurrentProject.MediaAssets.SequenceEqual(original.MediaAssets),
                "Restoration must preserve clips and absolute source references.");
            Require(!RecoveryStore.Discover(MainWindow.RecoveryDirectory, out _).Any(entry => entry.SessionId == restoreId),
                "Restoring must acquire the old recovery lease.");
            var restoredSnapshot = window.CurrentRecoverySnapshotPath!;
            window.LoadSmokeProject(original, originalPath);
            Require(!File.Exists(restoredSnapshot), "An accepted project switch must clear its owned recovery snapshot.");
            await VerifyRelinkAsync(window, original, originalPath, fixtureRoot);
            await VerifyMediaVisualsAsync(original);
            foreach (var (path, hash) in sourceHashes) Require(Hash(path) == hash, "Recovery/relink must preserve source bytes: " + path);
            File.WriteAllText(report, "PASS: atomic recovery round-trip; unsaved-project snapshots; absolute source paths; original project/source preservation; cancellation preserves previous snapshot; corrupt recovery isolation; live-session exclusion; independently owned cleanup; clean/dirty gating; recovered working-copy dirty state; accepted-switch cleanup; actual native-media relink; clip/source identity; undo; wrong-kind and insufficient-duration rejection; stale async relink cancellation; native source thumbnail; fixed-size real PCM waveform; reusable frozen image cache; duration-key invalidation; optional visualization cancellation.");
        }
        finally { window.LoadSmokeProject(original, originalPath); }
    }

    private static void VerifyStore(CutProject project, string folder)
    {
        var originalPath = Path.Combine(folder, "original.cutmaker");
        var relative = project with
        {
            MediaAssets = project.MediaAssets.Select(asset => asset with { Path = Path.GetRelativePath(folder, asset.Path) }).ToList()
        };
        ProjectStore.Save(originalPath, relative);
        var originalHash = Hash(originalPath);
        var recoveryFolder = Path.Combine(folder, "snapshots");
        var saved = new RecoveryStore(recoveryFolder);
        var unsaved = new RecoveryStore(recoveryFolder);
        var savedId = saved.SessionId;
        var unsavedId = unsaved.SessionId;
        try
        {
            saved.Save(relative, originalPath);
            unsaved.Save(project, null);
            Require(RecoveryStore.Discover(recoveryFolder, out var invalid).Count == 0 && invalid == 0,
                "Discovery must skip all leased live sessions.");
            var entry = saved.Read();
            Require(entry.Project.MediaAssets.All(asset => Path.IsPathFullyQualified(asset.Path)) &&
                entry.Project.MediaAssets.SequenceEqual(project.MediaAssets) && entry.Project.Clips.SequenceEqual(project.Clips),
                "Recovery must resolve relative media against the original project folder and round-trip clips.");
            Require(entry.OriginalProjectPath == originalPath && unsaved.Read().OriginalProjectPath is null,
                "Recovery must distinguish named and never-saved projects.");
            var previous = Hash(saved.SnapshotPath);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { saved.Save(project with { Title = "Cancelled update" }, originalPath, cancelled.Token); throw new InvalidOperationException("Cancelled save was accepted."); }
            catch (OperationCanceledException) { }
            Require(Hash(saved.SnapshotPath) == previous, "Cancellation must preserve the previous complete snapshot.");
            var colliding = project with { MediaAssets = [.. project.MediaAssets, new("recovery-path-source", saved.SnapshotPath, MediaKind.Image, 5)] };
            try { saved.Save(colliding, originalPath); throw new InvalidOperationException("Recovery source collision was accepted."); }
            catch (IOException) { }
            Require(Hash(saved.SnapshotPath) == previous, "A recovery path that is referenced as a source must never be overwritten.");
            Require(Hash(originalPath) == originalHash, "Recovery must never overwrite the original project.");
        }
        finally { saved.Dispose(); unsaved.Dispose(); }
        var entries = RecoveryStore.Discover(recoveryFolder, out _);
        Require(entries.Count == 2 && entries.Any(entry => entry.SessionId == savedId) && entries.Any(entry => entry.SessionId == unsavedId),
            "Released/crashed-session snapshots must become discoverable.");
        File.WriteAllText(Path.Combine(recoveryFolder, Guid.NewGuid().ToString("N") + ".recovery.json"), "{damaged");
        Require(RecoveryStore.Discover(recoveryFolder, out var broken).Count == 2 && broken == 1,
            "One corrupt snapshot must not block valid recoveries.");
        using var owner = new RecoveryStore(recoveryFolder, savedId);
        owner.ClearSnapshot();
        Require(RecoveryStore.Discover(recoveryFolder, out _).Single().SessionId == unsavedId,
            "Cleaning one session must preserve another session's recovery.");
        Require(!Directory.EnumerateFiles(recoveryFolder, "*.tmp").Any(), "Completed/cancelled writes must leave no partial temp files.");
    }

    private static async Task VerifyRelinkAsync(MainWindow window, CutProject original, string originalPath, string folder)
    {
        var image = original.MediaAssets.First(asset => asset.Kind == MediaKind.Image);
        var audio = original.MediaAssets.First(asset => asset.Kind == MediaKind.Audio && Path.GetExtension(asset.Path).Equals(".wav", StringComparison.OrdinalIgnoreCase));
        var imageCopy = Path.Combine(folder, "重新連結圖片" + Path.GetExtension(image.Path));
        var audioCopy = Path.Combine(folder, "重新連結音訊.wav");
        File.Copy(image.Path, imageCopy);
        File.Copy(audio.Path, audioCopy);
        var project = CutProject.CreateEmpty("重新連結驗證") with
        {
            MediaAssets = [image, audio],
            Clips = [new("image-clip", image.Id, "video-1", 0, 0, image.Duration), new("audio-clip", audio.Id, "audio-1", 0, 0, audio.Duration)]
        };
        window.LoadSmokeProject(project, originalPath);
        var clipBefore = window.CurrentProject.Clips.ToArray();
        Require(await window.RelinkAssetAsync(image.Id, imageCopy), "A decoded replacement image must relink successfully.");
        Require(window.HasUnsavedChanges && window.CurrentProject.MediaAssets.Single(asset => asset.Id == image.Id).Path == imageCopy &&
            window.CurrentProject.Clips.SequenceEqual(clipBefore), "Relink must retain the original asset ID and exact clip edits.");
        window.UndoEdit();
        Require(window.CurrentProject.MediaAssets.Single(asset => asset.Id == image.Id) == image, "Undo must restore the former reference.");
        Require(await window.RelinkAssetAsync(audio.Id, audioCopy), "Actual probed audio must relink successfully.");
        var before = JsonSerializer.Serialize(window.CurrentProject);
        Require(!await window.RelinkAssetAsync(audio.Id, imageCopy) && before == JsonSerializer.Serialize(window.CurrentProject),
            "A wrong media kind must be rejected without changing project contents.");

        var realProbe = window.ProbeMediaAsync;
        try
        {
            window.ProbeMediaAsync = (candidate, token) => Task.FromResult(new ProbedMedia(MediaKind.Audio, audio.Duration / 2));
            Require(!await window.RelinkAssetAsync(audio.Id, audioCopy) && before == JsonSerializer.Serialize(window.CurrentProject),
                "Replacement audio shorter than referenced source ranges must be rejected without mutation.");
            var pending = new TaskCompletionSource<ProbedMedia>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.ProbeMediaAsync = (_, _) => pending.Task;
            var stale = window.RelinkAssetAsync(audio.Id, audioCopy);
            window.LoadSmokeProject(original, originalPath);
            var switched = JsonSerializer.Serialize(window.CurrentProject);
            pending.SetResult(new(MediaKind.Audio, audio.Duration));
            Require(!await stale && switched == JsonSerializer.Serialize(window.CurrentProject),
                "A late relink result after project switching must not mutate the new project.");
        }
        finally { window.ProbeMediaAsync = realProbe; }
    }

    private static async Task VerifyMediaVisualsAsync(CutProject original)
    {
        var image = original.MediaAssets.First(asset => asset.Kind == MediaKind.Image);
        var audio = original.MediaAssets.First(asset => asset.Kind == MediaKind.Audio && Path.GetExtension(asset.Path).Equals(".wav", StringComparison.OrdinalIgnoreCase));
        var visual = await MediaVisualCache.GetAsync(image, null, CancellationToken.None);
        Require(visual.Thumbnail is { PixelWidth: 160, PixelHeight: 90, IsFrozen: true } && visual.Waveform is null,
            "An actual image must produce a frozen 160 by 90 thumbnail without an audio waveform.");
        var sound = await MediaVisualCache.GetAsync(audio, null, CancellationToken.None);
        Require(sound.Waveform is { PixelWidth: 1024, PixelHeight: 48, IsFrozen: true } && sound.Thumbnail is null,
            "Actual decoded audio must produce a fixed-size frozen waveform.");
        var pixels = new byte[1024 * 48 * 4];
        sound.Waveform!.CopyPixels(pixels, 1024 * 4, 0);
        Require(Enumerable.Range(0, pixels.Length / 4).Count(index => pixels[index * 4 + 3] > 0) > 1024,
            "The waveform must contain real source peaks beyond a flat baseline.");
        Require(ReferenceEquals(sound, await MediaVisualCache.GetAsync(audio, null, CancellationToken.None)),
            "Repeated requests must reuse the bounded in-memory visual cache.");
        Require(MediaVisualCache.GetKey(audio, null) != MediaVisualCache.GetKey(audio with { Duration = audio.Duration + 1 }, null),
            "Source duration must participate in cache identity.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await MediaVisualCache.GetAsync(audio, null, cancelled.Token); throw new InvalidOperationException("Cancelled overview was accepted."); }
        catch (OperationCanceledException) { }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Recovery check failed: " + message);
    }
}
