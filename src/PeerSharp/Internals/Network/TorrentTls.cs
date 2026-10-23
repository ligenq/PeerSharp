using System.Formats.Asn1;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PeerSharp.Internals.Framework;

namespace PeerSharp.Internals.Network;

/// <summary>libtorrent SSL torrents: embedded CA, torrent-name authorization and info-hash SNI.</summary>
internal static class TorrentTls
{
    internal static bool IsClientHello(ReadOnlySpan<byte> opening) => opening.Length >= 6
        && opening[0] == 22 && opening[1] == 3 && opening[2] <= 4
        && opening[3] <= 64 && opening[5] == 1;

    internal static X509Certificate2 PrepareCertificate(X509Certificate2 certificate)
    {
        if (OperatingSystem.IsWindows())
        {
            using var rsa = certificate.GetRSAPrivateKey();
            using var ecdsa = certificate.GetECDsaPrivateKey();
            // Schannel cannot use the ephemeral CNG keys produced by CreateFromPem and
            // CopyWithPrivateKey. DefaultKeySet gives it a temporary persisted key whose
            // lifetime is owned by the returned certificate (no PersistKeySet flag).
            if (rsa is RSACng { Key.IsEphemeral: true } || ecdsa is ECDsaCng { Key.IsEphemeral: true })
            {
                byte[] pfx = certificate.Export(X509ContentType.Pkcs12);
                try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet); }
                finally { CryptographicOperations.ZeroMemory(pfx); }
            }
        }
        return new X509Certificate2(certificate);
    }

    internal static bool ValidateCertificate(Torrent torrent, X509Certificate? certificate, X509Chain? suppliedChain, bool serverCertificate = false)
    {
        if (certificate == null || !torrent.InfoFile.Info.IsSsl) return false;
        try
        {
            using var peer = new X509Certificate2(certificate);
            using var authority = X509Certificate2.CreateFromPem(torrent.InfoFile.Info.SslCertificateAuthority!);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(authority);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(serverCertificate ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2"));
            if (suppliedChain != null)
            {
                foreach (var element in suppliedChain.ChainElements) chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
            return chain.Build(peer) && MatchesTorrentName(peer, torrent.Name);
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
        catch (AsnContentException) { return false; }
    }

    internal static bool MatchesTorrentName(X509Certificate2 certificate, string name)
    {
        bool hasDnsName = false;
        var extension = certificate.Extensions["2.5.29.17"];
        if (extension != null)
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            while (sequence.HasData)
            {
                var tag = sequence.PeekTag();
                if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                {
                    hasDnsName = true;
                    string dns = sequence.ReadCharacterString(UniversalTagNumber.IA5String, tag);
                    if (dns == "*" || dns.Equals(name, StringComparison.Ordinal)) return true;
                }
                else sequence.ReadEncodedValue();
            }
            reader.ThrowIfNotEmpty();
        }
        if (hasDnsName) return false;
        string commonName = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);
        return commonName == "*" || commonName.Equals(name, StringComparison.Ordinal);
    }

    internal static async Task<SslStream> ConnectAsync(Stream stream, Torrent torrent, CancellationToken ct)
    {
        var certificate = torrent.SslCertificate;
        if (certificate?.HasPrivateKey != true || !ValidateCertificate(torrent, certificate, null))
            throw new AuthenticationException("SSL torrent needs a valid torrent certificate and private key in AddTorrentOptions.SslCertificate.");
        var tls = new SslStream(stream, leaveInnerStreamOpen: false,
            (_, remote, chain, _) => ValidateCertificate(torrent, remote, chain, serverCertificate: true));
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = Convert.ToHexStringLower(torrent.InfoFile.Info.GetTrackerInfoHash().Span),
                ClientCertificates = new X509CertificateCollection { certificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, ct).ConfigureAwait(false);
            return tls;
        }
        catch { await tls.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static async Task<(SslStream Stream, Torrent Torrent)> AcceptAsync(Stream stream, ITorrentResolver resolver, CancellationToken ct)
    {
        Torrent? selected = null;
        var tls = new SslStream(stream, leaveInnerStreamOpen: false,
            (_, remote, chain, _) => selected != null && ValidateCertificate(selected, remote, chain));
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ServerCertificateSelectionCallback = (_, sni) =>
                {
                    if (sni?.Length != 40 || !sni.All(Uri.IsHexDigit)) throw new AuthenticationException("Invalid SSL torrent SNI.");
                    selected = resolver.GetTorrent(new InfoHash(Convert.FromHexString(sni))) as Torrent;
                    return selected?.Started == true && selected.InfoFile.Info.IsSsl
                        && selected.SslCertificate?.HasPrivateKey == true
                        && ValidateCertificate(selected, selected.SslCertificate, null, serverCertificate: true) ? selected.SslCertificate : throw new AuthenticationException("SSL torrent certificate unavailable.");
                }
            }, ct).ConfigureAwait(false);
            if (selected == null) throw new AuthenticationException("Unknown SSL torrent SNI.");
            return (tls, selected);
        }
        catch { await tls.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
