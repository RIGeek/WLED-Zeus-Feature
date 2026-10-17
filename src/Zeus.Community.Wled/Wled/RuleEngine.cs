// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Community.Wled.Wled;

internal sealed record RuleStatus(string Id, bool Active, IReadOnlyList<int> Segments, string? Error, double? Shown);

/// <summary>
/// Decides, every frame, which rule owns which segment and what it should look like, and hands
/// that to the controllers. A rule higher in the list wins a segment over a lower one while both
/// are active; when no rule wants a segment any more it gets its own look back.
/// </summary>
internal sealed class RuleEngine
{
    private sealed class MeterState
    {
        public double? Shown, Peak;
        public DateTime PeakAt, Last;
    }

    private sealed record Identify(string Controller, int Segment, DateTime UntilUtc);

    private static readonly Look IdentifyLook = new("FFFFFF", "Blink", 255, 200, 128);
    private static readonly TimeSpan PeakHoldFor = TimeSpan.FromSeconds(1.5);

    private readonly SignalHub _hub;
    private readonly object _gate = new();
    private readonly Dictionary<string, MeterState> _meters = new(StringComparer.Ordinal);
    private IReadOnlyList<Rule> _rules = [];
    private IReadOnlyDictionary<string, WledClient> _clients = new Dictionary<string, WledClient>();
    private Dictionary<string, RuleStatus> _status = new(StringComparer.Ordinal);
    private Identify? _identify;

    public RuleEngine(SignalHub hub) => _hub = hub;

    public void Configure(IReadOnlyList<Rule> rules, IReadOnlyDictionary<string, WledClient> clients)
    {
        lock (_gate)
        {
            _rules = rules;
            _clients = clients;
            foreach (var id in _meters.Keys.Except(rules.Select(r => r.Id)).ToList()) _meters.Remove(id);
        }
    }

    public bool UsesAmp()
    {
        lock (_gate) return _rules.Any(r => r.Enabled && Signals.Get(r.Signal)?.Source == SignalSource.Amp);
    }

    public IReadOnlyDictionary<string, RuleStatus> Status
    {
        get { lock (_gate) return _status; }
    }

    public void StartIdentify(string controller, int segment, TimeSpan duration)
    {
        lock (_gate) _identify = new Identify(controller, segment, DateTime.UtcNow + duration);
    }

    /// <summary>One frame: work out owners and looks, tell the controllers.</summary>
    public void Tick(DateTime now)
    {
        lock (_gate)
        {
            var status = new Dictionary<string, RuleStatus>(StringComparer.Ordinal);
            var owners = _clients.Keys.ToDictionary(k => k, _ => new Dictionary<int, (Rule? Rule, string Props, string? Leds)>(), StringComparer.Ordinal);

            if (_identify is { } ident)
            {
                if (ident.UntilUtc <= now) _identify = null;
                else if (_clients.TryGetValue(ident.Controller, out var ic) && ic.Connected)
                {
                    var fx = ic.EffectId(IdentifyLook.Effect!) ?? 0;
                    foreach (var seg in SegmentsOf(ic, ident.Segment))
                        owners[ident.Controller][seg] = (null, Render.Look(IdentifyLook, fx), null);
                }
            }

            foreach (var rule in _rules)
            {
                if (!rule.Enabled)
                {
                    status[rule.Id] = new RuleStatus(rule.Id, false, [], null, null);
                    continue;
                }
                var signal = Signals.Get(rule.Signal)!;
                var value = _hub.Get(rule.Signal);
                double? shown = signal.Kind == SignalKind.Level ? Smooth(rule, value, now) : null;
                var active = IsActive(rule, signal, value);

                if (!_clients.TryGetValue(rule.Controller, out var client))
                {
                    status[rule.Id] = new RuleStatus(rule.Id, active, [], "That WLED controller is no longer in settings", shown);
                    continue;
                }
                if (!client.Connected)
                {
                    status[rule.Id] = new RuleStatus(rule.Id, active, [], client.Config.Enabled ? "WLED controller not connected" : "WLED controller is switched off in settings", shown);
                    continue;
                }
                var segments = SegmentsOf(client, rule.Segment);
                if (segments.Count == 0)
                {
                    status[rule.Id] = new RuleStatus(rule.Id, active, [], rule.Segment < 0 ? "The controller has no segments" : $"Segment {rule.Segment} does not exist on {client.Config.Name}", shown);
                    continue;
                }
                if (!active)
                {
                    status[rule.Id] = new RuleStatus(rule.Id, false, [], null, shown);
                    continue;
                }

                var mine = new List<int>();
                string? error = null;
                foreach (var seg in segments)
                {
                    if (owners[rule.Controller].ContainsKey(seg)) continue;   // a rule above has it
                    var (props, leds, err) = Draw(rule, client, seg, value, shown);
                    if (props is null) { error = err; continue; }
                    owners[rule.Controller][seg] = (rule, props, leds);
                    mine.Add(seg);
                }
                status[rule.Id] = new RuleStatus(rule.Id, true, mine, error, shown);
            }

            foreach (var (id, client) in _clients)
            {
                var wanted = owners[id];
                foreach (var (seg, look) in wanted) client.Desire(seg, look.Props, look.Leds);
                foreach (var seg in client.Owned)
                    if (!wanted.ContainsKey(seg)) client.Release(seg);
            }
            _status = status;
        }
    }

