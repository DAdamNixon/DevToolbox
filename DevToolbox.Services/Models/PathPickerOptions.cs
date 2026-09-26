namespace DevToolbox.Services.Models;

/// <summary>What a folder dialog should say and where it should open.</summary>
/// <param name="Title">The dialog's title.</param>
/// <param name="StartIn">An existing folder to open on, or null for the dialog's own default.</param>
/// <param name="AllowNewFolder">Whether the dialog offers to create a folder.</param>
public sealed record FolderPickerOptions(string Title, string? StartIn = null, bool AllowNewFolder = true);

/// <summary>What a file dialog should say, where it should open and what it should list.</summary>
/// <param name="Title">The dialog's title.</param>
/// <param name="StartIn">An existing folder to open on, or null for the dialog's own default.</param>
/// <param name="Filter">
/// Windows Forms filter syntax — <c>Name|*.a;*.b|Other|*.c</c> — which every picker reads.
/// </param>
public sealed record FilePickerOptions(string Title, string? StartIn = null, string Filter = FilePickerOptions.AllFiles)
{
    public const string AllFiles = "All Files (*.*)|*.*";
}
