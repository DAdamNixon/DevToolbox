using DevToolbox.Services.Interfaces;
using DevToolbox.Services.Models.Hosts;

namespace DevToolbox.Services.Services.Hosts;

/// <inheritdoc cref="IHostsSettingsService"/>
public sealed class HostsSettingsService : IHostsSettingsService
{
    /// <summary>
    /// Storage key; <see cref="IYamlStorageService"/> appends the extension. Deliberately unlike the
    /// <c>hosts-configuration*</c> names an abandoned earlier prototype left in the config folder, so
    /// there is no chance of reading one of those by accident.
    /// </summary>
    private const string ConfigKey = "host_changer_settings";

    private readonly IYamlStorageService _yamlStorage;

    // One reader at a time, so two callers on startup cannot both miss the cache and race to read
    // the same file.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HostsSettings? _cached;

    public string? LoadError { get; private set; }

    public HostsSettingsService(IYamlStorageService yamlStorage)
    {
        _yamlStorage = yamlStorage;
    }

    public async Task<HostsSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null) return _cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;

            HostsSettings settings;

            try
            {
                var stored = await _yamlStorage.LoadAsync<HostsSettings>(ConfigKey).ConfigureAwait(false);

                if (stored is null)
                {
                    // Genuinely absent, so this is a first run. Seeding is worth doing so there is
                    // something to edit, and the starter contains nothing but the defaults plus a
                    // DNS flush — no hostname, address or group name of anybody's.
                    settings = HostsSettings.CreateStarter();
                    await _yamlStorage.SaveAsync(ConfigKey, settings).ConfigureAwait(false);
                }
                else
                {
                    settings = stored;
                }

                LoadError = null;
            }
            catch (InvalidOperationException ex)
            {
                // Malformed YAML. Fall back to defaults for this session and leave the file alone —
                // overwriting it would destroy whatever was being hand-edited.
                LoadError = ex.Message;
                settings = HostsSettings.CreateStarter();
            }

            _cached = settings;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(HostsSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
