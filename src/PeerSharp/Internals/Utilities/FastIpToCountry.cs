using System.Net;
using System.Text;

namespace PeerSharp.Internals.Utilities;

/// <summary>
/// Provides fast IP-to-country lookups using a pre-processed binary database.
/// The database ("ipclist") consists of a text section with country names followed by
/// 256 buckets of binary IP range data.
/// </summary>
internal class FastIpToCountry
{
    private readonly List<(uint StartIp, ushort CountryIdx)>[] _buckets = new List<(uint StartIp, ushort CountryIdx)>[256];
    private readonly List<string> _countries = [];

    public FastIpToCountry()
    {
        for (int i = 0; i < 256; i++)
        {
            _buckets[i] = [];
        }
    }

    /// <summary>
    /// Resolves the country code for a given IPv4 address.
    /// </summary>
    /// <param name="address">The IPv4 address to lookup.</param>
    /// <returns>A country string (e.g. "US", "GB") or an empty string if not found.</returns>
    public string GetCountry(IPAddress address)
    {
        // Only IPv4 is supported by this database format
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return "";
        }

        byte[] bytes = address.GetAddressBytes();

        // Construct a Big-Endian integer representation for comparison.
        // The first byte of the IP serves as the bucket index.
        uint ip = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);

        int bucketIdx = bytes[0];
        var bucket = _buckets[bucketIdx];

        if (bucket.Count == 0)
        {
            return "";
        }

        // Perform binary search to find the largest StartIp that is <= our target IP.
        // Since ranges are contiguous in this database, this accurately identifies the country.
        int low = 0, high = bucket.Count - 1;
        int found = -1;

        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (bucket[mid].StartIp <= ip)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (found != -1)
        {
            ushort countryIdx = bucket[found].CountryIdx;
            if (countryIdx < _countries.Count)
            {
                return _countries[countryIdx];
            }
        }

        return "";
    }

    /// <summary>
    /// Loads the GeoIP database from the specified file path.
    /// </summary>
    /// <param name="filePath">The full path to the GeoIP database file.</param>
    public void Load(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            using var fs = File.OpenRead(filePath);
            Load(fs);
        }
        catch (Exception)
        {
            // Fail silently - GeoIP is optional
        }
    }

    /// <summary>
    /// Loads the GeoIP database from a stream.
    /// </summary>
    public void Load(Stream stream)
    {
        try
        {
            // Format Phase 1: Read country names (plain text, one per line)
            // Ends with an empty line (double newline)
            var sb = new StringBuilder();
            while (true)
            {
                int b = stream.ReadByte();
                if (b == -1)
                {
                    break;
                }

                if (b == '\n')
                {
                    string line = sb.ToString().Trim();
                    sb.Clear();
                    if (string.IsNullOrEmpty(line))
                    {
                        break; // Empty line separator reached
                    }

                    _countries.Add(line);
                }
                else
                {
                    sb.Append((char)b);
                }
            }

            // Format Phase 2: Read 256 buckets of binary range data
            // Each entry is 6 bytes: [4 byte uint StartIP (LE)] [2 byte ushort CountryIndex (LE)]
            // Buckets are separated by a dummy entry with CountryIndex = 0x4545 ("EE")
            var buffer = new byte[6];
            int bucket = 0;

            while (bucket < 256 && stream.Read(buffer, 0, 6) == 6)
            {
                uint ip = BitConverter.ToUInt32(buffer, 0);
                ushort country = BitConverter.ToUInt16(buffer, 4);

                if (country == 0x4545) // Bucket separator marker "EE"
                {
                    bucket++;
                }
                else
                {
                    _buckets[bucket].Add((ip, country));
                }
            }
        }
        catch (Exception)
        {
            // Fail silently
        }
    }

    /// <summary>
    /// Loads the GeoIP database from the specified file path asynchronously.
    /// </summary>
    /// <param name="filePath">The full path to the GeoIP database file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            await using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await LoadAsync(fs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fail silently - GeoIP is optional
        }
    }

    /// <summary>
    /// Loads the GeoIP database from a stream asynchronously.
    /// </summary>
    public async Task LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        try
        {
            var buffer = new byte[8192];

            // Phase 1: Read country names (text, one per line, blank line terminator)
            var countryBuffer = new List<byte>(128);
            bool foundSeparator = false;
            int bytesRead;
            int remainingOffset = 0;
            int remainingCount = 0;
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                int offset = 0;
                while (offset < bytesRead)
                {
                    byte b = buffer[offset++];
                    if (b == (byte)'\n')
                    {
                        string line = Encoding.UTF8.GetString(countryBuffer.ToArray()).Trim();
                        countryBuffer.Clear();
                        if (string.IsNullOrEmpty(line))
                        {
                            foundSeparator = true;
                            remainingOffset = offset;
                            remainingCount = bytesRead - offset;
                            break;
                        }
                        _countries.Add(line);
                    }
                    else
                    {
                        countryBuffer.Add(b);
                    }
                }

                if (foundSeparator)
                {
                    break;
                }
            }

            if (!foundSeparator)
            {
                return;
            }

            // Phase 2: Read 6-byte entries: [uint StartIP LE][ushort CountryIndex LE]
            var entryBuffer = new byte[6];
            int entryOffset = 0;
            int bucket = 0;

            ProcessEntries(buffer, remainingOffset, remainingCount, entryBuffer, ref entryOffset, ref bucket);
            while (bucket < 256 && (bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                ProcessEntries(buffer, 0, bytesRead, entryBuffer, ref entryOffset, ref bucket);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Fail silently
        }
    }

    private void ProcessEntries(byte[] buffer, int offset, int count, byte[] entryBuffer, ref int entryOffset, ref int bucket)
    {
        int end = offset + count;
        while (offset < end && bucket < 256)
        {
            int toCopy = Math.Min(6 - entryOffset, end - offset);
            Buffer.BlockCopy(buffer, offset, entryBuffer, entryOffset, toCopy);
            entryOffset += toCopy;
            offset += toCopy;

            if (entryOffset == 6)
            {
                uint ip = BitConverter.ToUInt32(entryBuffer, 0);
                ushort country = BitConverter.ToUInt16(entryBuffer, 4);

                if (country == 0x4545) // Bucket separator marker "EE"
                {
                    bucket++;
                }
                else
                {
                    _buckets[bucket].Add((ip, country));
                }

                entryOffset = 0;
            }
        }
    }
}
