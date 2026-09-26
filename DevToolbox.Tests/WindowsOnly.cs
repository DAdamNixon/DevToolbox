namespace DevToolbox.Tests;

/// <summary>
/// A test that is Windows-only by design: it is about drive letters, UNC shares, <c>%VAR%</c>
/// paths or some other thing that only exists there. Skipped, and says why, everywhere else.
/// <para>
/// Not for a test that merely happens to use a backslash in its fixture — that is a test to fix,
/// because the code under it runs on Linux too.
/// </para>
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows-only by design.";
    }
}

/// <inheritdoc cref="WindowsFactAttribute"/>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows-only by design.";
    }
}
