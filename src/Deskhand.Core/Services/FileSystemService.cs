using System.IO.Compression;

namespace Deskhand.Core.Services;

public record FileEntryDto(string Name, string Path, bool IsDirectory, long? Size, DateTime? Modified, string? Extension);
public record DirListingDto(string Path, string? Parent, bool IsRoot, IReadOnlyList<FileEntryDto> Entries, string? Error = null);
public record FileContentDto(string Path, long Size, string? Base64, string? Error = null);
public record FileTextDto(string Path, string Name, long Size, long ReadBytes, bool Truncated, bool Binary, string? Text, string? Error = null);
public record WriteResultDto(string Path, long Size, bool Overwritten, string? Error = null);
public record FsOpResultDto(string Op, string Path, string? Dest, bool Ok, string? Detail = null, string? Error = null);

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

    // ---- read / write (download / upload). SENSITIVE: reads/writes real file bytes — the host layer
    // gates these on the kill switch (armed) and audits them. Base64 is for the MCP path; the HTTP host
    // streams large files instead (see /fs/download, /fs/upload).

    /// <summary>Max bytes returned as base64 (MCP/JSON). Larger files must use the streaming HTTP download.</summary>
    public const long MaxBase64Bytes = 25_000_000;

    /// <summary>Read a file as base64 (for MCP). Refuses files larger than <see cref="MaxBase64Bytes"/>.</summary>
    public static FileContentDto ReadFileBase64(string? path, long maxBytes = MaxBase64Bytes)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0) return new FileContentDto("", 0, null, "No path given.");
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            if (Directory.Exists(full)) return new FileContentDto(full, 0, null, "That is a folder, not a file.");
            if (!File.Exists(full)) return new FileContentDto(full, 0, null, "File not found.");
            long len = new FileInfo(full).Length;
            if (len > maxBytes) return new FileContentDto(full, len, null,
                $"File is {len:N0} bytes, over the {maxBytes:N0}-byte base64 limit — use the HTTP /fs/download endpoint for large files.");
            var bytes = File.ReadAllBytes(full);
            return new FileContentDto(full, bytes.LongLength, Convert.ToBase64String(bytes));
        }
        catch (UnauthorizedAccessException) { return new FileContentDto(path, 0, null, "Access denied."); }
        catch (Exception ex) { return new FileContentDto(path, 0, null, ex.Message); }
    }

    /// <summary>Write bytes (given as base64) to a file. <paramref name="overwrite"/> false fails if it exists.
    /// Creates the parent directory if needed.</summary>
    public static WriteResultDto WriteFileBase64(string? path, string? base64, bool overwrite)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0) return new WriteResultDto("", 0, false, "No path given.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64 ?? ""); }
        catch { return new WriteResultDto(path, 0, false, "content is not valid base64."); }
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            if (Directory.Exists(full)) return new WriteResultDto(full, 0, false, "A folder already exists at that path.");
            bool existed = File.Exists(full);
            if (existed && !overwrite) return new WriteResultDto(full, 0, false, "File exists — pass overwrite=true to replace it.");
            var dir = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(full, bytes);
            return new WriteResultDto(full, bytes.LongLength, existed);
        }
        catch (UnauthorizedAccessException) { return new WriteResultDto(path, 0, false, "Access denied — this location needs elevation."); }
        catch (Exception ex) { return new WriteResultDto(path, 0, false, ex.Message); }
    }

    /// <summary>Read a file as UTF-8 text for the dashboard's text-only viewer. Reads at most
    /// <paramref name="maxBytes"/> from the front (default 512 KB), decodes as UTF-8, and flags files that look
    /// binary (a NUL byte in the sample) so the caller can steer the user to download instead.</summary>
    public static FileTextDto ReadText(string? path, long maxBytes = 512 * 1024)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0) return new FileTextDto("", "", 0, 0, false, false, null, "No path given.");
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            if (Directory.Exists(full)) return new FileTextDto(full, "", 0, 0, false, false, null, "That is a folder, not a file.");
            if (!File.Exists(full)) return new FileTextDto(full, "", 0, 0, false, false, null, "File not found.");
            long len = new FileInfo(full).Length;
            int want = (int)Math.Min(len, maxBytes);
            var buf = new byte[want];
            int read;
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                read = fs.Read(buf, 0, want);
            bool binary = Array.IndexOf(buf, (byte)0, 0, read) >= 0;
            string name = System.IO.Path.GetFileName(full);
            if (binary) return new FileTextDto(full, name, len, read, len > read, true, null);
            // strip a UTF-8 BOM if present so it doesn't render as a stray glyph
            int start = (read >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) ? 3 : 0;
            string text = System.Text.Encoding.UTF8.GetString(buf, start, read - start);
            return new FileTextDto(full, name, len, read, len > read, false, text);
        }
        catch (UnauthorizedAccessException) { return new FileTextDto(path, "", 0, 0, false, false, null, "Access denied."); }
        catch (Exception ex) { return new FileTextDto(path, "", 0, 0, false, false, null, ex.Message); }
    }

    /// <summary>Validate a path for streaming download: returns the full path if it's a readable file, else an
    /// error string (one of the two out params is set).</summary>
    public static (string? full, string? error) ResolveForDownload(string? path)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0) return (null, "No path given.");
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            if (Directory.Exists(full)) return (null, "That is a folder, not a file.");
            if (!File.Exists(full)) return (null, "File not found.");
            return (full, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ---- mutations (delete / rename / move / copy). DESTRUCTIVE: the host layer gates these on the kill
    // switch (armed) and audits them. Delete goes to the Recycle Bin by default so it's recoverable.

    private static bool Exists(string full) => File.Exists(full) || Directory.Exists(full);
    private static bool IsDir(string full) => Directory.Exists(full);

    /// <summary>Delete a file or folder. By default it goes to the Recycle Bin (recoverable);
    /// <paramref name="permanent"/> true deletes it irreversibly.</summary>
    public static FsOpResultDto Delete(string? path, bool permanent)
    {
        var r = Full(path, "delete");
        if (r.error is not null) return new FsOpResultDto("delete", path ?? "", null, false, null, r.error);
        var full = r.full!;
        try
        {
            if (!permanent)
            {
                int rc = RecycleBin.Delete(full);
                if (rc != 0) return new FsOpResultDto("delete", full, null, false, null, $"Recycle failed (SHFileOperation {rc}).");
                return new FsOpResultDto("delete", full, null, true, "sent to Recycle Bin");
            }
            if (IsDir(full)) Directory.Delete(full, recursive: true); else File.Delete(full);
            return new FsOpResultDto("delete", full, null, true, "permanently deleted");
        }
        catch (UnauthorizedAccessException) { return new FsOpResultDto("delete", full, null, false, null, "Access denied — needs elevation."); }
        catch (Exception ex) { return new FsOpResultDto("delete", full, null, false, null, ex.Message); }
    }

    /// <summary>Rename a file/folder in place. <paramref name="newName"/> is a bare name, not a path.</summary>
    public static FsOpResultDto Rename(string? path, string? newName)
    {
        var r = Full(path, "rename");
        if (r.error is not null) return new FsOpResultDto("rename", path ?? "", null, false, null, r.error);
        var full = r.full!;
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) return new FsOpResultDto("rename", full, null, false, null, "No new name given.");
        if (newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            return new FsOpResultDto("rename", full, null, false, null, "New name has invalid characters (it must be a name, not a path).");
        var dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full) ?? "", newName);
        return DoMove(full, dest, "rename", overwrite: false);
    }

    /// <summary>Move a file/folder. <paramref name="dest"/> may be a target folder (move into it) or a full
    /// destination path.</summary>
    public static FsOpResultDto Move(string? source, string? dest, bool overwrite)
    {
        var r = Full(source, "move");
        if (r.error is not null) return new FsOpResultDto("move", source ?? "", dest, false, null, r.error);
        var (target, terr) = ResolveDest(r.full!, dest);
        if (terr is not null) return new FsOpResultDto("move", r.full!, dest, false, null, terr);
        return DoMove(r.full!, target!, "move", overwrite);
    }

    /// <summary>Copy a file, or a folder recursively. <paramref name="dest"/> may be a target folder or a full
    /// destination path.</summary>
    public static FsOpResultDto Copy(string? source, string? dest, bool overwrite)
    {
        var r = Full(source, "copy");
        if (r.error is not null) return new FsOpResultDto("copy", source ?? "", dest, false, null, r.error);
        var (target, terr) = ResolveDest(r.full!, dest);
        if (terr is not null) return new FsOpResultDto("copy", r.full!, dest, false, null, terr);
        try
        {
            if (IsDir(r.full!)) CopyDir(r.full!, target!, overwrite);
            else
            {
                if (File.Exists(target!) && !overwrite) return new FsOpResultDto("copy", r.full!, target, false, null, "Destination exists — pass overwrite=true.");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target!)!);
                File.Copy(r.full!, target!, overwrite);
            }
            return new FsOpResultDto("copy", r.full!, target, true, "copied");
        }
        catch (UnauthorizedAccessException) { return new FsOpResultDto("copy", r.full!, target, false, null, "Access denied — needs elevation."); }
        catch (Exception ex) { return new FsOpResultDto("copy", r.full!, target, false, null, ex.Message); }
    }

    private static FsOpResultDto DoMove(string full, string dest, string op, bool overwrite)
    {
        try
        {
            if (Exists(dest))
            {
                if (!overwrite) return new FsOpResultDto(op, full, dest, false, null, "Destination exists — pass overwrite=true.");
                if (File.Exists(dest)) File.Delete(dest); else Directory.Delete(dest, true);
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            if (IsDir(full)) Directory.Move(full, dest); else File.Move(full, dest, overwrite);
            return new FsOpResultDto(op, full, dest, true, op == "rename" ? "renamed" : "moved");
        }
        catch (UnauthorizedAccessException) { return new FsOpResultDto(op, full, dest, false, null, "Access denied — needs elevation."); }
        catch (Exception ex) { return new FsOpResultDto(op, full, dest, false, null, ex.Message); }
    }

    // Turn a user path into a full path that must exist; shared validation for the mutations.
    private static (string? full, string? error) Full(string? path, string op)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0) return (null, "No path given.");
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) { return (null, "Invalid path: " + ex.Message); }
        if (!Exists(full)) return (null, "Not found: " + full);
        // Refuse to delete/move a whole drive root — too easy to be catastrophic.
        if (op is "delete" or "move" && System.IO.Path.GetPathRoot(full)?.TrimEnd('\\') == full.TrimEnd('\\'))
            return (null, "Refusing to " + op + " a drive root.");
        return (full, null);
    }

    // Resolve a destination that may be an existing folder (put source inside it) or a full target path.
    private static (string? target, string? error) ResolveDest(string sourceFull, string? dest)
    {
        dest = (dest ?? "").Trim().Trim('"');
        if (dest.Length == 0) return (null, "No destination given.");
        string full;
        try { full = System.IO.Path.GetFullPath(dest); }
        catch (Exception ex) { return (null, "Invalid destination: " + ex.Message); }
        if (Directory.Exists(full)) full = System.IO.Path.Combine(full, System.IO.Path.GetFileName(sourceFull.TrimEnd('\\')));
        if (string.Equals(full, sourceFull, StringComparison.OrdinalIgnoreCase)) return (null, "Source and destination are the same.");
        return (full, null);
    }

    private static void CopyDir(string src, string dst, bool overwrite)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(f)), overwrite);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDir(d, System.IO.Path.Combine(dst, System.IO.Path.GetFileName(d)), overwrite);
    }

    // ---- zip / unzip ----

    /// <summary>Create a .zip at <paramref name="destZip"/> from one or more files/folders (folders are added
    /// recursively under their own name). overwrite=false fails if the zip already exists.</summary>
    public static FsOpResultDto Zip(IReadOnlyList<string>? sources, string? destZip, bool overwrite)
    {
        if (sources is null || sources.Count == 0) return new FsOpResultDto("zip", destZip ?? "", null, false, null, "No source paths given.");
        destZip = (destZip ?? "").Trim().Trim('"');
        if (destZip.Length == 0) return new FsOpResultDto("zip", "", null, false, null, "No destination zip path given.");
        string full;
        try { full = System.IO.Path.GetFullPath(destZip); }
        catch (Exception ex) { return new FsOpResultDto("zip", destZip, null, false, null, "Invalid zip path: " + ex.Message); }
        if (File.Exists(full) && !overwrite) return new FsOpResultDto("zip", full, null, false, null, "Zip already exists — pass overwrite=true.");
        try
        {
            var dir = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(full)) File.Delete(full);
            int n = 0;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var zip = ZipFile.Open(full, ZipArchiveMode.Create))
            {
                foreach (var s in sources)
                {
                    var sf = (s ?? "").Trim().Trim('"');
                    if (sf.Length == 0) continue;
                    sf = System.IO.Path.GetFullPath(sf);
                    if (Directory.Exists(sf))
                    {
                        var prefix = System.IO.Path.GetFileName(sf.TrimEnd('\\'));
                        foreach (var file in Directory.EnumerateFiles(sf, "*", SearchOption.AllDirectories))
                        {
                            var rel = System.IO.Path.GetRelativePath(sf, file).Replace('\\', '/');
                            zip.CreateEntryFromFile(file, Uniq($"{prefix}/{rel}", used), CompressionLevel.Optimal);
                            n++;
                        }
                    }
                    else if (File.Exists(sf))
                    {
                        zip.CreateEntryFromFile(sf, Uniq(System.IO.Path.GetFileName(sf), used), CompressionLevel.Optimal);
                        n++;
                    }
                }
            }
            if (n == 0) { try { File.Delete(full); } catch { } return new FsOpResultDto("zip", full, null, false, null, "None of the sources were readable files/folders."); }
            return new FsOpResultDto("zip", full, null, true, $"{n} entries");
        }
        catch (UnauthorizedAccessException) { return new FsOpResultDto("zip", full, null, false, null, "Access denied — needs elevation."); }
        catch (Exception ex) { return new FsOpResultDto("zip", full, null, false, null, ex.Message); }
    }

    /// <summary>Extract a .zip into a folder. <paramref name="destDir"/> empty extracts next to the zip into a
    /// folder named after it. overwrite=false fails if an output file already exists.</summary>
    public static FsOpResultDto Unzip(string? zipPath, string? destDir, bool overwrite)
    {
        zipPath = (zipPath ?? "").Trim().Trim('"');
        if (zipPath.Length == 0) return new FsOpResultDto("unzip", "", null, false, null, "No zip path given.");
        string zf;
        try { zf = System.IO.Path.GetFullPath(zipPath); }
        catch (Exception ex) { return new FsOpResultDto("unzip", zipPath, null, false, null, "Invalid zip path: " + ex.Message); }
        if (!File.Exists(zf)) return new FsOpResultDto("unzip", zf, null, false, null, "Zip file not found.");

        string dest;
        var d = (destDir ?? "").Trim().Trim('"');
        try
        {
            dest = d.Length > 0
                ? System.IO.Path.GetFullPath(d)
                : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(zf)!, System.IO.Path.GetFileNameWithoutExtension(zf));
        }
        catch (Exception ex) { return new FsOpResultDto("unzip", zf, destDir, false, null, "Invalid destination: " + ex.Message); }

        try
        {
            Directory.CreateDirectory(dest);
            int n;
            using (var za = ZipFile.OpenRead(zf)) n = za.Entries.Count;
            ZipFile.ExtractToDirectory(zf, dest, overwriteFiles: overwrite);
            return new FsOpResultDto("unzip", zf, dest, true, $"{n} entries");
        }
        catch (IOException ex) when (!overwrite && ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        { return new FsOpResultDto("unzip", zf, dest, false, null, "A file already exists at the destination — pass overwrite=true."); }
        catch (UnauthorizedAccessException) { return new FsOpResultDto("unzip", zf, dest, false, null, "Access denied — needs elevation."); }
        catch (InvalidDataException) { return new FsOpResultDto("unzip", zf, dest, false, null, "Not a valid zip archive."); }
        catch (Exception ex) { return new FsOpResultDto("unzip", zf, dest, false, null, ex.Message); }
    }

    private static string Uniq(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var ext = System.IO.Path.GetExtension(name);
        var dir = System.IO.Path.GetDirectoryName(name)?.Replace('\\', '/');
        for (int i = 1; ; i++)
        {
            var candidate = (string.IsNullOrEmpty(dir) ? "" : dir + "/") + $"{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }

    // Recycle Bin delete via the shell, so a delete is recoverable by default.
    private static class RecycleBin
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo;
            public ushort fFlags; public int fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle;
        }
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040, FOF_NOCONFIRMATION = 0x0010, FOF_SILENT = 0x0004, FOF_NOERRORUI = 0x0400;

        public static int Delete(string path)
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",   // double-null-terminated list
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            return SHFileOperation(ref op);
        }
    }
}
