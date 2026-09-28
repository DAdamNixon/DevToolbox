using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DevToolbox.UI.Services;

namespace DevToolbox.Tests;

/// <summary>
/// B2's regression check: the guard on a bare <see cref="Form"/>, no <c>MainWindow</c> and no
/// WebView — proving the mechanism itself, independent of everything else that form carries.
/// <para>
/// Runs on its own STA thread because a real <see cref="Form"/> requires one and xunit's own
/// threads are MTA. The thread pumps its own message loop just long enough to process the sent
/// messages, then is torn down.
/// </para>
/// </summary>
public sealed class WindowIconGuardTests
{
    private const int WM_SETICON = 0x0080;
    private const int WM_GETICON = 0x007F;
    private const int ICON_BIG = 1;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Runs <paramref name="body"/> on a dedicated STA thread and re-throws any failure on the caller's.</summary>
    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the STA test thread did not finish");
        if (failure is not null) throw failure;
    }

    [Fact]
    public void Attaching_sets_the_icon_immediately()
    {
        OnStaThread(() =>
        {
            using var form = new Form();
            var ours = SystemIcons.Asterisk;

            new WindowIconGuard(ours.Handle).Attach(form.Handle);
            Application.DoEvents();

            var current = SendMessage(form.Handle, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
            Assert.Equal(ours.Handle, current);
        });
    }

    [Fact]
    public void A_foreign_WM_SETICON_is_put_back_to_ours()
    {
        OnStaThread(() =>
        {
            using var form = new Form();
            var ours = SystemIcons.Asterisk;

            new WindowIconGuard(ours.Handle).Attach(form.Handle);
            Application.DoEvents();

            // The exact shape B0 measured: something else sets the window to the stock error icon.
            SendMessage(form.Handle, WM_SETICON, (IntPtr)ICON_BIG, SystemIcons.Error.Handle);
            Application.DoEvents();

            var current = SendMessage(form.Handle, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
            Assert.Equal(ours.Handle, current);
            Assert.NotEqual(SystemIcons.Error.Handle, current);
        });
    }
}
