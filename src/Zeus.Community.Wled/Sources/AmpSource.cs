// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Zeus.Community.Wled.Sources;

/// <summary>
/// Reads forward power and SWR from the RZ1ZR LDMOS Amplifier feature (com.n1mjf.hfamp) through its
/// own read-only status route on this Zeus. Zeus plugins cannot call each other directly, so this
/// goes over HTTP to the Zeus address entered in settings. Nothing is ever sent to the amplifier.
/// </summary>
internal sealed class AmpSource : IAsyncDisposable
{
    public const string AmpPluginId = "com.n1mjf.hfamp";

    private readonly ILogger _log;
    private readonly SignalHub _hub;
    private readonly HttpMessageHandler? _handler;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private string _state = "off";
    private string? _error;
    private double? _fullScale;

    /// <summary>The engine sets this; the amplifier is only polled while some rule needs it.</summary>
    public Func<bool> Wanted { get; set; } = () => true;

    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(150);

    public AmpSource(ILogger log, SignalHub hub, HttpMessageHandler? handler = null)
    {
        _log = log;
        _hub = hub;
        _handler = handler;
    }

    public (string State, string? Error, double? FullScaleW) Describe()
    {
        lock (_gate) return (_state, _error, _fullScale);
    }

    public static Uri StatusUri(string zeusBaseUrl) => new($"{zeusBaseUrl.TrimEnd('/')}/api/plugins/{AmpPluginId}/status");

    public async Task StartAsync(string? zeusBaseUrl)
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _error = null;
            if (zeusBaseUrl is null) { _state = "off"; return; }
            _state = "idle";
            var cts = new CancellationTokenSource();
            _cts = cts;
            _task = Task.Run(() => PollAsync(StatusUri(zeusBaseUrl), cts.Token));
        }
    }

    private async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate) { cts = _cts; task = _task; _cts = null; _task = null; }
        if (cts is null) return;
        cts.Cancel();
        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
        cts.Dispose();
        Clear();
    }

    private void Clear()
    {
        _hub.Invalidate(Signals.AmpPower);
        _hub.Invalidate(Signals.AmpSwr);
    }

    private async Task PollAsync(Uri uri, CancellationToken ct)
    {
        using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(3);
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!Wanted())
            {
                lock (_gate) { _state = "idle"; }
                Clear();
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            try
            {
                using var res = await http.GetAsync(uri, ct).ConfigureAwait(false);
                if ((int)res.StatusCode == 404) throw new AmpException("The amplifier feature is not installed in this Zeus (or the address is not Zeus)");
                if ((int)res.StatusCode == 503) throw new AmpException("The amplifier feature is not running");
                if (!res.IsSuccessStatusCode) throw new AmpException($"Zeus answered HTTP {(int)res.StatusCode}");
                await using var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
                Apply(doc.RootElement);
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
                    AmpException a => a.Message,
                    JsonException => "The amplifier feature answered with something unexpected",
                    _ => $"Cannot reach Zeus at {uri.GetLeftPart(UriPartial.Authority)}",
                };
                lock (_gate) { _state = "error"; _error = message; }
                if (failures++ == 0) _log.LogInformation("Amplifier readings unavailable: {Message}", message);
                Clear();
            }
            var wait = failures == 0 ? Interval : TimeSpan.FromSeconds(Math.Min(10, failures * 2));
            try { await Task.Delay(wait, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Takes forward power and SWR from the amplifier feature's status JSON.</summary>
    internal void Apply(JsonElement status)
    {
        var link = status.TryGetProperty("connection", out var c) && c.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString() : null;
        double? full = status.TryGetProperty("limits", out var l) && l.TryGetProperty("forwardW", out var fw) && fw.TryGetDouble(out var fwv) ? fwv : null;
        var meters = status.TryGetProperty("meters", out var m) && m.ValueKind == JsonValueKind.Object ? m : (JsonElement?)null;
        var fresh = meters is { } mm && mm.TryGetProperty("fresh", out var fr) && fr.ValueKind == JsonValueKind.True;

        lock (_gate)
        {
            _fullScale = full;
            if (link != "connected") { _state = "amp_offline"; _error = "The amplifier feature is not connected to the amplifier"; }
            else { _state = "connected"; _error = null; }
        }
        if (link != "connected" || meters is null)
        {
            Clear();
            return;
        }
        var mv = meters.Value;
        _hub.Set(Signals.AmpPower, fresh ? Num(mv, "forwardW") : 0);
        _hub.Set(Signals.AmpSwr, fresh ? Num(mv, "swr") : null);
    }

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

internal sealed class AmpException(string message) : Exception(message);
