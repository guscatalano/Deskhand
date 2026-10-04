using System.Collections.Concurrent;

namespace Deskhand.Core.Services;

public record FolderWatchEventDto(string Type, string Path, string? OldPath, string Time);
public record FolderWatchDto(string Id, string Path, bool Recursive, bool Watching, int Buffered, bool Overflowed, string? Error = null);
public record FolderWatchPollDto(string Id, string Path, bool Watching, bool Overflowed, int Count, IReadOnlyList<FolderWatchEventDto> Events, string? Error = null);

/// <summary>
/// Watch a folder for file changes (create / delete / change / rename) via <see cref="FileSystemWatcher"/>.
/// Report-only — it never modifies anything. Start a watch, then poll it for the events buffered since your
/// last poll (the buffer is capped; an overflow is flagged). Stop it when done. Bounded number of concurrent
/// watches so an agent can't exhaust handles.
/// </summary>
public static class FolderWatchService
{
    private const int MaxBuffer = 2000;
    private const int MaxWatches = 32;
    private static readonly ConcurrentDictionary<string, Watch> _watches = new();

    public static FolderWatchDto Start(string? path, bool recursive = false)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.Length == 0) return new FolderWatchDto("", "", recursive, false, 0, false, "No folder path.");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) { return new FolderWatchDto("", path, recursive, false, 0, false, "Invalid path: " + ex.Message); }
        if (!Directory.Exists(full)) return new FolderWatchDto("", full, recursive, false, 0, false, "Directory not found.");
        if (_watches.Count >= MaxWatches) return new FolderWatchDto("", full, recursive, false, 0, false, $"Too many active watches (max {MaxWatches}). Stop one first.");
        try
        {
            var w = new Watch(full, recursive);
            _watches[w.Id] = w;
            return w.ToDto();
        }
        catch (Exception ex) { return new FolderWatchDto("", full, recursive, false, 0, false, ex.Message); }
    }

    public static FolderWatchPollDto Poll(string id)
        => _watches.TryGetValue(id, out var w)
            ? w.Drain()
            : new FolderWatchPollDto(id, "", false, false, 0, Array.Empty<FolderWatchEventDto>(), "No such watch (it may have been stopped).");

    public static FolderWatchDto Stop(string id)
    {
        if (_watches.TryRemove(id, out var w)) { w.Dispose(); return w.ToDto() with { Watching = false }; }
        return new FolderWatchDto(id, "", false, false, 0, false, "No such watch.");
    }

    public static IReadOnlyList<FolderWatchDto> List() => _watches.Values.Select(w => w.ToDto()).ToList();

    private sealed class Watch : IDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
        public string Path { get; }
        public bool Recursive { get; }
        private readonly FileSystemWatcher _fsw;
        private readonly ConcurrentQueue<FolderWatchEventDto> _q = new();
        private volatile bool _overflow;

        public Watch(string path, bool recursive)
        {
            Path = path; Recursive = recursive;
            _fsw = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            _fsw.Created += (_, e) => Add("created", e.FullPath, null);
            _fsw.Deleted += (_, e) => Add("deleted", e.FullPath, null);
            _fsw.Changed += (_, e) => Add("changed", e.FullPath, null);
            _fsw.Renamed += (_, e) => Add("renamed", e.FullPath, e.OldFullPath);
            _fsw.Error += (_, _) => _overflow = true;   // buffer overrun in the OS watcher
            _fsw.EnableRaisingEvents = true;
        }

        private void Add(string type, string path, string? old)
        {
            if (_q.Count >= MaxBuffer) { _overflow = true; _q.TryDequeue(out _); }
            _q.Enqueue(new FolderWatchEventDto(type, path, old, DateTime.UtcNow.ToString("o")));
        }

        public FolderWatchPollDto Drain()
        {
            var list = new List<FolderWatchEventDto>();
            while (_q.TryDequeue(out var e)) list.Add(e);
            bool of = _overflow; _overflow = false;
            return new FolderWatchPollDto(Id, Path, true, of, list.Count, list);
        }

        public FolderWatchDto ToDto() => new(Id, Path, Recursive, true, _q.Count, _overflow);
        public void Dispose() { try { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); } catch { } }
    }
}
