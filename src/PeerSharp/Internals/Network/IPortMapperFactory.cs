using PeerSharp.Internals.Utilities;

namespace PeerSharp.Internals.Network;

/// <summary>
/// Factory for creating port mappers based on settings.
/// </summary>
internal interface IPortMapperFactory
{
    IEnumerable<IPortMapper> CreateMappers(Settings settings);
}

internal class PortMapperFactory : IPortMapperFactory
{
    public IEnumerable<IPortMapper> CreateMappers(Settings settings)
    {
        var mappers = new List<IPortMapper>();

        if (settings.Connection.UpnpPortMapping)
        {
            mappers.Add(new UpnpPortMapping());
        }

        if (settings.Connection.NatPmpPortMapping)
        {
            mappers.Add(new NatPmpPortMapping());
        }

        return mappers;
    }
}
