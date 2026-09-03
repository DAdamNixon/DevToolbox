using DevToolbox.Services.Models;

namespace DevToolbox.Mcp.Core;

/// <summary>
/// Whether a configured log location is usable at all — that its path is a path, and nothing more
/// than that.
/// <para>
/// <b>This type used to be <c>LocalPathPolicy</c>, and it used to decide more.</b> Until
/// 2026-09-03 it refused every UNC path and every mapped network drive, which on this workstation
/// meant admitting one location out of ten. That was scope control for a build phase: the
/// instruction was to read only the local logs while the server was being written, and a policy
/// was the honest way to express it — enforceable and testable, where a hardcoded
/// <c>C:\inetpub\LogFiles</c> would have been neither, and EE-specific in a codebase that is
/// agnostic by design.
/// </para>
/// <para>
/// The build phase ended and the restriction was lifted. What did not survive with it is the thing
/// the restriction was quietly also buying: an ingest walks <em>every</em> location it is given,
/// which is free at one local directory and is not free at ten — <c>DbLogService</c> measures one
/// archive walk at 17 seconds across 238,000 files, and four of the locations are web servers
/// serving customers. Removing this filter without replacing that bound would have turned a single
/// careless <c>prepare_table</c> into an SMB scan of production. So the bound moved rather than
/// vanished: it is now a <b>required per-call argument</b>, and it lives in
/// <see cref="LocationSelection"/>. Scope is chosen by the caller, per call, in the open — not by
/// this type, once, for the life of the process.
/// </para>
/// <para>
/// What is left here is config validity, and it is worth keeping separate for the reason the
/// refusals were always reported rather than dropped: a dev who cannot see a location in the list
/// has to be able to tell a broken config entry from a decision. That distinction is now the whole
/// job.
/// </para>
/// <para>
/// The three policies form a set, and each covers a different untrusted input. This one: the
/// <b>configuration</b> the dev wrote. <see cref="LocationSelection"/>: <b>which</b> of those an
/// agent asked for. <see cref="LogFileNamePolicy"/>: the <b>name</b> searched inside them. That
/// last one was written as the argument-side half of a pair and is now, with locality gone, the
/// only bound on what gets read once a location is in scope.
/// </para>
/// </summary>
internal static class LocationPolicy
{
    /// <summary>Why a location was refused, in words meant for the caller.</summary>
    internal const string ReasonNotRooted =
        "Refused: path is not fully qualified. A location must name an absolute local path or a UNC share.";

    internal const string ReasonBlank = "Refused: location has no path.";

    /// <summary>
    /// Null when the location is usable; otherwise the reason it is not.
    /// <para>
    /// Deliberately does NOT test whether the directory exists, and that matters more now than it
    /// did when everything admitted was local. A network share is absent for reasons that have
    /// nothing to do with configuration — the server is down, the VPN is off, the account has no
    /// rights today — and every one of those is a fact about this moment, not about
    /// <c>log_paths.yaml</c>. Collapsing them into a refusal would make an outage look like a
    /// policy decision and send the dev to edit a file that is correct.
    /// </para>
    /// </summary>
    internal static string? Refuse(LogLocation location)
    {
        var path = location.Path;

        if (string.IsNullOrWhiteSpace(path))
            return ReasonBlank;

        // UNC paths are fully qualified, so this admits them — which is the point of the change.
        // It still refuses a bare relative path, because that resolves against whatever the
        // working directory happens to be, and nobody configured that.
        if (!Path.IsPathFullyQualified(path))
            return ReasonNotRooted;

        return null;
    }

    internal static bool IsUsable(LogLocation location) => Refuse(location) is null;
}
