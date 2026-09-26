using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace DevToolbox.UI.Linux;

/// <summary>
/// One running DevToolbox per user, and how a second launch finds the first.
/// <para>
/// The lock is an exclusive open of <c>instance.lock</c> in the app's data folder. The kernel lets
/// go of it when the process dies, however it dies, so a crash can never leave DevToolbox refusing
/// to start — the property the Windows app gets from its named mutex. Beside it, <c>instance</c>
/// holds the process id and the address, for <c>--quit</c>'s fallback and for an older copy.
/// </para>
/// <para>
/// The running copy also listens on a Unix socket, the counterpart of the message the Windows app
/// broadcasts to its first instance. A second launch sends <c>show</c>, and <c>--quit</c> sends
/// <c>quit</c>. The socket is in the session's runtime folder, which only this user can open and
/// which is emptied at logout, so a socket left by a crash does not outlive the session.
/// </para>
/// </summary>
internal sealed class Instance : IDisposable
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevToolbox");

    private static string LockPath => Path.Combine(Folder, "instance.lock");

    private static string InfoPath => Path.Combine(Folder, "instance");

    /// <summary>
    /// $XDG_RUNTIME_DIR when the session has one, as systemd sessions do; the data folder otherwise.
    /// Short on purpose, too: a socket's path cannot be longer than 107 bytes.
    /// </summary>
    private static string SocketPath => Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
        ? Path.Combine(runtime, "devtoolbox.sock")
        : Path.Combine(Folder, "instance.sock");

    /// <summary>Long enough for an activation token, short enough that nothing else fits.</summary>
    private const int MaxMessageLength = 1024;

    private readonly FileStream _lock;

    private Socket? _listener;

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

    /// <summary>
    /// Records this copy, and where it is listening. The address is null when the browser view did not
    /// start, which the window survives; the process id is still worth having, for --quit.
    /// </summary>
    public void Publish(string? url) =>
        File.WriteAllLines(InfoPath, [Environment.ProcessId.ToString(), url ?? ""]);

    /// <summary>The address of the copy that is running, or null.</summary>
    public static string? RunningUrl() => Read()?.Url is { Length: > 0 } url ? url : null;

    /// <summary>
    /// Answers second launches and --quit. <paramref name="handle"/> gets the command and its argument
    /// (for <c>show</c>, the launcher's activation token, if it had one) and returns the reply. It runs
    /// on a background thread. A socket that cannot be opened costs only that: this copy still runs,
    /// and a second launch falls back to the address in the instance file.
    /// </summary>
    public void Listen(Func<string, string?, string> handle)
    {
        try
        {
            // Safe to remove: holding the lock means no other copy is running, so any socket here is
            // one a copy left behind when it died.
            File.Delete(SocketPath);

            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(backlog: 4);
            _listener = listener;

            new Thread(() => Serve(listener, handle)) { IsBackground = true, Name = "DevToolbox instance socket" }.Start();
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"A second launch will not be able to find this window ({SocketPath}): {ex.Message}");
        }
    }

    private static void Serve(Socket listener, Func<string, string?, string> handle)
    {
        while (true)
        {
            Socket client;
            try
            {
                client = listener.Accept();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return; // Closed on the way out.
            }

            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 2000;
                    client.SendTimeout = 2000;
                    if (ReadLine(client) is not { } line) continue;

                    var space = line.IndexOf(' ');
                    var command = space < 0 ? line : line[..space];
                    var argument = space < 0 ? null : line[(space + 1)..];
                    client.Send(Encoding.UTF8.GetBytes(handle(command, argument) + "\n"));
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    // One caller that went away, or never finished its line, is not a reason to stop.
                }
            }
        }
    }

    /// <summary>
    /// Asks the running copy to show itself. Its reply: <c>shown</c> when it brought its own window
    /// forward, <c>open URL</c> when it runs in the browser and this process should open it, so that
    /// the browser gets this launch's activation token. Null when nothing answered.
    /// </summary>
    public static string? Show(string? activationToken) =>
        Send(activationToken is null ? "show" : $"show {activationToken}");

    /// <summary>Asks the running copy to stop, the way the session would. True if one was running.</summary>
    public static bool Quit()
    {
        var running = Read();
        var answered = Send("quit") is not null;

        if (running is null) return answered;

        try
        {
            using var process = Process.GetProcessById(running.Value.Pid);

            // SIGTERM when the socket did not answer, as from a copy older than it. The running copy
            // handles either by closing its window and shutting the server down cleanly.
            if (!answered)
            {
                Process.Start("kill", ["-TERM", running.Value.Pid.ToString()])?.WaitForExit();
            }

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
            return answered; // Not running: the file outlived it.
        }
    }

    private static string? Send(string message)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.ReceiveTimeout = 5000;
            socket.SendTimeout = 2000;
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.UTF8.GetBytes(message + "\n"));
            return ReadLine(socket);
        }
        catch (SocketException)
        {
            return null; // No socket, or nothing listening on it.
        }
    }

    /// <summary>One line, without its newline; null if the other end closed first or sent too much.</summary>
    private static string? ReadLine(Socket socket)
    {
        var received = new List<byte>();
        var buffer = new byte[256];

        while (received.Count <= MaxMessageLength)
        {
            var count = socket.Receive(buffer);
            if (count == 0) return null;

            var end = Array.IndexOf(buffer, (byte)'\n', 0, count);
            received.AddRange(buffer.AsSpan(0, end < 0 ? count : end).ToArray());
            if (end >= 0) return Encoding.UTF8.GetString(received.ToArray());
        }

        return null;
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
        if (_listener is not null)
        {
            _listener.Dispose();
            try { File.Delete(SocketPath); } catch (IOException) { /* the next start removes it */ }
        }

        try { File.Delete(InfoPath); } catch (IOException) { /* the next start overwrites it */ }
        _lock.Dispose();
    }
}
