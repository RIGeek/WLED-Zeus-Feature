// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.Logging;
using Zeus.Community.Wled.Sources;
using Zeus.Community.Wled.Wled;

namespace Zeus.Community.Wled;

// ---- JSON shapes returned to the panel (serialized camelCase) ----------------

internal sealed record ApiStatus(
    ApiRadio Radio, ApiSources Sources, IReadOnlyList<ApiSignal> Signals,
    IReadOnlyList<ControllerStatus> Controllers, IReadOnlyList<ApiRule> Rules, string SampledUtc);

internal sealed record ApiRadio(bool Available, bool? Tx, string? Band);

internal sealed record ApiSources(ApiLink Tci, ApiLink Amp);

internal sealed record ApiLink(bool Enabled, string State, string? Error, string? Endpoint, double? FullScale);

internal sealed record ApiSignal(string Id, string Name, string Kind, string? Unit, bool Valid, double? Value, string? Text, bool Testing, int Digits);

internal sealed record ApiRule(Rule Rule, bool Active, IReadOnlyList<int> Segments, string? Error, double? Shown);

internal sealed record ApiCatalog(IReadOnlyList<ApiSignalInfo> Signals, IReadOnlyList<ApiBand> Bands, int MaxControllers, int MaxRules);

internal sealed record ApiSignalInfo(
    string Id, string Name, string Kind, string Source, string? Unit, double Min, double Max, double? Warn, double? Bad,
    bool LowIsBad, string Gate, string[] Styles, int Digits);

internal sealed record ApiBand(string Band, string Color);

/// <summary>
/// Everything the feature does, with no dependency on the Zeus SDK so that it can be exercised
/// directly by tests: the signal sources, the controllers, the rules, and the frame loop.
/// </summary>
internal sealed class WledService : IAsyncDisposable
{
    public static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(25);

    private readonly ILogger _log;
    private readonly ISettingsStore _store;
    private readonly IRadioFeed _radio;
    private readonly SemaphoreSlim _settingsLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private WledSettings _settings = WledSettings.Empty;
    private Dictionary<string, WledClient> _clients = new(StringComparer.Ordinal);
    private Task? _loop;

    public SignalHub Hub { get; } = new();
    public RuleEngine Engine { get; }
    public TciSource Tci { get; }
    public AmpSource Amp { get; }

