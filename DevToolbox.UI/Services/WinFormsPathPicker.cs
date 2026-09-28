using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.UI.Services;

/// <summary>
/// The Windows Forms folder and file dialogs, set up exactly the way the pages set them up before
/// they asked for them through <see cref="IPathPicker"/>.
/// <para>
/// Shown synchronously, on the caller's thread, which is the WebView's UI thread when the window
/// asks — the same thread the pages opened these dialogs on themselves. The browser view gets the
/// same picker, and the same result it always had.
/// </para>
/// </summary>
public sealed class WinFormsPathPicker : IPathPicker
{
    public Task<string?> PickFolderAsync(FolderPickerOptions options)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = options.Title,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = options.AllowNewFolder,
            RootFolder = Environment.SpecialFolder.MyComputer
        };

        if (options.StartIn is { } start) dialog.SelectedPath = start;

        return Task.FromResult<string?>(dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null);
    }

    public Task<string?> PickFileAsync(FilePickerOptions options)
    {
        using var dialog = new OpenFileDialog
        {
            Title = options.Title,
            Filter = options.Filter,
            CheckFileExists = true,
            CheckPathExists = true,
            InitialDirectory = options.StartIn ?? Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
        };

        return Task.FromResult<string?>(dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null);
    }
}
