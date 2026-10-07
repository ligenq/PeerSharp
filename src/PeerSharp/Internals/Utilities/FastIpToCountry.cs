using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PeerSharp.Internals.Utilities;

/// <summary>IPv4 country lookups from the ipclist text header and little-endian range buckets.</summary>
internal class FastIpToCountry
{
    private Database? _database;

    internal bool IsLoaded => Volatile.Read(ref _database) != null;

    public string GetCountry(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var database = Volatile.Read(ref _database);
        if (database == null || address.AddressFamily != AddressFamily.InterNetwork) return string.Empty;
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        uint ip = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var bucket = database.Buckets[bytes[0]];
        int low = 0, high = bucket.Length - 1, found = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (bucket[mid].StartIp <= ip) { found = mid; low = mid + 1; }
            else high = mid - 1;
        }
        return found < 0 ? string.Empty : database.Countries[bucket[found].CountryIdx];
    }

    public void Load(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            Load(stream);
        }
        catch (Exception) { /* An optional database failure leaves the previous snapshot intact. */ }
    }

    public void Load(Stream stream)
    {
        try
        {
            var parser = new DatabaseParser();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer)) > 0) parser.Append(buffer.AsSpan(0, count));
            Volatile.Write(ref _database, parser.Complete());
        }
        catch (Exception) { /* Keep the last complete database. */ }
    }

    public async Task LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* GeoIP is optional. */ }
    }

    public async Task LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var parser = new DatabaseParser();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                parser.Append(buffer.AsSpan(0, count));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var database = parser.Complete();
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _database, database);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* Keep the last complete database. */ }
    }

    private sealed record Database(string[] Countries, (uint StartIp, ushort CountryIdx)[][] Buckets);

    private sealed class DatabaseParser
    {
        private const int MaxBytes = 64 * 1024 * 1024;
        private const int MaxEntries = 4_000_000;
        private static readonly UTF8Encoding TextEncoding = new(false, true);
        private readonly List<string> _countries = [];
        private readonly List<(uint StartIp, ushort CountryIdx)>[] _buckets =
            Enumerable.Range(0, 256).Select(_ => new List<(uint, ushort)>()).ToArray();
        private readonly List<byte> _line = [];
        private readonly byte[] _entry = new byte[6];
        private bool _headerComplete;
        private int _bucket, _entryBytes, _bytes, _entries;

        public void Append(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > MaxBytes - _bytes) throw new InvalidDataException("GeoIP database exceeds 64 MiB.");
            _bytes += bytes.Length;
            foreach (byte value in bytes)
            {
                if (!_headerComplete)
                {
                    if (value == '\n')
                    {
                        string country = TextEncoding.GetString(_line.ToArray()).Trim();
                        _line.Clear();
                        if (country.Length == 0) _headerComplete = true;
                        else
                        {
                            if (_countries.Count >= ushort.MaxValue) throw new InvalidDataException("Too many countries.");
                            _countries.Add(country);
                        }
                    }
                    else
                    {
                        if (_line.Count >= 4096) throw new InvalidDataException("GeoIP country name is too long.");
                        _line.Add(value);
                    }
                    continue;
                }
                if (_bucket >= 256) throw new InvalidDataException("Trailing GeoIP data.");
                _entry[_entryBytes++] = value;
                if (_entryBytes < 6) continue;
                _entryBytes = 0;
                uint ip = BinaryPrimitives.ReadUInt32LittleEndian(_entry);
                ushort countryIndex = BinaryPrimitives.ReadUInt16LittleEndian(_entry.AsSpan(4));
                if (countryIndex == 0x4545) { _bucket++; continue; }
                var bucket = _buckets[_bucket];
                if (countryIndex >= _countries.Count || ip >> 24 != _bucket
                    || (bucket.Count > 0 && ip <= bucket[^1].StartIp) || ++_entries > MaxEntries)
                    throw new InvalidDataException("Invalid GeoIP range.");
                bucket.Add((ip, countryIndex));
            }
        }

        public Database Complete()
        {
            // A file may omit empty trailing buckets, but every populated bucket must terminate.
            if (!_headerComplete || _countries.Count == 0 || _entryBytes != 0 || _bucket == 0
                || (_bucket < 256 && _buckets[_bucket].Count != 0))
                throw new InvalidDataException("Truncated GeoIP database.");
            return new Database(_countries.ToArray(), _buckets.Select(bucket => bucket.ToArray()).ToArray());
        }
    }
}
