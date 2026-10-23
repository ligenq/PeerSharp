namespace PeerSharp.Tests.Core;

public sealed class ConnectionSettingsTests
{
    [Fact]
    public void DirectionalPoliciesOverrideTheLegacyPolicyIndependently()
    {
        var settings = new ConnectionSettings { Encryption = Encryption.Require };
        Assert.Equal(Encryption.Require, settings.EffectiveIncomingEncryption);
        Assert.Equal(Encryption.Require, settings.EffectiveOutgoingEncryption);
        settings.IncomingEncryption = Encryption.Allow;
        Assert.Equal(Encryption.Allow, settings.EffectiveIncomingEncryption);
        Assert.Equal(Encryption.Require, settings.EffectiveOutgoingEncryption);
        settings.OutgoingEncryption = Encryption.Refuse;
        Assert.Equal(Encryption.Allow, settings.EffectiveIncomingEncryption);
        Assert.Equal(Encryption.Refuse, settings.EffectiveOutgoingEncryption);
        settings.IncomingEncryption = null;
        Assert.Equal(Encryption.Require, settings.EffectiveIncomingEncryption);
    }

}
