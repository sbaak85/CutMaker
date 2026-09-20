using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CutMaker.Core;

internal static class RecoveryChecks
{
    public static void Run()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var folder = Path.GetFullPath(Path.Combine(temporaryRoot, "CutMaker-RecoveryChecks-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        try
        {
            var mediaFolder = Path.Combine(folder, "media");
            Directory.CreateDirectory(mediaFolder);
            var source = Path.Combine(mediaFolder, "來源音訊.wav");
            File.WriteAllBytes(source, [1, 7, 5, 2, 19]);
            var sourceHash = Hash(source);
            var originalPath = Path.Combine(folder, "original.cutmaker");
            var project = CutProject.CreateEmpty("Recovery CI") with
            {
                MediaAssets = [new("source", Path.GetRelativePath(folder, source), MediaKind.Audio, 10)],
                Clips = [new("clip", "source", "audio-1", 2, 1, 4, .7, new(1), new(1))]
            };
            ProjectStore.Save(originalPath, project);
            var originalHash = Hash(originalPath);
            var directory = Path.Combine(folder, "recovery");
            string firstSession;
            string secondSession;
            string validSnapshot;
            using (var first = new RecoveryStore(directory))
            using (var second = new RecoveryStore(directory))
            {
                firstSession = first.SessionId;
                secondSession = second.SessionId;
                first.Save(project, originalPath);
                var captured = RecoveryStore.Capture(project, originalPath);
                second.Save(captured, null);
                var named = first.Read();
                var unnamed = second.Read();
                Require(named.Project.MediaAssets.Single().Path == source && named.Project.Clips.SequenceEqual(project.Clips),
                    "Recovery must preserve edits and resolve source paths against the original project folder.");
                Require(named.OriginalProjectPath == originalPath && unnamed.OriginalProjectPath is null &&
                    named.SavedAt > DateTimeOffset.UtcNow.AddMinutes(-2), "Recovery must retain timestamp and original/unsaved provenance.");
                Require(!ReferenceEquals(captured.MediaAssets, project.MediaAssets) && !ReferenceEquals(captured.Tracks, project.Tracks) &&
                    !ReferenceEquals(captured.Clips, project.Clips), "Recovery capture must own all mutable list containers.");
                Require(RecoveryStore.Discover(directory, out var skipped).Count == 0 && skipped == 0,
                    "Live owners must not be offered for recovery.");
                Expect<IOException>(() => { using var duplicateOwner = new RecoveryStore(directory, first.SessionId); });
                first.Save(project with { Title = "Updated recovery" }, originalPath);
                Require(first.Read().Project.Title == "Updated recovery", "Replacing a recovery must produce the latest complete project.");
                validSnapshot = File.ReadAllText(first.SnapshotPath);
                var before = Hash(first.SnapshotPath);
                Expect<ProjectValidationException>(() => first.Save(project with { Title = "" }, originalPath));
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                Expect<OperationCanceledException>(() => first.Save(project, originalPath, cancelled.Token));
                var sourceCollision = project with
                {
                    MediaAssets = [.. project.MediaAssets, new("snapshot-as-source", first.SnapshotPath, MediaKind.Image, 5)]
                };
                Expect<IOException>(() => first.Save(sourceCollision, originalPath));
                Expect<IOException>(() => first.Save(captured, first.SnapshotPath));
                Require(Hash(first.SnapshotPath) == before, "Invalid/cancelled/colliding writes must preserve the complete prior snapshot.");
                Require(Hash(originalPath) == originalHash && Hash(source) == sourceHash,
                    "Recovery must never change the original project or source media.");
            }
            var available = RecoveryStore.Discover(directory, out _);
            Require(available.Count == 2 && available.Select(entry => entry.SessionId).ToHashSet().SetEquals([firstSession, secondSession]),
                "Released sessions must be independently recoverable.");
            var malformed = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(directory, malformed + ".recovery.json"), "{broken");
            WriteChanged(directory, validSnapshot, node => node["recoveryVersion"] = 999);
            WriteChanged(directory, validSnapshot, node => node["recoveryVersion"] = "1");
            WriteChanged(directory, validSnapshot, node => node["project"]!.AsObject().Remove("schemaVersion"));
            WriteChanged(directory, validSnapshot, node => node["project"]!["mediaAssets"]![0]!["path"] = "relative.wav");
            WriteChanged(directory, validSnapshot, node => node["project"]!["clips"]![0]!["assetId"] = "missing-source");
            Require(RecoveryStore.Discover(directory, out var invalid).Count == 2 && invalid == 6,
                "Corrupt/versioned/relative-source/invalid-reference snapshots must be isolated from valid recoveries.");
            using (var restoredOwner = new RecoveryStore(directory, firstSession))
            {
                Require(restoredOwner.Read().Project.Title == "Updated recovery", "A restored owner must read the complete prior snapshot.");
                restoredOwner.ClearSnapshot();
            }
            Require(RecoveryStore.Discover(directory, out _).Single().SessionId == secondSession,
                "Clearing one session must preserve all other valid recovery copies.");
            CheckCancelledWriterCannotResurrect(directory, RecoveryStore.Capture(project, originalPath));
            Require(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Atomic writes must clean their temporary files.");
            Require(Hash(originalPath) == originalHash && Hash(source) == sourceHash, "All checks must preserve input bytes.");
            Expect<ArgumentException>(() => { using var outside = new RecoveryStore(directory, "../outside"); });
        }
        finally
        {
            if (!folder.StartsWith(temporaryRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Recovery test cleanup escaped the temporary directory.");
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void CheckCancelledWriterCannotResurrect(string directory, CutProject project)
    {
        using var store = new RecoveryStore(directory);
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        store.Save(project, null);
        var writer = Task.Run(() =>
        {
            entered.Set();
            try
            {
                for (var index = 0; index < 32; index++)
                    store.Save(project with { Title = "Pending write " + index }, null, cancellation.Token);
            }
            catch (OperationCanceledException) { }
        });
        Require(entered.Wait(TimeSpan.FromSeconds(5)), "Background writer must start.");
        cancellation.Cancel();
        store.ClearSnapshot();
        writer.GetAwaiter().GetResult();
        Require(!File.Exists(store.SnapshotPath), "A cancelled in-flight writer must not resurrect a snapshot cleared after save/discard.");
    }

    private static void WriteChanged(string directory, string json, Action<JsonObject> mutate)
    {
        var id = Guid.NewGuid().ToString("N");
        var node = JsonNode.Parse(json)!.AsObject();
        node["sessionId"] = id;
        mutate(node);
        File.WriteAllText(Path.Combine(directory, id + ".recovery.json"), node.ToJsonString());
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
