using System.Runtime.InteropServices;

namespace DevToolbox.UI.Services;

/// <summary>Win32 calls this project needs. Kept to the minimum and in one place.</summary>
internal static class NativeMethods
{
    /// <summary>
    /// Releases an icon handle produced by <c>Bitmap.GetHicon</c>.
    /// <para>
    /// <see cref="System.Drawing.Icon.FromHandle(nint)"/> does not take ownership, so without this
    /// every icon drawn would leak a GDI handle for the life of the process.
    /// </para>
    /// <para>
    /// Declared with <see cref="DllImportAttribute"/> rather than the newer source-generated
    /// <c>LibraryImport</c>, which would require turning on unsafe code for the whole project — a
    /// disproportionate change for a single call taking a handle and returning a bool.
    /// </para>
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint handle);
}
