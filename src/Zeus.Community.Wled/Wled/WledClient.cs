// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Zeus.Community.Wled.Wled;

/// <summary>A segment as last read from the controller.</summary>
internal sealed record SegmentInfo(int Id, string? Name, int Length, int VirtualLength, bool On);

internal sealed record ControllerStatus(
    string Id, string Name, string Host, bool Enabled, string State, string? Error,
    string? DeviceName, string? Version, int LedCount, bool LightOn,
    IReadOnlyList<SegmentInfo> Segments, int EffectCount, IReadOnlyList<int> Driven);

/// <summary>
/// One WLED controller over its JSON HTTP API (/json/si to read, /json/state to write).
///
/// The rule engine says what each segment should look like (<see cref="Desire"/>) or that it is
/// done with one (<see cref="Release"/>); this class sends only the differences, one request at
/// a time and no faster than the controller's rate limit. Requests never queue up: if the radio
/// changes faster than WLED can keep up, intermediate frames are skipped and the latest one wins.
///
/// When the engine first takes a segment, the segment's own look (colours, effect, sliders,
/// brightness, on/off) is kept, and put back when the engine releases it.
/// </summary>
internal sealed class WledClient : IAsyncDisposable
{
    private static readonly string[] RestoreKeys = ["on", "bri", "col", "fx", "sx", "ix", "pal", "frz", "o1"];

    private sealed class Want
    {
        public string? Props;      // JSON members (no braces), e.g. "on":true,"fx":0
        public string? Leds;       // the "i" array, or null
        public string? Restore;    // the segment's own look, captured when the engine took it
        public bool Releasing;
    }

    private readonly ILogger _log;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<int, Want> _want = [];
    private readonly Dictionary<int, (string? Props, string? Leds)> _sent = [];
    private readonly Dictionary<int, string> _baseline = [];
    private readonly Dictionary<int, DateTime> _touched = [];
    private Task? _poller, _sender;

    private string _state = "connecting";
    private string? _error, _deviceName, _version;
    private int _ledCount;
    private bool _lightOn = true, _knowState;
    private IReadOnlyList<SegmentInfo> _segments = [];
    private string[] _effects = [];
    private bool? _topWant, _topSent;
    private bool _turnedOn;

