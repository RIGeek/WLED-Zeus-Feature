// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Plugins.Contracts;

namespace Zeus.Community.Wled;

internal interface ISettingsStore
{
    ValueTask<WledSettings> LoadAsync(CancellationToken ct);
    ValueTask SaveAsync(WledSettings settings, CancellationToken ct);
}

/// <summary>Transmit state and frequency from Zeus itself (no TCI needed for these).</summary>
internal interface IRadioFeed : IDisposable
{
    bool Available { get; }
    /// <summary>Starts pushing TX and band into the hub.</summary>
    void Attach(SignalHub hub);
}

/// <summary>
/// The ONLY place that touches Zeus-provided services beyond logging: the plugin-scoped settings
/// store, and IRadioStateReader (ReadRadioState) for MOX and frequency. The feature never calls
/// IRadioController.
/// </summary>
internal static class HostBridge
{
    public static ISettingsStore CreateSettingsStore(IPluginContext context) => new PluginSettingsStore(context.Settings);

    public static IRadioFeed CreateRadioFeed(IPluginContext context) =>
        context.Radio is { } radio ? new ZeusRadioFeed(radio) : new NoRadioFeed();
}

internal sealed class PluginSettingsStore(IPluginSettings settings) : ISettingsStore
{
    private const string ControllersKey = "controllers";
    private const string SourcesKey = "sources";
    private const string RulesKey = "rules";

    public async ValueTask<WledSettings> LoadAsync(CancellationToken ct)
    {
        try
        {
            var controllers = Validate.Deserialize<List<Controller>>(await settings.GetAsync<string>(ControllersKey, ct).ConfigureAwait(false));
            var sources = Validate.Deserialize<SourceSettings>(await settings.GetAsync<string>(SourcesKey, ct).ConfigureAwait(false));
            var rules = Validate.Deserialize<List<Rule>>(await settings.GetAsync<string>(RulesKey, ct).ConfigureAwait(false));
            return new WledSettings(controllers ?? [], sources ?? SourceSettings.Default, rules ?? []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WledSettings.Empty;   // unreadable settings must not stop the feature loading
        }
    }

    public async ValueTask SaveAsync(WledSettings s, CancellationToken ct)
    {
        await settings.SetAsync(ControllersKey, Validate.Serialize(s.Controllers), ct).ConfigureAwait(false);
        await settings.SetAsync(SourcesKey, Validate.Serialize(s.Sources), ct).ConfigureAwait(false);
        await settings.SetAsync(RulesKey, Validate.Serialize(s.Rules), ct).ConfigureAwait(false);
    }
}

internal sealed class MemorySettingsStore : ISettingsStore
{
    private WledSettings _current = WledSettings.Empty;
    public int Saves { get; private set; }

    public ValueTask<WledSettings> LoadAsync(CancellationToken ct) => new(_current);

    public ValueTask SaveAsync(WledSettings settings, CancellationToken ct)
    {
        _current = settings;
        Saves++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ZeusRadioFeed(IRadioStateReader reader) : IRadioFeed
{
    private SignalHub? _hub;
    private Timer? _timer;

    public bool Available => true;

    public void Attach(SignalHub hub)
    {
        _hub = hub;
        reader.MoxChanged += OnMox;
        reader.FrequencyChanged += OnFrequency;
        // Events carry the changes; a slow re-read covers a host that misses one.
        _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void Refresh()
    {
        try { OnMox(reader.Mox); } catch (Exception) { _hub?.Invalidate(Signals.Tx); }
        try { OnFrequency(reader.FrequencyHz); } catch (Exception) { _hub?.Invalidate(Signals.Band); }
    }

    private void OnMox(bool on) => _hub?.SetFlag(Signals.Tx, on);

    private void OnFrequency(long hz)
    {
        if (_hub is null) return;
        var band = Signals.BandOf(hz);
        if (band is null)
        {
            // Outside the table: fall back to Zeus's own band name, if it is one we colour.
            string? named = null;
            try { named = reader.Band?.Trim().ToLowerInvariant(); } catch (Exception) { }
            band = Signals.BandDefaults.Any(b => b.Band == named) ? named : "other";
        }
        _hub.SetText(Signals.Band, band);
    }

    public void Dispose()
    {
        reader.MoxChanged -= OnMox;
        reader.FrequencyChanged -= OnFrequency;
        _timer?.Dispose();
    }
}

internal sealed class NoRadioFeed : IRadioFeed
{
    public bool Available => false;
    public void Attach(SignalHub hub) { }
    public void Dispose() { }
}

/// <summary>For tests and the development host.</summary>
internal sealed class FakeRadioFeed : IRadioFeed
{
    private SignalHub? _hub;
    public bool Available => true;
    public void Attach(SignalHub hub) => _hub = hub;
    public void SetTx(bool on) => _hub?.SetFlag(Signals.Tx, on);
    public void SetFrequency(long hz) => _hub?.SetText(Signals.Band, Signals.BandOf(hz) ?? "other");
    public void Dispose() { }
}
