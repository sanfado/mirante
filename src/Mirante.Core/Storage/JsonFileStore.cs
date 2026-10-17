using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Mirante.Core.Storage;

/// <summary>
/// Reads and writes one JSON document. Writes are atomic (temp file + rename), so a
/// crash or power loss mid-save never leaves a half-written config behind.
/// </summary>
public sealed class JsonFileStore<T>(string filePath, JsonTypeInfo<T> typeInfo)
    where T : class
{
    public string FilePath { get; } = filePath;

    /// <summary>
    /// Loads the document, or returns null when it does not exist. A corrupt file is
    /// moved aside (kept for inspection) instead of crashing the app or being overwritten.
    /// </summary>
    public T? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), typeInfo);
        }
        catch (JsonException)
        {
            File.Move(FilePath, $"{FilePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            return null;
        }
    }

    public void Save(T document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, typeInfo));
        File.Move(temp, FilePath, overwrite: true);
    }
}
