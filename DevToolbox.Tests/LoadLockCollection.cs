namespace DevToolbox.Tests;

/// <summary>
/// Every test class that runs a real log load, run one class at a time.
/// <para>
/// <c>DbLogService</c>'s load lock is static and process-wide, by design: two loads in one process
/// queue behind each other. Tests that hold a load open on purpose — a file blocked mid-read, a
/// batch gated in the writer — hold that lock for as long, and several of them give a load a couple
/// of seconds to get going. Run in parallel, one class's held load ate another's two seconds. In one
/// collection they take turns, and everything else still runs alongside.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class LoadLockCollection
{
    public const string Name = "Log load lock";
}
