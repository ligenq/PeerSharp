using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using PeerSharp.Internals;

namespace PeerSharp.PieceWriter;

internal sealed class FileHandleCache : IFileHandleCache
{
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly ILogger<FileHandleCache> _logger = TorrentLoggerFactory.CreateLogger<FileHandleCache>();
    private readonly LinkedList<string> _lruList = new();
    private readonly int _maxOpenFiles;
    private AtomicDisposal _disposal = new();

    public FileHandleCache(int maxOpenFiles = 200)
    {
        _maxOpenFiles = Math.Max(32, maxOpenFiles);
        _logger.LogDebug("FileHandleCache initialized with limit of {MaxOpenFiles} handles", _maxOpenFiles);
    }

    public void CloseTorrentHandles(string rootPath)
    {
        // Cache keys are full file paths under the torrent root. Match on a directory
        // boundary so that stopping a torrent rooted at "/d/foo" does not also close
        // handles belonging to a sibling torrent at "/d/foobar".
        string normalizedRoot = Path.GetFullPath(rootPath);
        string prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) || normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        lock (_lock)
        {
            var toRemove = _cache.Keys.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var path in toRemove)
            {
                if (_cache.Remove(path, out var entry))
                {
                    _lruList.Remove(entry.Node);
                    if (entry.RefCount == 0)
                    {
                        CloseEntry(entry);
                    }
                    // If RefCount > 0, it is now orphaned and will be closed when last lease is disposed.
                }
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public ValueTask<IFileHandleLease> GetHandleAsync(string path, bool writable, CancellationToken cancellationToken = default)
    {
        _disposal.ThrowIfDisposed(this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var entry))
            {
                // If it's already open and we need write access but it's read-only, we must re-open
                // But only if no one else is using it!
                if (writable && !entry.IsWritable)
                {
                    if (entry.RefCount > 0)
                    {
                        _logger.LogTrace("Upgrading file handle to writable (orphan old): {Path}", path);
                        _cache.Remove(path);
                        _lruList.Remove(entry.Node);
                    }
                    else
                    {
                        _logger.LogTrace("Upgrading file handle to writable: {Path}", path);
                        CloseEntry(entry); // RefCount is 0, safe to close
                        _cache.Remove(path);
                        _lruList.Remove(entry.Node);
                    }
                }
                else
                {
                    // Hit!
                    _lruList.Remove(entry.Node);
                    _lruList.AddFirst(entry.Node);
                    entry.RefCount++;
                    return new ValueTask<IFileHandleLease>(new FileHandleLease(this, entry));
                }
            }

            // Eviction needed?
            while (_cache.Count >= _maxOpenFiles)
            {
                // Find LRU candidate with RefCount == 0
                var node = _lruList.Last;
                bool evicted = false;

                while (node != null)
                {
                    var prev = node.Previous; // Save prev because we might remove node
                    if (_cache.TryGetValue(node.Value, out var candidate) && candidate.RefCount == 0)
                    {
                        _logger.LogTrace("Closing LRU file handle: {Path}", node.Value);
                        _cache.Remove(node.Value);
                        _lruList.Remove(node);
                        CloseEntry(candidate);
                        evicted = true;
                        break;
                    }
                    node = prev;
                }

                if (!evicted)
                {
                    // Cache is full of busy handles. We must expand temporarily.
                    _logger.LogWarning("FileHandleCache limit reached ({Limit}) but all handles are in use. Temporarily exceeding limit.", _maxOpenFiles);
                    break;
                }
            }

            // Open new handle
            var handle = File.OpenHandle(
                path: path,
                mode: FileMode.OpenOrCreate,
                access: writable ? FileAccess.ReadWrite : FileAccess.Read,
                share: FileShare.ReadWrite,
                options: FileOptions.Asynchronous | FileOptions.RandomAccess);

            var newNode = new LinkedListNode<string>(path);
            var newEntry = new CacheEntry
            {
                Handle = handle,
                Node = newNode,
                IsWritable = writable,
                RefCount = 1
            };

            _cache[path] = newEntry;
            _lruList.AddFirst(newNode);

            return new ValueTask<IFileHandleLease>(new FileHandleLease(this, newEntry));
        }
    }

    private void CloseEntry(CacheEntry entry)
    {
        try
        {
            entry.Handle.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing file handle");
        }
    }

    private void Dispose(bool disposing)
    {
        if (_disposal.MarkDisposed() && disposing)
        {
            lock (_lock)
            {
                foreach (var entry in _cache.Values)
                {
                    CloseEntry(entry);
                }
                _cache.Clear();
                _lruList.Clear();
            }
        }
    }

    private void Release(CacheEntry entry)
    {
        lock (_lock)
        {
            entry.RefCount--;
            if (entry.RefCount < 0)
            {
                _logger.LogError("Ref count underflow for {Path}", entry.Node.Value);
                entry.RefCount = 0;
            }

            // Check if orphaned (not in cache) and refcount 0 -> Dispose immediately
            if (entry.RefCount == 0 && entry.Node.List == null)
            {
                CloseEntry(entry);
            }
        }
    }

    private sealed class CacheEntry
    {
        public required SafeFileHandle Handle { get; init; }
        public bool IsWritable { get; set; }
        public required LinkedListNode<string> Node { get; init; }
        public int RefCount { get; set; }
    }

    private sealed class FileHandleLease : IFileHandleLease
    {
        private readonly FileHandleCache _cache;
        private readonly CacheEntry _entry;
        private AtomicDisposal _disposal = new();

        public FileHandleLease(FileHandleCache cache, CacheEntry entry)
        {
            _cache = cache;
            _entry = entry;
        }

        public SafeFileHandle Handle => _entry.Handle;
        public string Path => _entry.Node.ValueRef;

        public void Dispose()
        {
            if (_disposal.MarkDisposed())
            {
                _cache.Release(_entry);
            }
        }
    }
}
