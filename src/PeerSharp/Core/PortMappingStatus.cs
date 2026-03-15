namespace PeerSharp.Core;

/// <summary>
/// Represents the result of a port mapping attempt.
/// </summary>
public enum PortMappingResult
{
    /// <summary>Port mapping has not been attempted yet or is disabled.</summary>
    NotAttempted,

    /// <summary>Port mapping is currently in progress.</summary>
    Pending,

    /// <summary>Port mapping was successful.</summary>
    Success,

    /// <summary>Port mapping failed.</summary>
    Failed
}

/// <summary>
/// Provides status information for a specific port mapping protocol.
/// </summary>
/// <param name="Protocol">The name of the protocol (e.g., "UPnP", "NAT-PMP").</param>
/// <param name="Result">The current result of the mapping attempt.</param>
/// <param name="ExternalPort">The external port that was mapped, if successful.</param>
/// <param name="ErrorMessage">A message describing the error, if the mapping failed.</param>
public sealed record PortMappingStatus(
    string Protocol,
    PortMappingResult Result = PortMappingResult.NotAttempted,
    int? ExternalPort = null,
    string? ErrorMessage = null);