    private static List<int> SegmentsOf(WledClient client, int segment)
    {
        var segs = client.Segments;
        return segment < 0 ? segs.Select(s => s.Id).ToList() : segs.Any(s => s.Id == segment) ? [segment] : [];
    }

    private bool IsActive(Rule rule, SignalInfo signal, SignalValue value)
    {
        switch (signal.Kind)
        {
            case SignalKind.Flag:
                return value is { Valid: true, Number: > 0 };
            case SignalKind.Text:
                return value.Valid;
            default:
                if (!value.Valid) return false;
                return rule.Gate switch
                {
                    "tx" => _hub.IsOn(Signals.Tx),
                    "rx" => _hub.IsOn(Signals.Rx),
                    _ => true,
                };
        }
    }

    /// <summary>Fast attack, eased release, and a peak that holds then falls.</summary>
    private double? Smooth(Rule rule, SignalValue value, DateTime now)
    {
        if (!_meters.TryGetValue(rule.Id, out var st)) _meters[rule.Id] = st = new MeterState { Last = now };
        var dt = Math.Clamp((now - st.Last).TotalSeconds, 0, 1);
        st.Last = now;
        if (!value.Valid || value.Number is not { } target)
        {
            st.Shown = null;
            st.Peak = null;
            return null;
        }
        var m = rule.Meter!;
        var range = m.Max - m.Min;
        if (st.Shown is not { } shown || target >= shown || m.Smoothing == 0)
        {
            st.Shown = target;
        }
        else
        {
            var tau = m.Smoothing / 100.0 * 0.8;               // up to 0.8 s release
            st.Shown = shown + (target - shown) * (1 - Math.Exp(-dt / tau));
            if (Math.Abs(st.Shown.Value - target) < range * 0.002) st.Shown = target;
        }
        if (st.Peak is not { } peak || st.Shown >= peak)
        {
            st.Peak = st.Shown;
            st.PeakAt = now;
        }
        else if (now - st.PeakAt > PeakHoldFor)
        {
            st.Peak = Math.Max(st.Shown!.Value, peak - range * 1.5 * dt);   // falls the full scale in ~0.7 s
        }
        return st.Shown;
    }

    private (string? Props, string? Leds, string? Error) Draw(Rule rule, WledClient client, int seg, SignalValue value, double? shown)
    {
        switch (rule.Style)
        {
            case Styles.Look:
            {
                var look = rule.Look!;
                var fx = 0;
                if (look.Effect is { } name)
                {
                    if (client.EffectId(name) is not { } id) return (null, null, $"{client.Config.Name} has no effect called “{name}”");
                    fx = id;
                }
                return (Render.Look(look, fx), null, null);
            }
            case Styles.Band:
            {
                var band = value.Text ?? "other";
                var colors = rule.BandColors!;
                var color = colors.TryGetValue(band, out var c) ? c : colors.GetValueOrDefault("other", "404040");
                var look = rule.Look! with { Color = color };
                var fx = 0;
                if (look.Effect is { } name)
                {
                    if (client.EffectId(name) is not { } id) return (null, null, $"{client.Config.Name} has no effect called “{name}”");
                    fx = id;
                }
                return (Render.Look(look, fx), null, null);
            }
            case Styles.Percent:
            {
                if (client.EffectId("Percent") is not { } pfx)
                    return (null, null, $"{client.Config.Name} has no Percent effect; use the LED-by-LED style");
                return (Render.Percent(rule.Meter!, shown ?? rule.Meter!.Min, pfx), null, null);
            }
            case Styles.Color:
                return (Render.ZoneFill(rule.Meter!, shown ?? rule.Meter!.Min), null, null);
            case Styles.Leds:
            {
                var info = client.Segments.FirstOrDefault(s => s.Id == seg);
                if (info is null) return (null, null, $"Segment {seg} does not exist");
                if (info.VirtualLength > 1024) return (null, null, "Segment too long for an LED-by-LED bar (over 1024); use Percent");
                var peak = _meters.TryGetValue(rule.Id, out var st) ? st.Peak : null;
                var m = rule.Meter!;
                return (Render.LedSetup(m), Render.Leds(m, shown ?? m.Min, peak, info.VirtualLength), null);
            }
            default:
                return (null, null, "Unknown style");
        }
    }
}
