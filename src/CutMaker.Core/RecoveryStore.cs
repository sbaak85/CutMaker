using System.Text.Json;
using System.Text.Json.Serialization;

namespace CutMaker.Core;

public sealed record RecoveryEntry(string SessionId, DateTimeOffset SavedAt, string? OriginalProjectPath, CutProject Project)
{
    public int RecoveryVersion { get; init; } = 1;
}

/// <summary>One independently leased recovery copy. Disposal releases its lease but retains its snapshot.</summary>
public sealed class RecoveryStore : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private readonly object _gate = new();
    private readonly FileStream _lease;
    private bool _disposed;
    public string SessionId { get; }
    public string SnapshotPath { get; }
    private string LeasePath { get; }

    public RecoveryStore(string directory, string? sessionId = null)
    {
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        SessionId = sessionId ?? Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(SessionId, "N", out _)) throw new ArgumentException("Invalid recovery session identifier.", nameof(sessionId));
        SnapshotPath = Path.Combine(root, SessionId + ".recovery.json");
        LeasePath = Path.Combine(root, SessionId + ".lock");
        _lease = new FileStream(LeasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Save(CutProject project, string? projectPath, CancellationToken cancellationToken = default)
    {
        // Capture before leaving the UI thread. Records are immutable but the project lists are not.
        var captured = Capture(project, projectPath);
        if ((projectPath is not null && string.Equals(Path.GetFullPath(projectPath), SnapshotPath, StringComparison.OrdinalIgnoreCase)) ||
            captured.MediaAssets.Any(asset => string.Equals(asset.Path, SnapshotPath, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("復原備份位置不能覆蓋專案或來源素材。");
        var entry = new RecoveryEntry(SessionId, DateTimeOffset.UtcNow,
            projectPath is null ? null : Path.GetFullPath(projectPath), captured);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = SnapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, entry, Options);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(SnapshotPath)) File.Replace(temporaryPath, SnapshotPath, null);
                else File.Move(temporaryPath, SnapshotPath);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static CutProject Capture(CutProject project, string? projectPath)
    {
        ProjectValidator.Validate(project);
        return project with
        {
            MediaAssets = project.MediaAssets.Select(asset => asset with
            {
                Path = projectPath is null ? Path.GetFullPath(asset.Path) : ProjectStore.ResolveAssetPath(projectPath, asset)
            }).ToList(),
            Tracks = [.. project.Tracks], Clips = [.. project.Clips]
        };
    }

    public RecoveryEntry Read()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadEntry(SnapshotPath, SessionId);
        }
    }

    public void ClearSnapshot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (File.Exists(SnapshotPath)) File.Delete(SnapshotPath);
        }
    }

    public static IReadOnlyList<RecoveryEntry> Discover(string directory, out int unreadableCount)
    {
        unreadableCount = 0;
        var entries = new List<RecoveryEntry>();
        if (!Directory.Exists(directory)) return entries;
        foreach (var path in Directory.EnumerateFiles(directory, "*.recovery.json", SearchOption.TopDirectoryOnly))
        {
            var session = Path.GetFileName(path)[..^".recovery.json".Length];
            if (!Guid.TryParseExact(session, "N", out _)) continue;
            FileStream lease;
            try { lease = new(Path.Combine(directory, session + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { continue; } // Still owned by a running editor.
            catch (UnauthorizedAccessException) { unreadableCount++; continue; }
            using (lease)
            {
                try { entries.Add(ReadEntry(path, session)); }
                catch (Exception ex) when (IsRecoveryError(ex)) { unreadableCount++; }
            }
        }
        return entries.OrderByDescending(entry => entry.SavedAt).ToArray();
    }

    private static RecoveryEntry ReadEntry(string path, string session)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("recoveryVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var recoveryVersion) || recoveryVersion != 1 ||
            !root.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object ||
            !project.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaVersion) ||
            schemaVersion != CutProject.CurrentSchemaVersion)
            throw new InvalidDataException("復原備份版本或專案結構不符。");
        var entry = root.Deserialize<RecoveryEntry>(Options)
            ?? throw new InvalidDataException("復原備份內容為空白。");
        if (entry.RecoveryVersion != 1 || entry.SessionId != session || entry.SavedAt == default || entry.Project is null)
            throw new InvalidDataException("復原備份格式不符。");
        ProjectValidator.Validate(entry.Project);
        if (entry.Project.MediaAssets.Any(asset => !Path.IsPathFullyQualified(asset.Path)))
            throw new InvalidDataException("復原備份必須保留完整素材路徑。");
        return entry;
    }

    public static bool IsRecoveryError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        ProjectValidationException or NotSupportedException or ArgumentException;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lease.Dispose();
            // Retain the tiny lease file: unlinking it after release could race another process acquiring it.
        }
    }
}
