using System.Text.Json;

namespace DevToolbox.Services.Models.Hosts;

/// <summary>
/// How the request and the response are written and read.
/// <para>
/// Shared by both sides on purpose. The two halves of this exchange are separate processes, and if
/// one ever serialised with a naming policy the other did not expect, every field would silently
/// arrive empty — which surfaces as a baffling error about a missing path rather than as a version
/// mismatch. Case-insensitive reading makes that class of mistake impossible.
/// </para>
/// </summary>
public static class HostsWriteJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>Operations the elevated side of the app knows how to perform.</summary>
public static class HostsWriteOperations
{
    public const string Write = "write";
    public const string GrantWrite = "grantWrite";
    public const string RevokeWrite = "revokeWrite";
}

/// <summary>
/// Exit codes of the elevated run. Named so neither side compares against a bare number.
/// </summary>
public static class HostsWriterExitCodes
{
    public const int Success = 0;
    public const int Unexpected = 1;

    /// <summary>The staged payload does not hash to what the request claims.</summary>
    public const int PayloadMismatch = 2;

    /// <summary>The hosts file changed between being read and being written.</summary>
    public const int TargetChanged = 3;

    /// <summary>Access refused despite elevation — usually anti-malware rather than permissions.</summary>
    public const int Denied = 4;

    public const int MalformedRequest = 5;

    /// <summary>The write happened but the file does not match what was written.</summary>
    public const int VerifyFailed = 6;
}

/// <summary>
/// What the elevated run is being asked to do, written to <c>request.json</c> beside the payload.
/// <para>
/// The file content travels as a separate <c>payload.hosts</c> file and only the directory path
/// goes on the command line. A hosts file exceeds the command-line length limit, quoting it is
/// hostile, and a base64 blob that rewrites the hosts file is a textbook malware signature — so
/// the request is a directory, not an argument list.
/// </para>
/// </summary>
public sealed class HostsWriteRequest
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>One of the constants in <see cref="HostsWriteOperations"/>.</summary>
    public string Operation { get; set; } = HostsWriteOperations.Write;

    /// <summary>The hosts file. Must already exist — the elevated side never creates one.</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>Hash of <c>payload.hosts</c>, so a truncated staging write cannot be applied.</summary>
    public string PayloadSha256 { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the target as it was when we read it. The precondition that stops us clobbering an
    /// edit somebody made while the elevation prompt was on screen.
    /// </summary>
    public string OriginalSha256 { get; set; } = string.Empty;

    /// <summary>Whose access is being granted or revoked, as a SID.</summary>
    public string? PrincipalSid { get; set; }

    public DateTime RequestedAtUtc { get; set; }
}

/// <summary>What the elevated run reports back, written to <c>result.json</c>.</summary>
public sealed class HostsWriteResponse
{
    public int ExitCode { get; set; }
    public string? Message { get; set; }
    public string? VerifiedSha256 { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}
