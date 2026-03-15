using System.Net;

namespace PeerSharp.Core;

/// <summary>
/// Represents the persisted state of the Distributed Hash Table (DHT).
/// </summary>
/// <param name="NodeId">The local node ID.</param>
/// <param name="Nodes">The list of known nodes (routing table).</param>
public sealed record DhtState(byte[]? NodeId, IReadOnlyList<DhtNode> Nodes);

/// <summary>
/// Represents a single node in the DHT routing table.
/// </summary>
/// <param name="Id">The 20-byte node ID.</param>
/// <param name="EndPoint">The IP endpoint of the node.</param>
public sealed record DhtNode(byte[] Id, IPEndPoint EndPoint);
