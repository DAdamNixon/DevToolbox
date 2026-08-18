using System.Diagnostics;
using System.Runtime.InteropServices;
using DevToolbox.Services;

namespace DevToolbox.UI.Services;

/// <summary>
/// Makes a second launch reopen the window that is already running instead of starting another copy
/// of the application.
/// <para>
/// This matters more here than it does in most applications, because closing the window hides it to
/// the tray. A user who "closed" DevToolbox and clicks the shortcut again saw nothing happen — and
/// got a second set of health monitors polling every endpoint, a second hosts-file watcher, and a
/// second writer into the same <c>logs.db</c>.
/// </para>
/// <para>
/// A mutex decides who wins; a broadcast window message is how the loser wakes the winner. The
/// obvious alternative — find the process and call <c>SetForegroundWindow</c> on its
/// <c>MainWindowHandle</c> — cannot work here: that property only considers *visible* windows, so it
/// returns nothing in precisely the hidden-to-tray case this exists for.
/// </para>
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const int HWND_BROADCAST = 0xFFFF;

    /// <summary>Grants any process the right to take the foreground, when the target is unknown.</summary>
    private const int ASFW_ANY = -1;

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// The message a second instance posts and the first instance listens for. Zero if Windows
    /// refused to register it, which callers must treat as "no signalling", not as a valid message.
    /// </summary>
    public static int ShowWindowMessage { get; } =
        RegisterWindowMessage(SingleInstanceKey.ShowWindowMessageName(Environment.ProcessPath));

    /// <summary>
    /// True if this process is the one instance. False means another is already running and has been
    /// asked to show itself, and this process should exit without starting anything.
    /// </summary>
    /// <param name="instance">
    /// The claim, which must be kept alive for as long as the application runs — releasing it early
    /// would let a later launch start a second copy alongside this one.
    /// </param>
    public static bool TryAcquire(out SingleInstance? instance)
    {
        instance = null;

        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: true, SingleInstanceKey.MutexName(Environment.ProcessPath), out var isFirst);

            if (!isFirst)
            {
                mutex.Dispose();
                SignalExistingInstance();
                return false;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // The name exists as some other kind of object, or this account cannot open it. Starting
            // is the right answer to not knowing: refusing to launch over a failed guard would be a
            // worse bug than the one the guard prevents.
            Debug.WriteLine($"Single-instance guard unavailable, starting anyway: {ex.Message}");
            return true;
        }

        instance = new SingleInstance(mutex);
        return true;
    }

    /// <summary>
    /// Asks the running instance to come back to the front.
    /// <para>
    /// Broadcast rather than posted to a known window, because the target may be hidden and so has
    /// no discoverable main window handle. Hidden top-level windows still receive broadcasts, and
    /// the message id is derived from this executable's path, so it cannot disturb anything else.
    /// </para>
    /// </summary>
    private static void SignalExistingInstance()
    {
        if (ShowWindowMessage == 0) return;

        // Windows will not let a background process take the foreground on its own, so the instance
        // being woken has to be granted the right by the one that has it — this one, which the user
        // just launched. Without this the window is restored but only flashes in the taskbar.
        AllowSetForegroundWindow(FindRunningInstanceId() ?? ASFW_ANY);

        PostMessage(HWND_BROADCAST, ShowWindowMessage, nint.Zero, nint.Zero);
    }

    /// <summary>The process id of the other copy of this same executable, if it can be identified.</summary>
    private static int? FindRunningInstanceId()
    {
        var self = Environment.ProcessId;
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            {
                using (process)
                {
                    if (process.Id == self) continue;

                    // MainModule throws for a process this account cannot inspect. Comparing paths
                    // keeps the grant narrow: a differently-installed copy is deliberately allowed
                    // to be running, and must not be handed the foreground.
                    try
                    {
                        if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                        {
                            return process.Id;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Enumeration raced with a process exiting. ASFW_ANY is the fallback.
        }

        return null;
    }

    public void Dispose()
    {
        // No ReleaseMutex: ownership is claimed on the thread that runs Main and the process holds
        // it until it exits, and releasing a mutex from the wrong thread has already cost this
        // project a runtime bug the compiler could not see. Disposing the handle is enough — an
        // abandoned mutex is irrelevant to a guard that only ever asks whether it already existed.
        _mutex.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint handle, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
