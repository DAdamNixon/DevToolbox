using System.Drawing.Imaging;
using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.UI.Services;

/// <summary>
/// Owns the window's icon: turns the saved choice into a real <see cref="Icon"/>, swaps it when the
/// setting changes, and keeps the folder of icons the user has imported.
/// <para>
/// Only the running window is in scope, and the Settings page says so. The icon on DevToolbox.exe,
/// on a desktop shortcut, and on a taskbar entry that is pinned but not running all come from
/// <c>&lt;ApplicationIcon&gt;</c> in the csproj — that one is compiled into the executable and no
/// setting can reach it. Presenting both as a single control would promise something this cannot
/// deliver.
/// </para>
/// <para>
/// The tray icon is not in scope either. <see cref="HostsTrayIcon"/> draws its own mark in the
/// colour of whichever hosts option is live, and that colour is the whole point of it.
/// </para>
/// </summary>
public sealed class AppIconService
{
    /// <summary>The folder under Config that holds imported icons. It is also the list of them.</summary>
    public const string IconsFolderName = "Icons";

    /// <summary>
    /// Anything larger is refused on import. A window icon is a few KB; a file past this is a
    /// mistake, and it would be copied into the user's profile and kept there.
    /// </summary>
    private const long MaxCustomIconBytes = 2 * 1024 * 1024;

    private readonly IUiSettingsService _settings;
    private readonly IYamlStorageService _storage;

    /// <summary>
    /// Resolved icons are kept rather than rebuilt on every apply. <c>Properties.Resources</c> hands
    /// back a new <see cref="Icon"/> on each get and <see cref="Form.Icon"/> does not take ownership
    /// of what it is given, so resolving per call would leak one icon per swap.
    /// </summary>
    private readonly Dictionary<string, Icon> _cache = [];
    private readonly object _gate = new();

    private Form? _window;

    public AppIconService(IUiSettingsService settings, IYamlStorageService storage)
    {
        _settings = settings;
        _storage = storage;
    }

    /// <summary>Where imported icons live. Beside ui_settings.yaml, so the two travel together.</summary>
    public string IconsDirectory => Path.Combine(_storage.StorageDirectory, IconsFolderName);

    /// <summary>The window this service dresses. Called once, by <see cref="MainWindow"/>.</summary>
    public void Attach(Form window) => _window = window;

