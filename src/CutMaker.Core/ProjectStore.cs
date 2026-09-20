using System.Text.Json;
using System.Text.Json.Serialization;

namespace CutMaker.Core;

public static class ProjectStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>Writes a sibling temporary file, then atomically replaces the destination after a successful flush.</summary>
    public static void Save(string path, CutProject project)
    {
        ProjectValidator.Validate(project);
        var fullPath = System.IO.Path.GetFullPath(path);
        var folder = System.IO.Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(folder);
        var temporaryPath = System.IO.Path.Combine(folder, $".{System.IO.Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, project, Options);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath))
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, fullPath);
        }
        finally
        {
            // Cleanup must not hide a failed save, and must never touch the existing project file.
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Recognizes the schema before deserializing. Missing media is allowed and can be relinked by the UI.</summary>
    public static CutProject Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version))
            throw new InvalidDataException("The project must contain an integer schemaVersion.");
        if (version != CutProject.CurrentSchemaVersion)
            throw new NotSupportedException($"Project schema {version} is unsupported; expected {CutProject.CurrentSchemaVersion}.");
        var project = document.RootElement.Deserialize<CutProject>(Options)
            ?? throw new InvalidDataException("The project is empty.");
        ProjectValidator.Validate(project);
        return project;
    }

    public static string ResolveAssetPath(string projectPath, MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(asset.Path);
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(projectPath))!;
        return System.IO.Path.GetFullPath(asset.Path, folder);
    }
}
