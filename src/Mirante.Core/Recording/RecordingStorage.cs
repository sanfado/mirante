namespace Mirante.Core.Recording;

/// <summary>
/// A place recordings go (a folder, usually on its own disk) with its own space limit. Cameras
/// record to a fixed location or, by default, "Automático": the first location with room.
/// </summary>
public sealed record RecordingLocation
{
    public const int DefaultMaxGigabytes = 100;

    public required Guid Id { get; init; }
    public required string Folder { get; init; }

    /// <summary>Null: the default (100 GB).</summary>
    public int? MaxGigabytes { get; init; }

    public int Gigabytes => RecordingFiles.ClampMaxGigabytes(MaxGigabytes);

    public long MaxBytes => Gigabytes * 1024L * 1024 * 1024;
}

/// <summary>How a location is doing right now (measured in the background).</summary>
/// <param name="Available">The folder's disk is there (an external drive may be unplugged).</param>
/// <param name="FreeBytes">Free space on that disk.</param>
/// <param name="UsedBytes">Space taken by Mirante's recordings in the location.</param>
/// <param name="OldestContinuousUtc">The oldest continuous recording there, if any.</param>
public sealed record LocationState(RecordingLocation Location, bool Available, long FreeBytes, long UsedBytes,
    DateTime? OldestContinuousUtc)
{
    /// <summary>New files may go here (the disk is there and has more than the safety margin free).</summary>
    public bool CanWrite => Available && FreeBytes >= RecordingFiles.MinimumFreeBytes;

    public bool UnderLimit => UsedBytes < Location.MaxBytes;
}

public static class RecordingStorage
{
    public const int MaxLocations = 10;

    /// <summary>
    /// The locations to use: valid, distinct, at most <see cref="MaxLocations"/>, and never none
    /// (without any, recordings go to <paramref name="defaultFolder"/>).
    /// </summary>
    public static IReadOnlyList<RecordingLocation> Normalize(IEnumerable<RecordingLocation?>? locations, string defaultFolder)
    {
        var valid = (locations ?? [])
            .Where(l => l is not null && l.Id != Guid.Empty && RecordingFiles.IsUsableFolder(l.Folder))
            .Select(l => l! with { Folder = Path.TrimEndingDirectorySeparator(l.Folder.Trim()) })
            .DistinctBy(l => l.Id)
            .DistinctBy(l => l.Folder, StringComparer.OrdinalIgnoreCase)
            .Take(MaxLocations)
            .ToList();
        return valid.Count > 0 ? valid : [Default(defaultFolder)];
    }

    /// <summary>The single location everyone starts with. Its id is fixed so cameras can refer to it.</summary>
    public static RecordingLocation Default(string folder) =>
        new() { Id = new Guid("6d6972a0-6e74-6500-0000-000000000001"), Folder = folder };

    /// <summary>
    /// Where a camera's next file goes. A fixed location is used while its disk can take it (its
    /// space limit is kept by deleting its oldest continuous recordings). Otherwise, or for
    /// "Automático": the first location under its limit; when all are full, the one holding the
    /// oldest continuous recording (which the cleanup will remove), like a recorder overwriting
    /// the oldest footage. Null when no disk can take anything.
    /// </summary>
    public static RecordingLocation? Choose(IReadOnlyList<LocationState> states, Guid? fixedLocation)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (fixedLocation is { } id && states.FirstOrDefault(s => s.Location.Id == id) is { CanWrite: true } chosen)
        {
            return chosen.Location;
        }

        var writable = states.Where(s => s.CanWrite).ToList();
        return writable.FirstOrDefault(s => s.UnderLimit)?.Location
               ?? writable.OrderBy(s => s.OldestContinuousUtc ?? DateTime.MaxValue).FirstOrDefault()?.Location;
    }

    /// <summary>The location a file belongs to (by folder), or null.</summary>
    public static RecordingLocation? LocationOf(IEnumerable<RecordingLocation> locations, string path) =>
        locations.FirstOrDefault(l => path.StartsWith(
            l.Folder.EndsWith(Path.DirectorySeparatorChar) ? l.Folder : l.Folder + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
}
