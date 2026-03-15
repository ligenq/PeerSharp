using PeerSharp.PieceWriter;

namespace PeerSharp.PiecePicking;

/// <summary>
/// Composition root for piece picking components.
/// Keeps construction logic out of consumers without adding new projects.
/// </summary>
internal static class PiecePickingModule
{
    public static PiecePicker CreatePicker(IPiecePickerContext context, TimeProvider? timeProvider = null, Random? random = null)
    {
        return new PiecePicker(
            context,
            timeProvider ?? TimeProvider.System,
            random ?? Random.Shared);
    }

    public static PieceChecker CreateChecker(IInternalFiles files, IPieceCheckerContext context, IProgress<PieceCheckProgress>? progress = null)
    {
        return new PieceChecker(files, context, progress);
    }
}
