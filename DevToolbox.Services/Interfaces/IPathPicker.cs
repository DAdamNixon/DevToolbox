using DevToolbox.Services.Models;

namespace DevToolbox.Services.Interfaces;

/// <summary>
/// The operating system's own folder and file dialogs, for the Browse buttons.
/// <para>
/// Native rather than an in-page browser because this is a desktop tool: the native dialog is the
/// one that can reach a mapped drive, a UNC share or a mounted volume, and it is what people expect
/// a Browse button to open. Each host registers the picker its platform has — Windows Forms on
/// Windows, zenity on Linux.
/// </para>
/// </summary>
public interface IPathPicker
{
    /// <summary>The chosen folder, or null when the dialog was cancelled.</summary>
    /// <exception cref="InvalidOperationException">No dialog can be shown here; the field is still typeable.</exception>
    Task<string?> PickFolderAsync(FolderPickerOptions options);

    /// <summary>The chosen file, or null when the dialog was cancelled.</summary>
    /// <exception cref="InvalidOperationException">No dialog can be shown here; the field is still typeable.</exception>
    Task<string?> PickFileAsync(FilePickerOptions options);
}
