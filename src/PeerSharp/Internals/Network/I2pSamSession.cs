using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PeerSharp.Internals.Network;

/// <summary>One long-lived SAM 3.1 STREAM session shared by an engine's trackers and peers.</summary>
internal sealed class I2pSamSession : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Func<Stream, CancellationToken, Task> _incoming;
    private readonly SemaphoreSlim _initialize = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _id = "ps" + Guid.NewGuid().ToString("N");
    private Stream? _control;
    private readonly List<Task> _background = [];
    private int _disposed;
    public string? Destination { get; private set; }

    // Each outstanding STREAM ACCEPT costs one idle socket to the bridge and can serve one
    // inbound stream, so a handful keeps the router from having nowhere to deliver the next one.
    private const int ConcurrentAccepts = 4;
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AlreadyAcceptingDelay = TimeSpan.FromSeconds(30);

    public I2pSamSession(string host, int port, TimeProvider time, ILogger logger, Func<Stream, CancellationToken, Task> incoming)
    {
        _host = host; _port = port; _timeProvider = time; _logger = logger; _incoming = incoming;
    }

    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _initialize.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_control != null) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            Stream? control = await OpenAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                await CommandAsync(control, $"SESSION CREATE STYLE=STREAM ID={_id} DESTINATION=TRANSIENT SIGNATURE_TYPE=7 i2cp.leaseSetEncType=4,0 inbound.quantity=3 outbound.quantity=3", "SESSION STATUS", deadline.Token).ConfigureAwait(false);
                var naming = await CommandAsync(control, "NAMING LOOKUP NAME=ME", "NAMING REPLY", deadline.Token).ConfigureAwait(false);
                Destination = Value(naming, "VALUE") ?? throw new IOException("SAM did not return the local Destination.");
                ValidateDestination(Destination);
                _control = control;
                var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _background.RemoveAll(task => task.IsCompleted);
                var accepts = new Task[ConcurrentAccepts];
                for (int i = 0; i < ConcurrentAccepts; i++)
                {
                    accepts[i] = AcceptLoopAsync(sessionStop.Token);
                    _background.Add(accepts[i]);
                }
                _background.Add(MonitorControlAsync(control, sessionStop, accepts));
                control = null;
            }
            finally { if (control != null) await control.DisposeAsync().ConfigureAwait(false); }
        }
        finally { _initialize.Release(); }
    }

    public async Task<Stream> ConnectAsync(string destination, CancellationToken ct)
    {
        ValidateDestination(destination);
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        string target = destination;
        if (target.EndsWith(".i2p", StringComparison.OrdinalIgnoreCase))
        {
            if (target.Length > 516) target = target[..^4];
            else
            {
                await using var lookup = await OpenAsync(deadline.Token).ConfigureAwait(false);
                var naming = await CommandAsync(lookup, "NAMING LOOKUP NAME=" + target, "NAMING REPLY", deadline.Token).ConfigureAwait(false);
                target = Value(naming, "VALUE") ?? throw new IOException("I2P name was not found.");
            }
        }
        ValidateDestination(target);
        var stream = await OpenAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            await CommandAsync(stream, $"STREAM CONNECT ID={_id} DESTINATION={target} SILENT=false", "STREAM STATUS", deadline.Token).ConfigureAwait(false);
            return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public static bool IsI2pUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Host.EndsWith(".i2p", StringComparison.OrdinalIgnoreCase);

    public static string HashToHostname(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != 32) throw new ArgumentException("I2P destination hashes contain 32 bytes.", nameof(hash));
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var output = new StringBuilder(60);
        int bits = 0, value = 0;
        foreach (byte item in hash)
        {
            value = (value << 8) | item; bits += 8;
            while (bits >= 5) { bits -= 5; output.Append(alphabet[(value >> bits) & 31]); }
        }
        if (bits > 0) output.Append(alphabet[(value << (5 - bits)) & 31]);
        return output.Append(".b32.i2p").ToString();
    }

    internal static void ValidateDestination(string value)
    {
        if (value.Length is < 1 or > 1024 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '~' and not '=' and not '.'))
            throw new ArgumentException("Invalid I2P destination.", nameof(value));
    }

    private async Task<Stream> OpenAsync(CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
            var stream = client.GetStream();
            await CommandAsync(stream, "HELLO VERSION MIN=3.1 MAX=3.1", "HELLO REPLY", ct).ConfigureAwait(false);
            return stream;
        }
        catch { client.Dispose(); throw; }
    }

    internal static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[16 * 1024];
        int length = 0;
        while (length < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(length, 1), ct).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("SAM closed the connection.");
            if (buffer[length++] == '\n') return Encoding.ASCII.GetString(buffer, 0, length - 1).TrimEnd('\r');
        }
        throw new IOException("SAM response exceeds the line limit.");
    }

    internal static async Task<string> CommandAsync(Stream stream, string command, string reply, CancellationToken ct)
    {
        string line = await SendAsync(stream, command, ct).ConfigureAwait(false);
        if (!line.StartsWith(reply + " ", StringComparison.Ordinal) || Value(line, "RESULT") != "OK")
            throw new IOException("SAM command failed: " + line);
        return line;
    }

    /// <summary>Sends one command and returns its reply line without judging the result.</summary>
    private static async Task<string> SendAsync(Stream stream, string command, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), ct).ConfigureAwait(false);
        return await ReadLineAsync(stream, ct).ConfigureAwait(false);
    }

    private static string? Value(string line, string key) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault(token => token.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan? backOff = null;
            Stream? stream = null;
            try
            {
                stream = await OpenAsync(ct).ConfigureAwait(false);
                string status = await SendAsync(stream, $"STREAM ACCEPT ID={_id} SILENT=false", ct).ConfigureAwait(false);
                string? result = Value(status, "RESULT");
                if (result == "ALREADY_ACCEPTING")
                {
                    // Routers differ on whether one session may hold several STREAM ACCEPTs at
                    // once. Where it may not, the extra acceptors stand down and retry slowly
                    // while the one that got the slot keeps serving inbound streams.
                    backOff = AlreadyAcceptingDelay;
                }
                else if (!status.StartsWith("STREAM STATUS ", StringComparison.Ordinal) || result != "OK")
                {
                    throw new IOException("SAM command failed: " + status);
                }
                else
                {
                    // The next line identifies the remote destination. Read exactly through newline:
                    // the BitTorrent handshake can already follow in the same socket receive.
                    string remote = (await ReadLineAsync(stream, ct).ConfigureAwait(false)).Split(' ')[0];
                    ValidateDestination(remote);
                    // Hand the stream off without awaiting it, so the next STREAM ACCEPT is posted
                    // at once. Awaiting the handler let one peer that opened a stream and then said
                    // nothing hold up every inbound connection for the whole handshake deadline,
                    // which the TCP and uTP listeners avoid by dispatching per connection too.
                    _ = HandOffAsync(stream, ct);
                    stream = null;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "I2P accept failed; ensure the router SAM bridge is enabled");
                backOff = AcceptRetryDelay;
            }
            finally { if (stream != null) await stream.DisposeAsync().ConfigureAwait(false); }

            if (backOff != null)
            {
                try { await Task.Delay(backOff.Value, _timeProvider, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }

    /// <summary>
    /// Owns one accepted stream for the rest of its handshake. Written as an async method so a
    /// handler that throws before its first await faults this task instead of the accept loop,
    /// and so no inbound connection can leave an exception unobserved.
    /// </summary>
    private async Task HandOffAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            await _incoming(stream, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "I2P inbound connection handler failed");
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Watches one control socket and tears its session generation down when it ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes <paramref name="accepts"/> because <paramref name="sessionStop"/> outlives this read:
    /// the accept loops hold its token, and <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
    /// on a disposed source throws. The source is therefore disposed only once every loop that
    /// observes it has stopped, which is what makes this method its owner rather than merely the
    /// first task to notice the session is over.
    /// </para>
    /// </remarks>
    private async Task MonitorControlAsync(Stream control, CancellationTokenSource sessionStop, Task[] accepts)
    {
        try
        {
            byte[] unused = new byte[1];
            // SAM 3.1 has no keepalive traffic. EOF means the destination session is gone.
            int read = await control.ReadAsync(unused, sessionStop.Token).ConfigureAwait(false);
            if (read != 0) _logger.LogDebug("Unexpected SAM 3.1 control data; reopening the session");
        }
        catch (OperationCanceledException) when (sessionStop.IsCancellationRequested) { /* Shutdown. */ }
        // DisposeAsync cancels the lifetime and then disposes the control stream, so a shutdown that
        // lands between the two surfaces as a disposed socket rather than as cancellation. Reading a
        // closed session is what this method is here to observe, however it is spelled.
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "I2P SAM session disconnected");
        }
        finally
        {
            await _initialize.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_control, control)) { _control = null; Destination = null; }
                await sessionStop.CancelAsync().ConfigureAwait(false);
                await control.DisposeAsync().ConfigureAwait(false);
            }
            finally { _initialize.Release(); }
            // Outside the gate: the loops do not take it, and DisposeAsync waits on this task.
            try { await Task.WhenAll(accepts).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "I2P accept loop failed while the session was closing"); }
            sessionStop.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task[] background;
        await _initialize.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_control != null) await _control.DisposeAsync().ConfigureAwait(false);
            _control = null;
            background = _background.ToArray();
        }
        finally { _initialize.Release(); }
        // The monitor also takes the initialization gate, so drain it after releasing the gate.
        await Task.WhenAll(background).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
