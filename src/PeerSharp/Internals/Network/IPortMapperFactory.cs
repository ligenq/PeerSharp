using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILoggerFactory _loggerFactory;

    public PortMapperFactory()
        : this(NullLoggerFactory.Instance)
    {
    }

    public PortMapperFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public IEnumerable<IPortMapper> CreateMappers(Settings settings)
    {
        var mappers = new List<IPortMapper>();

        if (settings.Connection.UpnpPortMapping)
        {
            mappers.Add(new UpnpPortMapping(_loggerFactory));
        }

        if (settings.Connection.NatPmpPortMapping)
        {
            mappers.Add(new NatPmpPortMapping(_loggerFactory));
        }

        return mappers;
    }
}
