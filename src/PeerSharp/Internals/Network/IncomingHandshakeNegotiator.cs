using Microsoft.Extensions.Logging;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Peers;
using PeerSharp.Internals.Utilities;

namespace PeerSharp.Internals.Network;

/// <summary>
/// Works out how an inbound peer is talking to us - plaintext or MSE - and completes whichever
/// handshake it turns out to be.
///
/// <para>
/// Shared by every transport on purpose. This logic previously existed only on the TCP path, so
/// incoming uTP connections assumed plaintext and rejected any peer that offered encryption. Since
/// encryption is near universal and uTP carries a large share of BitTorrent traffic, that silently
/// refused a whole class of inbound peers, visible only as "Invalid uTP handshake ... first byte"
/// warnings whose payload was in fact somebody's Diffie-Hellman key.
/// </para>
/// </summary>
internal static class IncomingHandshakeNegotiator
{
    /// <summary>The first byte of a plaintext BitTorrent handshake: the length of "BitTorrent protocol".</summary>
    private const byte PlaintextMarker = 19;

    private const int HandshakeLength = 68;

    internal readonly record struct Result(
        bool Success,
        byte[] Handshake,
        ProtocolEncryption? Encryption,
        InfoHash InfoHash);

    internal static Result Failed { get; } = new(false, [], null, default);

    /// <summary>
    /// Reads the opening bytes and completes the handshake the peer actually offered.
    /// </summary>
    /// <param name="stream">The freshly accepted connection.</param>
    /// <param name="resolver">Used to match the info hash, which for MSE is only known mid-handshake.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="cancellationToken">Bounds the whole exchange.</param>
    internal static async Task<Result> NegotiateAsync(
        Stream stream,
        ITorrentResolver resolver,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // One byte decides it. A plaintext handshake starts with 19; anything else is the start of an
        // MSE key exchange, which is indistinguishable from random bytes by design.
        byte[] first = new byte[1];
        int read = await stream.ReadAsync(first.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return Failed;
        }

        return first[0] == PlaintextMarker
            ? await ReadPlaintextAsync(stream, cancellationToken).ConfigureAwait(false)
            : await NegotiateEncryptedAsync(stream, first, resolver, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result> ReadPlaintextAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] handshake = new byte[HandshakeLength];
        handshake[0] = PlaintextMarker;

        int read = 1;
        while (read < HandshakeLength)
        {
            int r = await stream.ReadAsync(handshake.AsMemory(read, HandshakeLength - read), cancellationToken).ConfigureAwait(false);
            if (r == 0)
            {
                return Failed;
            }

            read += r;
        }

        return new Result(true, handshake, null, new InfoHash(handshake.AsSpan(28, InfoHash.V1Length)));
    }

    private static async Task<Result> NegotiateEncryptedAsync(
        Stream stream,
        byte[] initialBytes,
        ITorrentResolver resolver,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var pe = new ProtocolEncryptionHandshake(resolver);

        try
        {
            var response = pe.HandleIncoming(initialBytes);
            if (response.Length > 0)
            {
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            }

            byte[] buffer = new byte[4096];
            while (!pe.IsComplete && !pe.IsError)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return Failed;
                }

                response = pe.HandleIncoming(buffer.AsSpan(0, read).ToArray());
                if (response.Length > 0)
                {
                    await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                }
            }

            if (pe.IsComplete && pe.MatchedInfoHash != null && pe.Encryption != null)
            {
                return new Result(true, pe.ReceivedPayload ?? [], pe.Encryption, new InfoHash(pe.MatchedInfoHash));
            }

            return Failed;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Encryption handshake error on an incoming connection");
            return Failed;
        }
        finally
        {
            pe.Dispose();
        }
    }
}