    /// <summary>
    /// The file names of every imported icon, alphabetically. A missing folder is the normal
    /// first-run case and comes back empty rather than throwing.
    /// </summary>
    public IReadOnlyList<string> ListImported()
    {
        try
        {
            if (!Directory.Exists(IconsDirectory)) return [];

            return Directory.EnumerateFiles(IconsDirectory, "*.ico")
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The icon <paramref name="settings"/> asks for, falling back to the default when an imported
    /// file has been deleted or stopped being readable. A window wearing the shipped icon is a
    /// better answer than one wearing none.
    /// </summary>
    public Icon Resolve(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Resolve(settings.AppIcon);
    }

    /// <summary>
    /// What a choice resolves to — including the fallback, so a preview never shows artwork the
    /// window would not actually wear.
    /// </summary>
    public Icon Resolve(string? choice)
    {
        var id = AppIconOptions.Normalize(choice);

        if (!AppIconOptions.IsBuiltIn(id) && TryLoadImported(id, out var imported))
        {
            return imported;
        }

        return id == AppIconOptions.Classic
            ? Cached(AppIconOptions.Classic, static () => Properties.Resources.classic_icon)
            : Cached(AppIconOptions.Default, static () => Properties.Resources.toolbox_icon);
    }

    /// <summary>Reads the saved choice and puts it on the window.</summary>
    public async Task ApplyAsync()
    {
        if (_window is null) return;

        var settings = await _settings.GetAsync().ConfigureAwait(false);
        Apply(Resolve(settings));
    }

    /// <summary>
    /// Copies <paramref name="sourcePath"/> into the Icons folder and hands back the name it was
    /// stored under, or an <c>Error</c> saying why it could not. Nothing changes on failure.
    /// <para>
    /// The file keeps its own name, deduplicated if one is already taken — with a list rather than a
    /// single slot, the name is how anybody tells two imported icons apart.
    /// </para>
    /// </summary>
    public async Task<(string? Name, string? Error)> ImportAsync(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return (null, "No file was chosen.");

        try
        {
            var file = new FileInfo(sourcePath);
            if (!file.Exists) return (null, "That file no longer exists.");
            if (file.Length > MaxCustomIconBytes)
            {
                return (null, $"That file is {file.Length / 1024f / 1024f:0.#} MB. Icons need to be under 2 MB.");
            }

            // Parsed before it is copied, so a .png someone renamed to .ico is refused here rather
            // than becoming a window with no icon later.
            using (var probe = new Icon(sourcePath)) { }

            Directory.CreateDirectory(IconsDirectory);
            var name = AvailableName(file.Name);
            var destination = Path.Combine(IconsDirectory, name);

            await Task.Run(() => File.Copy(sourcePath, destination, overwrite: false)).ConfigureAwait(false);
            return (name, null);
        }
        catch (ArgumentException)
        {
            // What Icon's constructor throws for a file that is not an icon at all.
            return (null, "That does not look like a Windows .ico file.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"Could not read that file: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes an imported icon, returning null or why it could not. Built-in ids are ignored —
    /// there is no file behind them to delete.
    /// </summary>
    public async Task<string?> RemoveAsync(string name)
    {
        var id = AppIconOptions.Normalize(name);
        if (AppIconOptions.IsBuiltIn(id)) return null;

        try
        {
            var path = Path.Combine(IconsDirectory, id);
            if (File.Exists(path)) await Task.Run(() => File.Delete(path)).ConfigureAwait(false);

            Forget(id);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not delete {id}: {ex.Message}";
        }
    }

    /// <summary>
    /// A PNG of <paramref name="icon"/> as a data URI. The Settings page is a web view and cannot
    /// render an <see cref="Icon"/>, and writing previews out as files to serve would leave litter.
    /// </summary>
    public static string PreviewDataUri(Icon icon, int size = 64)
    {
        ArgumentNullException.ThrowIfNull(icon);

        using var sized = new Icon(icon, size, size);
        using var bitmap = sized.ToBitmap();
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray());
    }

    /// <summary>
    /// <paramref name="preferred"/> if nothing holds that name yet, otherwise the same name with a
    /// counter — <c>acme (2).ico</c>. Importing the same file twice is a thing people do, and
    /// silently overwriting the first one loses whichever was still in use.
    /// </summary>
    private string AvailableName(string preferred)
    {
        var name = Path.GetFileName(preferred);
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "icon.ico";

        if (!File.Exists(Path.Combine(IconsDirectory, name))) return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{stem} ({n}){extension}";
            if (!File.Exists(Path.Combine(IconsDirectory, candidate))) return candidate;
        }

        // A thousand copies of one name is not a case worth a better answer than a unique one.
        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }

    private bool TryLoadImported(string name, out Icon icon)
    {
        icon = null!;

        try
        {
            var path = Path.Combine(IconsDirectory, name);
            if (!File.Exists(path)) return false;

            // Keyed on the write time as well as the name, so replacing a file in place shows the
            // new artwork instead of the cached copy.
            var key = $"file|{name}|{File.GetLastWriteTimeUtc(path):O}";
            icon = Cached(key, () => new Icon(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private Icon Cached(string key, Func<Icon> create)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var existing)) return existing;

            var icon = create();
            _cache[key] = icon;
            return icon;
        }
    }

    private void Forget(string name)
    {
        lock (_gate)
        {
            // The icons themselves are not disposed: the window may still be wearing one, and
            // WinForms keeps the instance it was handed. They are a few KB each and this happens
            // only when somebody removes one.
            var prefix = $"file|{name}|";
            foreach (var key in _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _cache.Remove(key);
            }
        }
    }

    /// <summary>
    /// <see cref="Form.Icon"/> is a UI-thread property and the Settings page calls in from wherever
    /// the web view happens to be, so the assignment is marshalled.
    /// </summary>
    private void Apply(Icon icon)
    {
        var window = _window;
        if (window is null || window.IsDisposed) return;

        try
        {
            if (window.InvokeRequired) window.BeginInvoke(() => SetIcon(window, icon));
            else SetIcon(window, icon);
        }
        catch (ObjectDisposedException)
        {
            // The window went away between the check and the call.
        }
        catch (InvalidOperationException)
        {
            // No handle yet. MainWindow_Load applies it again once there is one.
        }
    }

    private static void SetIcon(Form window, Icon icon)
    {
        if (!window.IsDisposed) window.Icon = icon;
    }
}
