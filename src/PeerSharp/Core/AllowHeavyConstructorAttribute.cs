namespace PeerSharp.Core;

/// <summary>
/// Exempts one constructor from the architecture rule that keeps I/O out of constructors, for the
/// rare case where the work is what makes the constructed object's contract true.
/// </summary>
/// <remarks>
/// Applies to the constructor it is written on and to nothing else, and has to say why: an
/// exemption without a reason is indistinguishable from one made to get a build through.
/// </remarks>
[AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
internal sealed class AllowHeavyConstructorAttribute(string reason) : Attribute
{
    /// <summary>Why this constructor does the work it does.</summary>
    public string Reason { get; } = reason;
}
