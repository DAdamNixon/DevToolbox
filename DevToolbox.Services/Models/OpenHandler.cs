using YamlDotNet.Serialization;

namespace DevToolbox.Services.Models;

/// <summary>Root of Config/openHandlers.yaml.</summary>
public class OpenHandlerConfig
{
    /// <summary>Evaluated top to bottom; the first pattern that matches wins.</summary>
    public List<OpenHandler> Handlers { get; set; } = new();
}

/// <summary>
/// "Files like this open in that program." Applies to every card on the dashboard,
/// hand-added and scanned alike.
/// <para>
/// This exists because the Windows file association is not trustworthy enough to build
/// on: .code-workspace had no handler registered at all, and .sln resolved through a
/// launcher shim to an older Visual Studio that could not open the file — both failing
/// silently. Naming the program removes the guesswork.
/// </para>
/// </summary>
public class OpenHandler : CustomOpenOption
{
    /// <summary>
    /// Glob tested against the file name, e.g. <c>*.sln</c>. A pattern containing a path
    /// separator is tested against the whole path instead, so a rule can be scoped to
    /// one tree.
    /// </summary>
    public string Match { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    [YamlIgnore]
    public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(Match);
}
