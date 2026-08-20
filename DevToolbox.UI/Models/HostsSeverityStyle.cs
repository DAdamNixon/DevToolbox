using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.UI.Models;

/// <summary>
/// The complete class strings for one severity level.
/// <para>
/// Spelled out rather than composed as <c>$"bg-{colour}-500/20"</c>, because Tailwind scans source
/// text for literal class names and cannot see a string built at runtime. An interpolated version
/// only ever works for colours that happen to appear literally somewhere else, which is how a
/// status dot ends up rendering with no colour at all.
/// </para>
/// </summary>
public sealed record HostsSeverityStyle(
    string Dot,
    string Chip,
    string Text,
    string CardBorder,
    string Icon,
    string Label);

/// <summary>
/// Maps a severity onto its presentation.
/// <para>
/// Lives in the UI project deliberately: Tailwind's content globs cover
/// <c>Pages</c>, <c>Components</c>, <c>Shared</c>, <c>Services</c> and <c>Models</c> here, and
/// nothing in <c>DevToolbox.Services</c>. A switch like this one placed there would compile fine
/// and render colourless.
/// </para>
/// </summary>
public static class HostsSeverityStyles
{
    public static HostsSeverityStyle For(HostsSeverityLevel level) => level switch
    {
        HostsSeverityLevel.Danger => new HostsSeverityStyle(
            "bg-red-400",
            "bg-red-500/20 text-red-300",
            "text-red-300",
            "border-red-500/40",
            "bi-exclamation-octagon-fill",
            "danger"),

        HostsSeverityLevel.Caution => new HostsSeverityStyle(
            "bg-amber-400",
            "bg-amber-500/20 text-amber-300",
            "text-amber-300",
            "border-amber-500/40",
            "bi-exclamation-triangle-fill",
            "caution"),

        _ => new HostsSeverityStyle(
            "bg-emerald-400",
            "bg-emerald-500/20 text-emerald-300",
            "text-emerald-300",
            "border-dark-border",
            "bi-check-circle-fill",
            "normal"),
    };

    /// <summary>What an option being switched on means, in words, for a tooltip or a dialog.</summary>
    public static string Describe(HostsSeverityLevel level) => level switch
    {
        HostsSeverityLevel.Danger => "This option is flagged as dangerous.",
        HostsSeverityLevel.Caution => "This option is flagged for caution.",
        _ => string.Empty,
    };
}
