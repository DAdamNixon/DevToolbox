using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models;

namespace DevToolbox.Services.Services;

/// <inheritdoc cref="IUiSettingsService"/>
public sealed class UiSettingsService : IUiSettingsService
{
    /// <summary>Storage key; <see cref="IYamlStorageService"/> appends the extension.</summary>
    private const string ConfigKey = "ui_settings";

    private readonly IYamlStorageService _yamlStorage;

    // One reader at a time, so the first two callers on startup cannot both miss
    // the cache and race to read the same file.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UiSettings? _cached;

    public string? LoadError { get; private set; }

    public UiSettingsService(IYamlStorageService yamlStorage)
    {
        _yamlStorage = yamlStorage;
    }

    public async Task<UiSettings> GetAsync()
    {
        if (_cached is not null) return _cached;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;

            UiSettings settings;
            try
            {
                // A missing file is the normal first-run case and comes back null.
                settings = await _yamlStorage.LoadAsync<UiSettings>(ConfigKey).ConfigureAwait(false)
                           ?? new UiSettings();
                LoadError = null;
            }
            catch (InvalidOperationException ex)
            {
                // Malformed YAML. Fall back to defaults for this session but leave
                // the file untouched — overwriting it would destroy whatever the
                // user was in the middle of hand-editing.
                LoadError = ex.Message;
                settings = new UiSettings();
            }

            settings.Theme = ThemeOptions.Normalize(settings.Theme);
            _cached = settings;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Theme = ThemeOptions.Normalize(settings.Theme);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _yamlStorage.SaveAsync(ConfigKey, settings).ConfigureAwait(false);
            _cached = settings;
            LoadError = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
