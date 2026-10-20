using System.Globalization;
using System.Text.RegularExpressions;

namespace Mirante.Core.Recording;

public enum RecordingKind
{
    /// <summary>Always on for the camera; cleaned up automatically (age and space limits).</summary>
    Continuous,

    /// <summary>"Gravar agora": kept until the user deletes it.</summary>
    Manual,
}

/// <summary>A recording on disk, as found by <see cref="RecordingFiles.Scan"/>.</summary>
public sealed record RecordingFile(string Path, long Size, DateTime LastWriteUtc, RecordingKind Kind);

/// <summary>
/// Where recordings go and how they are named:
/// <c>&lt;folder&gt;\Sala\2026-10-20\14-00-00 contínua.mkv</c> or <c>…\14-23-05 manual.mkv</c>.
/// Only files with exactly this shape are ever treated as recordings (and possibly deleted), so
/// anything else the user keeps in the folder is left alone.
/// </summary>
public static partial class RecordingFiles
{
    public const string Extension = ".mkv";

    /// <summary>Recording stops below this much free space instead of filling the disk.</summary>
    public const long MinimumFreeBytes = 5L * 1024 * 1024 * 1024;

    public const int DefaultRetentionDays = 7;
    public const int DefaultMaxGigabytes = 100;
    public const int MaxRetentionDays = 3650;
    public const int MaxGigabytes = 100_000;

    private const string ContinuousWord = "contínua";
    private const string ManualWord = "manual";

    public static string NextPath(string folder, string cameraName, DateTime localStart, RecordingKind kind)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var directory = Path.Combine(folder, SafeName(cameraName),
            localStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var stem = $"{localStart.ToString("HH-mm-ss", CultureInfo.InvariantCulture)} {(kind == RecordingKind.Manual ? ManualWord : ContinuousWord)}";
        var path = Path.Combine(directory, stem + Extension);
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){Extension}");
        }

        return path;
    }

    /// <summary>The kind of a file Mirante recorded, or null for anything else.</summary>
    public static RecordingKind? KindOf(string path)
    {
        var name = Path.GetFileName(path).Normalize();
        var match = FileNamePattern().Match(name);
        if (!match.Success
            || !Regex.IsMatch(Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty), @"^\d{4}-\d{2}-\d{2}$",
                RegexOptions.CultureInvariant))
        {
            return null;
        }

        return match.Groups["kind"].Value == ManualWord ? RecordingKind.Manual : RecordingKind.Continuous;
    }

    /// <summary>Every recording under <paramref name="folder"/> (camera\date\file).</summary>
    public static IReadOnlyList<RecordingFile> Scan(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var files = new List<RecordingFile>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        foreach (var path in Directory.EnumerateFiles(folder, "*" + Extension, options))
        {
            if (KindOf(path) is { } kind)
            {
                var info = new FileInfo(path);
                files.Add(new RecordingFile(path, info.Length, info.LastWriteTimeUtc, kind));
            }
        }

        return files;
    }

    /// <summary>
    /// Which recordings to delete: continuous ones older than <paramref name="retentionDays"/>, then
    /// the oldest continuous ones until everything fits in <paramref name="maxBytes"/>. Manual
    /// recordings and files being written are never chosen.
    /// </summary>
    public static IReadOnlyList<RecordingFile> SelectForCleanup(IEnumerable<RecordingFile> files, DateTime nowUtc,
        int retentionDays, long maxBytes, IReadOnlySet<string> inUse)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(inUse);
        var all = files.ToList();
        var deletable = all
            .Where(f => f.Kind == RecordingKind.Continuous && !inUse.Contains(f.Path))
            .OrderBy(f => f.LastWriteUtc)
            .ToList();

        var cutoff = nowUtc.AddDays(-retentionDays);
        var chosen = deletable.Where(f => f.LastWriteUtc < cutoff).ToList();
        var total = all.Sum(f => f.Size) - chosen.Sum(f => f.Size);
        foreach (var file in deletable.Except(chosen))
        {
            if (total <= maxBytes)
            {
                break;
            }

            chosen.Add(file);
            total -= file.Size;
        }

        return chosen;
    }

    /// <summary>
    /// A folder the video engine can be given safely: absolute, and without the characters that
    /// delimit its output settings (quotes and braces).
    /// </summary>
    public static bool IsUsableFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder)
        && Path.IsPathFullyQualified(folder)
        && folder.IndexOfAny(['\'', '"', '{', '}', '\0']) < 0;

    public static int ClampRetentionDays(int? days) => Math.Clamp(days ?? DefaultRetentionDays, 1, MaxRetentionDays);

    public static int ClampMaxGigabytes(int? gigabytes) => Math.Clamp(gigabytes ?? DefaultMaxGigabytes, 1, MaxGigabytes);

    internal static string SafeName(string? cameraName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. (cameraName ?? string.Empty).Trim()
            .Select(c => invalid.Contains(c) || c is '\'' or '{' or '}' ? '_' : c)]).Trim('.', ' ');
        return safe.Length == 0 ? "camera" : safe;
    }

    [GeneratedRegex(@"^\d{2}-\d{2}-\d{2} (?<kind>contínua|manual)( \(\d+\))?\.mkv$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}