    public Controller Config { get; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Requests sent so far (for tests and diagnostics).</summary>
    public int Requests;

    public WledClient(ILogger log, Controller config, HttpMessageHandler? handler = null)
    {
        _log = log;
        Config = config;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(4);
        _http.BaseAddress = new Uri(config.Host + "/");
        if (!config.Enabled) _state = "disabled";
    }

    public void Start()
    {
        if (!Config.Enabled) return;
        _poller = Task.Run(() => PollLoopAsync(_cts.Token));
        _sender = Task.Run(() => SendLoopAsync(_cts.Token));
    }

    // ------------------------------------------------------------------ what the engine sees

    public bool Connected { get { lock (_gate) return _state == "connected"; } }

    public IReadOnlyList<SegmentInfo> Segments { get { lock (_gate) return _segments; } }

    public int? EffectId(string name)
    {
        lock (_gate)
        {
            for (var i = 0; i < _effects.Length; i++)
                if (string.Equals(_effects[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return null;
        }
    }

    public IReadOnlyList<string> Effects
    {
        get
        {
            lock (_gate)
                return _effects.Where(e => e is not ("RSVD" or "-" or "")).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public ControllerStatus Describe()
    {
        lock (_gate)
        {
            return new ControllerStatus(Config.Id, Config.Name, Config.Host, Config.Enabled, _state, _error,
                _deviceName, _version, _ledCount, _lightOn, _segments, _effects.Length,
                _want.Where(w => !w.Value.Releasing).Select(w => w.Key).Order().ToArray());
        }
    }

    /// <summary>The engine wants this segment to look like this. Cheap to call every frame.</summary>
    public void Desire(int segment, string props, string? leds)
    {
        lock (_gate)
        {
            if (!_want.TryGetValue(segment, out var w) || w.Releasing)
            {
                var restore = w?.Releasing == true ? w.Restore : _baseline.GetValueOrDefault(segment);
                w = new Want { Restore = restore };
                _want[segment] = w;
            }
            if (w.Props == props && w.Leds == leds) return;
            w.Props = props;
            w.Leds = leds;
            UpdateTopLocked();
        }
        Wake();
    }

    /// <summary>The engine is done with this segment: put its own look back.</summary>
    public void Release(int segment)
    {
        lock (_gate)
        {
            if (!_want.TryGetValue(segment, out var w) || w.Releasing) return;
            w.Releasing = true;
            w.Leds = null;
            // Unknown original look: at least unfreeze it so its own effect runs again.
            w.Props = w.Restore ?? "\"frz\":false";
            UpdateTopLocked();
        }
        Wake();
    }

    public IReadOnlyCollection<int> Owned
    {
        get { lock (_gate) return _want.Where(w => !w.Value.Releasing).Select(w => w.Key).ToArray(); }
    }

    private void UpdateTopLocked()
    {
        var holding = _want.Values.Any(w => !w.Releasing);
        if (holding && Config.TurnOn && _knowState && !_lightOn && !_turnedOn)
        {
            _turnedOn = true;
            _topWant = true;
        }
        else if (!holding && _turnedOn)
        {
            _turnedOn = false;
            _topWant = false;
        }
    }

    private void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { /* already awake */ }
    }

    // ------------------------------------------------------------------ reading

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool needEffects;
                lock (_gate) needEffects = _effects.Length == 0;
                if (needEffects)
                {
                    var eff = await GetJsonAsync("json/eff", ct).ConfigureAwait(false);
                    var names = eff.ValueKind == JsonValueKind.Array
                        ? eff.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "").ToArray()
                        : [];
                    lock (_gate) _effects = names;
                }
                var started = DateTime.UtcNow;
                var si = await GetJsonAsync("json/si", ct).ConfigureAwait(false);
                ApplyState(si, started);
                if (failures > 0) _log.LogInformation("WLED {Host} is back", Config.Host);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var message = ex switch
                {
                    JsonException => $"{Config.Host} did not answer like WLED",
                    HttpRequestException { StatusCode: { } code } => $"{Config.Host} answered HTTP {(int)code} (is it WLED?)",
                    _ => $"Cannot reach {Config.Host}",
                };
                lock (_gate)
                {
                    _state = "error";
                    _error = message;
                    _sent.Clear();          // whatever it shows after it comes back, send it all again
                    _topSent = null;
                    _effects = failures > 2 ? [] : _effects;
                }
                if (failures++ == 0) _log.LogInformation("WLED {Host}: {Message}", Config.Host, message);
            }
            var wait = failures == 0 ? PollInterval : TimeSpan.FromSeconds(Math.Min(15, 2 * failures));
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    /// <summary>Takes in a /json/si answer ({ state, info }).</summary>
    internal void ApplyState(JsonElement si, DateTime pollStartedUtc)
    {
        if (!si.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
            throw new JsonException("no state");
        var segs = new List<SegmentInfo>();
        var looks = new Dictionary<int, string>();
        if (state.TryGetProperty("seg", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
                var start = Int(s, "start") ?? 0;
                var stop = Int(s, "stop") ?? start;
                var len = Int(s, "len") ?? (stop - start);
                if (len <= 0) continue;
                var grp = Math.Max(1, Int(s, "grp") ?? 1);
                var spc = Math.Max(0, Int(s, "spc") ?? 0);
                var vlen = (len + grp + spc - 1) / (grp + spc);
                if (s.TryGetProperty("mi", out var mi) && mi.ValueKind == JsonValueKind.True) vlen = (vlen + 1) / 2;
                var name = s.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var on = !s.TryGetProperty("on", out var o) || o.ValueKind != JsonValueKind.False;
                segs.Add(new SegmentInfo(id, name, len, Math.Max(1, vlen), on));
                looks[id] = LookOf(s);
            }
        }

        string? device = null, version = null;
        var leds = 0;
        if (si.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            device = info.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null;
            version = info.TryGetProperty("ver", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (info.TryGetProperty("leds", out var l) && l.ValueKind == JsonValueKind.Object) leds = Int(l, "count") ?? 0;
        }
        var lightOn = !state.TryGetProperty("on", out var top) || top.ValueKind != JsonValueKind.False;

        lock (_gate)
        {
            _state = "connected";
            _error = null;
            _deviceName = device;
            _version = version;
            _ledCount = leds;
            _segments = segs;
            _knowState = true;
            if (!_turnedOn) _lightOn = lightOn;
            // A segment's own look is only learned while nothing here has touched it lately,
            // so a restore never puts back a frame of our own.
            foreach (var (id, look) in looks)
            {
                var quiet = !_touched.TryGetValue(id, out var t) || pollStartedUtc - t > TimeSpan.FromMilliseconds(600);
                if (!_want.ContainsKey(id) && quiet) _baseline[id] = look;
            }
            UpdateTopLocked();
        }
        Wake();
    }

    private static string LookOf(JsonElement seg)
    {
        var sb = new StringBuilder();
        foreach (var key in RestoreKeys)
        {
            if (!seg.TryGetProperty(key, out var v)) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append('"').Append(key).Append("\":").Append(v.GetRawText());
        }
        return sb.ToString();
    }

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    // ------------------------------------------------------------------ writing

    private async Task SendLoopAsync(CancellationToken ct)
    {
        var minGap = TimeSpan.FromMilliseconds(1000.0 / Config.MaxRate);
        var last = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var gap = minGap - (DateTime.UtcNow - last);
            if (gap > TimeSpan.Zero)
            {
                try { await Task.Delay(gap, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
            try
            {
                var sentSomething = await SendOnceAsync(ct).ConfigureAwait(false);
                if (sentSomething) last = DateTime.UtcNow;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _state = "error";
                    _error = $"{Config.Host} did not accept an update";
                    _sent.Clear();
                    _topSent = null;
                }
                _log.LogDebug(ex, "WLED {Host}: send failed", Config.Host);
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                Wake();
            }
        }
    }

    /// <summary>Sends one request with every segment that differs. Returns false when nothing differed.</summary>
    internal async Task<bool> SendOnceAsync(CancellationToken ct)
    {
        string body;
        var parts = new List<(int Id, string? Props, string? Leds)>();
        var done = new List<int>();
        bool? top;
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        lock (_gate)
        {
            if (_state != "connected") return false;
            top = _topWant != _topSent ? _topWant : null;
            foreach (var (id, w) in _want)
            {
                var sent = _sent.GetValueOrDefault(id);
                if (w.Props is not null && w.Props != sent.Props) parts.Add((id, w.Props, null));
                else if (w.Leds is not null && w.Leds != sent.Leds) parts.Add((id, null, w.Leds));
                else if (w.Releasing) done.Add(id);   // its own look is already back on the device
            }
            foreach (var id in done)
            {
                if (_want.Remove(id, out var dw) && dw.Restore is not null) _baseline[id] = dw.Restore;
                _sent.Remove(id);
            }
            if (top is null && parts.Count == 0) return false;

            var sb = new StringBuilder("{\"tt\":0");
            if (top is { } t) sb.Append(",\"on\":").Append(t ? "true" : "false");
            var segs = parts;
            if (segs.Count > 0)
            {
                sb.Append(",\"seg\":[");
                for (var i = 0; i < segs.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(segs[i].Id).Append(',').Append(segs[i].Props ?? segs[i].Leds).Append('}');
                }
                sb.Append(']');
            }
            sb.Append('}');
            body = sb.ToString();
        }

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var res = await _http.PostAsync("json/state", content, ct).ConfigureAwait(false);
        Interlocked.Increment(ref Requests);
        res.EnsureSuccessStatusCode();

        var more = false;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (top is not null) _topSent = top;
            foreach (var (id, props, leds) in parts)
            {
                _touched[id] = now;
                if (!_want.TryGetValue(id, out var w)) continue;
                var sent = _sent.GetValueOrDefault(id);
                if (props is not null) sent = (props, null);   // a property change unfreezes the segment: LEDs go again
                if (leds is not null) sent = (sent.Props, leds);
                _sent[id] = sent;
                if (w.Releasing && props is not null && w.Props == props)
                {
                    // Restored: from now on the segment is the user's again.
                    _want.Remove(id);
                    _sent.Remove(id);
                    if (w.Restore is not null) _baseline[id] = w.Restore;
                    continue;
                }
                if ((w.Props is not null && w.Props != sent.Props) || (w.Leds is not null && w.Leds != sent.Leds)) more = true;
            }
            if (_topWant != _topSent) more = true;
            if (top == true) _lightOn = true;
            if (top == false) _lightOn = false;
        }
        if (more) Wake();
        return true;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Puts every segment back the way it was. Used when the feature stops or settings change.</summary>
    public async Task ReleaseAllAsync(TimeSpan timeout)
    {
        int[] owned;
        lock (_gate) owned = _want.Keys.ToArray();
        foreach (var id in owned) Release(id);
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            lock (_gate) if (_want.Count == 0 && _topWant == _topSent) return;
            try
            {
                if (!await SendOnceAsync(CancellationToken.None).WaitAsync(end - DateTime.UtcNow).ConfigureAwait(false))
                {
                    lock (_gate) if (_state != "connected") return;   // cannot reach it; nothing to restore
                }
            }
            catch (Exception) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var t in new[] { _poller, _sender })
        {
            if (t is null) continue;
            try { await t.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* stopping */ }
        }
        _http.Dispose();
        _cts.Dispose();
        _wake.Dispose();
        _sendLock.Dispose();
    }
}
