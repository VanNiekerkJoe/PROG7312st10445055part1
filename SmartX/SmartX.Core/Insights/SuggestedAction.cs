using SmartX.Core.Commands;

namespace SmartX.Core.Insights;

/// <summary>
/// One entry in the dashboard's "Suggested Actions / Automated Insights"
/// panel. <c>Command</c> and <c>DeviceId</c> are set when the suggestion can
/// be applied with one click; both are null for pure "inspect this" hints.
/// <c>Basis</c> is "learned", "rule" or "history" so the UI can show why.
/// </summary>
public sealed record SuggestedAction(
    string Id,
    string Title,
    string Detail,
    string? DeviceId,
    CommandType? Command,
    double Confidence,
    string Basis);