    public WledService(ILogger log, ISettingsStore store, IRadioFeed radio)
    {
        _log = log;
        _store = store;
        _radio = radio;
        Engine = new RuleEngine(Hub);
        Tci = new TciSource(log, Hub) { SupplyTx = !radio.Available };
        Amp = new AmpSource(log, Hub) { Wanted = () => Engine.UsesAmp() };
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _settings = await _store.LoadAsync(ct).ConfigureAwait(false);
        _radio.Attach(Hub);
        await ApplyControllersAsync(_settings.Controllers).ConfigureAwait(false);
        await ApplySourcesAsync(_settings.Sources).ConfigureAwait(false);
        Engine.Configure(_settings.Rules, _clients);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public WledSettings GetSettings() => _settings;

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Frame);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { Engine.Tick(DateTime.UtcNow); }
                catch (Exception ex) { _log.LogWarning(ex, "WLED frame failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ settings

    public async Task<(CommandResult Result, IReadOnlyList<Controller>? Controllers)> SetControllersAsync(IReadOnlyList<ControllerInput>? input, CancellationToken ct)
    {
        var (controllers, error) = Validate.Controllers(input);
        if (controllers is null) return (CommandResult.Invalid(error!), null);
        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Rules on a removed controller go with it.
            var ids = controllers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var rules = _settings.Rules.Where(r => ids.Contains(r.Controller)).ToList();
            var next = _settings with { Controllers = controllers, Rules = rules };
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            _settings = next;
            await ApplyControllersAsync(controllers).ConfigureAwait(false);
            Engine.Configure(rules, _clients);
            return (CommandResult.Success("Controllers saved"), controllers);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task<CommandResult> SetSourcesAsync(SourceInput? input, CancellationToken ct)
    {
        var (sources, error) = Validate.Sources(input, _settings.Sources);
        if (sources is null) return CommandResult.Invalid(error!);
        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = _settings with { Sources = sources };
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            _settings = next;
            await ApplySourcesAsync(sources).ConfigureAwait(false);
            return CommandResult.Success("Sources saved");
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    public async Task<(CommandResult Result, IReadOnlyList<Rule>? Rules)> SetRulesAsync(IReadOnlyList<RuleInput>? input, CancellationToken ct)
    {
        var (rules, error) = Validate.Rules(input, _settings.Controllers);
        if (rules is null) return (CommandResult.Invalid(error!), null);
        await _settingsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = _settings with { Rules = rules };
            await _store.SaveAsync(next, ct).ConfigureAwait(false);
            _settings = next;
            Engine.Configure(rules, _clients);
            return (CommandResult.Success("Rules saved"), rules);
        }
        finally
        {
            _settingsLock.Release();
        }
    }

    private async Task ApplyControllersAsync(IReadOnlyList<Controller> controllers)
    {
        var next = new Dictionary<string, WledClient>(StringComparer.Ordinal);
        var retired = new List<WledClient>();
        foreach (var (id, client) in _clients)
        {
            var keep = controllers.FirstOrDefault(c => c.Id == id);
            if (keep is not null && keep == client.Config) next[id] = client;
            else retired.Add(client);
        }
        foreach (var c in controllers)
        {
            if (next.ContainsKey(c.Id)) continue;
            var client = new WledClient(_log, c);
            client.Start();
            next[c.Id] = client;
        }
        _clients = next;
        Engine.Configure(_settings.Rules.Where(r => next.ContainsKey(r.Controller)).ToList(), next);
        foreach (var old in retired)
        {
            await old.ReleaseAllAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await old.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ApplySourcesAsync(SourceSettings s)
    {
        await Tci.StartAsync(s.TciEnabled ? s.TciHost : null, s.TciPort, s.TciIntervalMs).ConfigureAwait(false);
        await ApplyAmpAsync(s).ConfigureAwait(false);
    }

    private Task ApplyAmpAsync(SourceSettings s)
    {
        if (!s.AmpEnabled) return Amp.StartAsync(null, "off");
        if (s.ZeusUrl is { } manual) return Amp.StartAsync(AmpSource.StatusUri(manual), "off");
        return Amp.StartAsync(DetectedAmpStatus, "waiting_for_zeus");
    }

    /// <summary>
    /// Where the amplifier feature's status route is on this Zeus, worked out from the requests the
    /// panel makes to this feature: they arrive on Zeus's API listener under its plugin prefix, so the
    /// amp's route is the same address and prefix with the other plugin id. Null until the panel has
    /// been opened once since Zeus started.
    /// </summary>
    public Uri? DetectedAmpStatus { get; private set; }

    /// <summary>Records the address a panel request came in on; restarts amp polling if it changed.</summary>
    public void NoteApiAddress(Uri amp)
    {
        if (amp == DetectedAmpStatus) return;
        DetectedAmpStatus = amp;
        var s = _settings.Sources;
        if (s.AmpEnabled && s.ZeusUrl is null && Amp.Target != amp)
            _ = Task.Run(async () =>
            {
                await _settingsLock.WaitAsync().ConfigureAwait(false);
                try { await ApplyAmpAsync(_settings.Sources).ConfigureAwait(false); }
                finally { _settingsLock.Release(); }
            });
    }

    // ------------------------------------------------------------------ actions

    public CommandResult Identify(string? controllerId, int? segment)
    {
        if (controllerId is null || !_clients.TryGetValue(controllerId, out var client)) return CommandResult.Invalid("Unknown controller");
        if (!client.Connected) return CommandResult.Blocked($"{client.Config.Name} is not connected");
        var seg = segment ?? -1;
        if (seg >= 0 && client.Segments.All(s => s.Id != seg)) return CommandResult.Invalid($"Segment {seg} does not exist");
        Engine.StartIdentify(controllerId, seg, TimeSpan.FromSeconds(3));
        return CommandResult.Success(seg < 0 ? $"{client.Config.Name}: flashing every segment" : $"{client.Config.Name}: flashing segment {seg}");
    }

    /// <summary>
    /// Shows a rule as if its signal were live (a flag on, a level at 70 %, the current band) for a
    /// few seconds, without the radio doing anything. An empty rule id ends any test.
    /// </summary>
    public CommandResult Test(string? ruleId, int? seconds)
    {
        if (string.IsNullOrEmpty(ruleId))
        {
            Hub.ClearOverrides();
            return CommandResult.Success("Test ended");
        }
        var rule = _settings.Rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule is null) return CommandResult.Invalid("Unknown rule");
        var signal = Signals.Get(rule.Signal)!;
        var span = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 5, 1, 15));
        switch (signal.Kind)
        {
            case SignalKind.Flag:
                Hub.Override(signal.Id, new SignalValue(1, null, true), span);
                break;
            case SignalKind.Text:
                var band = Hub.Get(Signals.Band).Text ?? "20m";
                Hub.Override(signal.Id, new SignalValue(null, band, true), span);
                break;
            default:
                var m = rule.Meter!;
                Hub.Override(signal.Id, new SignalValue(m.Min + (m.Max - m.Min) * 0.7, null, true), span);
                if (rule.Gate == "tx") Hub.Override(Signals.Tx, new SignalValue(1, null, true), span);
                if (rule.Gate == "rx") Hub.Override(Signals.Tx, new SignalValue(0, null, true), span);
                break;
        }
        return CommandResult.Success($"Testing for {span.TotalSeconds:0} s");
    }

    public IReadOnlyList<string>? Effects(string? controllerId) =>
        controllerId is not null && _clients.TryGetValue(controllerId, out var c) ? c.Effects : null;

    // ------------------------------------------------------------------ status

    public static ApiCatalog Catalog() => new(
        Signals.All.Select(s => new ApiSignalInfo(s.Id, s.Name, s.Kind.ToString().ToLowerInvariant(), s.Source.ToString().ToLowerInvariant(),
            s.Unit, s.Min, s.Max, s.Warn, s.Bad, s.LowIsBad, s.DefaultGate, Styles.For(s.Kind), s.Digits)).ToArray(),
        Signals.BandDefaults.Select(b => new ApiBand(b.Band, b.Color)).ToArray(),
        Validate.MaxControllers, Validate.MaxRules);

    public ApiStatus BuildStatus()
    {
        var tx = Hub.Get(Signals.Tx);
        var band = Hub.Get(Signals.Band);
        var (tciState, tciError, tciEndpoint) = Tci.Describe();
        var (ampState, ampError, ampFull) = Amp.Describe();
        var s = _settings.Sources;

        var signals = Signals.All.Select(info =>
        {
            var v = Hub.Get(info.Id);
            return new ApiSignal(info.Id, info.Name, info.Kind.ToString().ToLowerInvariant(), info.Unit,
                v.Valid, v.Number, v.Text, Hub.Overridden(info.Id), info.Digits);
        }).ToArray();

        var engine = Engine.Status;
        var rules = _settings.Rules.Select(r => engine.TryGetValue(r.Id, out var st)
            ? new ApiRule(r, st.Active, st.Segments, st.Error, st.Shown)
            : new ApiRule(r, false, [], null, null)).ToArray();

        return new ApiStatus(
            new ApiRadio(_radio.Available, tx.Valid ? tx.Number > 0 : null, band.Text),
            new ApiSources(
                new ApiLink(s.TciEnabled, tciState, tciError, tciEndpoint, null),
                new ApiLink(s.AmpEnabled, ampState, ampError, Amp.Target?.ToString(), ampFull)),
            signals,
            _clients.Values.Select(c => c.Describe()).OrderBy(c => _settings.Controllers.ToList().FindIndex(x => x.Id == c.Id)).ToArray(),
            rules,
            DateTime.UtcNow.ToString("O"));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { /* stopping */ }
        }
        // Give every light its own look back before going (Zeus allows 5 s for shutdown).
        await Task.WhenAll(_clients.Values.Select(c => c.ReleaseAllAsync(TimeSpan.FromSeconds(2)))).ConfigureAwait(false);
        foreach (var c in _clients.Values) await c.DisposeAsync().ConfigureAwait(false);
        await Tci.DisposeAsync().ConfigureAwait(false);
        await Amp.DisposeAsync().ConfigureAwait(false);
        _radio.Dispose();
        _cts.Dispose();
        _settingsLock.Dispose();
    }
}
