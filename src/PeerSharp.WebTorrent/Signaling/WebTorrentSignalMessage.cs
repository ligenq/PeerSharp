using System.Text.Json.Serialization;

namespace PeerSharp.WebTorrent.Signaling;

internal sealed record WebTorrentSignalMessage(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("info_hash")] string InfoHash,
    [property: JsonPropertyName("peer_id")] string PeerId,
    [property: JsonPropertyName("offer_id")] string? OfferId = null,
    [property: JsonPropertyName("offer")] WebTorrentSdp? Offer = null,
    [property: JsonPropertyName("answer")] WebTorrentSdp? Answer = null,
    [property: JsonPropertyName("candidate")]
    [property: JsonConverter(typeof(WebTorrentCandidateConverter))]
    string? Candidate = null,
    [property: JsonPropertyName("interval")] int? Interval = null)
{
    public string? OfferSdp => Offer?.Sdp;
    public string? AnswerSdp => Answer?.Sdp;
    public string? AnswerType => Answer?.Type;

    public WebTorrentSignalMessage AsOfferFromAnswer()
    {
        return this with
        {
            Offer = Answer,
            Answer = null
        };
    }
}

internal sealed record WebTorrentSdp(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sdp")] string Sdp);
