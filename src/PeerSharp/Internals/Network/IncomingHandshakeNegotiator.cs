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

    /// <summary>The protocol name a plaintext handshake carries after its length byte.</summary>
    private static ReadOnlySpan<byte> ProtocolName => "BitTorrent protocol"u8;

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
        // The opening byte narrows it down and does not settle it. A plaintext handshake starts with
        // 19, the length of "BitTorrent protocol"; an MSE exchange opens with a Diffie-Hellman key,
        // which is indistinguishable from random bytes by design and therefore starts with 19 about
        // once in every 256 connections. Committing to plaintext on that byte alone reads 96 bytes of
        // key as a handshake, derives an info hash belonging to no torrent, and drops a peer that was
        // talking to us perfectly correctly - too rare to look like anything but swarm churn.
        byte[] first = new byte[1];
        int read = await stream.ReadAsync(first.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return Failed;
        }

        if (first[0] != PlaintextMarker)
        {
            return await NegotiateEncryptedAsync(stream, first, resolver, logger, cancellationToken).ConfigureAwait(false);
        }

        byte[] opening = await ReadHandshakeLengthAsync(stream, cancellationToken).ConfigureAwait(false);
        if (opening.Length == 0)
        {
            return Failed;
        }

        // The protocol name settles it. An MSE peer always sends at least its 96-byte key, so there
        // is no risk of waiting on a plaintext peer that had nothing more to send.
        if (!opening.AsSpan(1, ProtocolName.Length).SequenceEqual(ProtocolName))
        {
            return await NegotiateEncryptedAsync(stream, opening, resolver, logger, cancellationToken).ConfigureAwait(false);
        }

        return new Result(true, opening, null, new InfoHash(opening.AsSpan(28, InfoHash.V1Length)));
    }

    /// <summary>
    /// Reads a handshake's worth of bytes, the first of which has already been taken from the stream.
    /// </summary>
    private static async Task<byte[]> ReadHandshakeLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] opening = new byte[HandshakeLength];
        opening[0] = PlaintextMarker;

        int read = 1;
        while (read < HandshakeLength)
        {
            int r = await stream.ReadAsync(opening.AsMemory(read, HandshakeLength - read), cancellationToken).ConfigureAwait(false);
            if (r == 0)
            {
                return [];
            }

            read += r;
        }

        return opening;
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
