using System.Diagnostics;

namespace DevToolbox.UI.Linux;

/// <summary>
/// One running DevToolbox per user, and how a second launch finds the first.
/// <para>
/// The lock is an exclusive open of <c>instance.lock</c> in the app's data folder. The kernel lets
/// go of it when the process dies, however it dies, so a crash can never leave DevToolbox refusing
/// to start — the property the Windows app gets from its named mutex. Beside it, <c>instance</c>
/// holds the process id and the address, for a second launch to open and for <c>--quit</c> to stop.
/// </para>
/// </summary>
internal sealed class Instance : IDisposable
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevToolbox");

    private static string LockPath => Path.Combine(Folder, "instance.lock");

    private static string InfoPath => Path.Combine(Folder, "instance");

    private readonly FileStream _lock;

    private Instance(FileStream held)
    {
        _lock = held;
    }

    /// <summary>The lock, or null when another copy holds it.</summary>
    public static Instance? TryAcquire()
    {
        Directory.CreateDirectory(Folder);

        try
        {
            return new Instance(new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Records where this copy is listening, once it is.</summary>
    public void Publish(string url) =>
        File.WriteAllLines(InfoPath, [Environment.ProcessId.ToString(), url]);

    /// <summary>The address of the copy that is running, or null.</summary>
    public static string? RunningUrl() => Read()?.Url;

    /// <summary>Asks the running copy to stop, the way the session would. True if one was running.</summary>
    public static bool Quit()
    {
        if (Read() is not { } running) return false;

        try
        {
            using var process = Process.GetProcessById(running.Pid);

            // SIGTERM, which the running copy handles by closing its window and shutting the server
            // down cleanly.
            Process.Start("kill", ["-TERM", running.Pid.ToString()])?.WaitForExit();

            // Polled, because WaitForExit does not wait for a process this one did not start. Waited
            // for at all so that a launch right after --quit — an upgrade, a restart — finds the lock
            // free rather than the copy that is still on its way out.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
                process.Refresh();
            }

            return true;
        }
        catch (ArgumentException)
        {
            return false; // Not running: the file outlived it.
        }
    }

    private static (int Pid, string Url)? Read()
    {
        try
        {
            var lines = File.ReadAllLines(InfoPath);
            return lines.Length >= 2 && int.TryParse(lines[0], out var pid) ? (pid, lines[1]) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { File.Delete(InfoPath); } catch (IOException) { /* the next start overwrites it */ }
        _lock.Dispose();
    }
}
