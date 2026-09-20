namespace CutMaker.Core;

public sealed record ImportCandidate(string FullPath, MediaKind ExpectedKind);

public enum ImportRejectionReason { InvalidPath, Directory, MissingFile, UnsupportedType, Duplicate }

public sealed record ImportRejection(string Path, ImportRejectionReason Reason);

public sealed record MediaImportPlan(
    IReadOnlyList<ImportCandidate> Candidates,
    IReadOnlyList<ImportRejection> Rejections);

/// <summary>
/// Plans a non-destructive file import. Extensions identify candidates only; the caller must
/// probe their contents and duration before creating assets. Directories are never traversed.
/// </summary>
public static class MediaImport
{
    private static readonly Dictionary<string, MediaKind> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = MediaKind.Video,
        [".mp3"] = MediaKind.Audio,
        [".wav"] = MediaKind.Audio,
        [".png"] = MediaKind.Image,
        [".jpg"] = MediaKind.Image,
        [".jpeg"] = MediaKind.Image
    };

    public static bool IsSupportedPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && SupportedExtensions.ContainsKey(System.IO.Path.GetExtension(path));

    /// <summary>Resolves relative paths against the project folder supplied by the caller.</summary>
    public static string NormalizePath(string path, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return System.IO.Path.GetFullPath(path, System.IO.Path.GetFullPath(baseDirectory));
    }

    /// <summary>
    /// Preserves input order and compares normalized Windows paths without case sensitivity.
    /// Existing assets may use project-relative paths. Rejected files do not stop the batch.
    /// </summary>
    public static MediaImportPlan Plan(
        IEnumerable<string> paths, IEnumerable<MediaAsset> existingAssets, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(existingAssets);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var folder = System.IO.Path.GetFullPath(baseDirectory);
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in existingAssets)
        {
            if (TryNormalizePath(asset.Path, folder, out var existingPath)) knownPaths.Add(existingPath);
        }

        var candidates = new List<ImportCandidate>();
        var rejections = new List<ImportRejection>();
        foreach (var path in paths)
        {
            ImportRejectionReason? reason = null;
            if (!TryNormalizePath(path, folder, out var fullPath)) reason = ImportRejectionReason.InvalidPath;
            else if (Directory.Exists(fullPath)) reason = ImportRejectionReason.Directory;
            else if (!File.Exists(fullPath)) reason = ImportRejectionReason.MissingFile;
            else if (!IsSupportedPath(fullPath)) reason = ImportRejectionReason.UnsupportedType;
            else if (!knownPaths.Add(fullPath)) reason = ImportRejectionReason.Duplicate;

            if (reason is { } rejection)
                rejections.Add(new ImportRejection(path ?? "", rejection));
            else
                candidates.Add(new ImportCandidate(fullPath, SupportedExtensions[System.IO.Path.GetExtension(fullPath)]));
        }
        return new MediaImportPlan(candidates.AsReadOnly(), rejections.AsReadOnly());
    }

    private static bool TryNormalizePath(string? path, string folder, out string fullPath)
    {
        try
        {
            fullPath = NormalizePath(path!, folder);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = "";
            return false;
        }
    }
}
