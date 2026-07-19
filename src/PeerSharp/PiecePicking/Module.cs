using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using PeerSharp.PieceWriter;

namespace PeerSharp.PiecePicking;

/// <summary>
/// Composition root for piece picking components.
/// Keeps construction logic out of consumers without adding new projects.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class PiecePickingModule
{
    public static PiecePicker CreatePicker(IPiecePickerContext context, TimeProvider? timeProvider = null, Random? random = null, ILoggerFactory? loggerFactory = null)
    {
        return new PiecePicker(
            context,
            timeProvider ?? TimeProvider.System,
            random ?? Random.Shared,
            loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
    }

    public static PieceChecker CreateChecker(IInternalFiles files, IPieceCheckerContext context, IProgress<PieceCheckProgress>? progress = null, ILoggerFactory? loggerFactory = null)
    {
        return new PieceChecker(files, context, progress, loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
    }
}
