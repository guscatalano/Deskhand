namespace Deskhand.Core.Services;

public record FileEntryDto(string Name, string Path, bool IsDirectory, long? Size, DateTime? Modified, string? Extension);
public record DirListingDto(string Path, string? Parent, bool IsRoot, IReadOnlyList<FileEntryDto> Entries, string? Error = null);

/// <summary>
/// Read-only file-system browsing: list the folders and files in a directory. It never writes, deletes,
/// or reads file contents — it returns names, sizes, and timestamps only (open a file by handing its path
/// to the process-launch tool, which shell-executes it). An empty path lists the drive roots. Bounded to
/// what the host's token can see; a folder that needs elevation returns a clear access error, not a crash.
/// </summary>
public static class FileSystemService
{
    private const int MaxEntries = 5000;   // cap huge directories so a listing can't blow up the response

    /// <summary>Browse a directory. <paramref name="path"/> empty/null lists the drives; otherwise a
    /// folder path (e.g. <c>C:\Users</c>). A file path returns an error pointing at process-launch.</summary>
    public static DirListingDto Browse(string? path)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0) return Roots();

        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) { return new DirListingDto(path, null, false, Array.Empty<FileEntryDto>(), "Invalid path: " + ex.Message); }

        if (!Directory.Exists(full))
        {
            if (File.Exists(full))
                return new DirListingDto(full, System.IO.Path.GetDirectoryName(full), false, Array.Empty<FileEntryDto>(),
                    "This is a file, not a folder — launch it with the process-launch tool to open it.");
            return new DirListingDto(full, null, false, Array.Empty<FileEntryDto>(), "Directory not found.");
        }

        try
        {
            var di = new DirectoryInfo(full);
            var entries = new List<FileEntryDto>();

            foreach (var d in Safe(() => di.EnumerateDirectories()))
            {
                if (entries.Count >= MaxEntries) break;
                DateTime? mod = null; try { mod = d.LastWriteTime; } catch { }
                entries.Add(new FileEntryDto(d.Name, d.FullName, true, null, mod, null));
            }
            foreach (var f in Safe(() => di.EnumerateFiles()))
            {
                if (entries.Count >= MaxEntries) break;
                long? size = null; DateTime? mod = null;
                try { size = f.Length; } catch { }
                try { mod = f.LastWriteTime; } catch { }
                entries.Add(new FileEntryDto(f.Name, f.FullName, false, size, mod,
                    string.IsNullOrEmpty(f.Extension) ? null : f.Extension));
            }

            // Folders first, then files; each alphabetical, case-insensitive.
            var ordered = entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DirListingDto(full, Directory.GetParent(full)?.FullName, false, ordered);
        }
        catch (UnauthorizedAccessException) { return new DirListingDto(full, null, false, Array.Empty<FileEntryDto>(), "Access denied — this folder needs elevation."); }
        catch (Exception ex) { return new DirListingDto(full, null, false, Array.Empty<FileEntryDto>(), ex.Message); }
    }

    private static DirListingDto Roots()
    {
        var entries = new List<FileEntryDto>();
        foreach (var drv in Safe(() => DriveInfo.GetDrives()))
        {
            try
            {
                string name = drv.IsReady && !string.IsNullOrWhiteSpace(drv.VolumeLabel)
                    ? $"{drv.Name} ({drv.VolumeLabel})" : drv.Name;
                long? free = drv.IsReady ? drv.AvailableFreeSpace : null;
                entries.Add(new FileEntryDto(name, drv.RootDirectory.FullName, true, free, null, drv.DriveType.ToString()));
            }
            catch { /* a drive that isn't ready (empty DVD, disconnected) — skip it */ }
        }
        return new DirListingDto("", null, true, entries);
    }

    // Materialize an enumeration that may throw part-way (a locked/denied child); return what we can.
    private static IEnumerable<T> Safe<T>(Func<IEnumerable<T>> f)
    {
        try { return f().ToList(); }
        catch { return Array.Empty<T>(); }
    }
}
