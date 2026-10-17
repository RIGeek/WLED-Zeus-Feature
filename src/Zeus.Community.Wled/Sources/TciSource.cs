// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Zeus.Community.Wled.Sources;

/// <summary>
/// Read-only TCI client for the meters the Zeus plugin SDK does not expose: S-meter, mic level,
/// transmit power and SWR (the TCI sensor streams), plus tune. It connects to Zeus's own TCI
/// server, asks for the sensor streams, and never sends anything that changes the radio.
/// </summary>
internal sealed class TciSource : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly SignalHub _hub;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private string _state = "off";
    private string? _error;
    private string? _endpoint;

    /// <summary>When true, TCI's trx command also drives the transmit signal (no Zeus radio state).</summary>
    public bool SupplyTx { get; set; }

    public TimeSpan ReconnectMin { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReconnectMax { get; init; } = TimeSpan.FromSeconds(30);

    public TciSource(ILogger log, SignalHub hub)
    {
        _log = log;
        _hub = hub;
    }

    public (string State, string? Error, string? Endpoint) Describe()
    {
        lock (_gate) return (_state, _error, _endpoint);
    }

    /// <summary>(Re)starts the client; a null host stops it.</summary>
    public async Task StartAsync(string? host, int port, int intervalMs)
    {
        await StopAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _error = null;
            if (host is null)
            {
                _state = "off";
                _endpoint = null;
                return;
            }
            _endpoint = $"ws://{(host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host)}:{port}";
            _state = "connecting";
            var cts = new CancellationTokenSource();
            _cts = cts;
            var endpoint = _endpoint;
            _task = Task.Run(() => SuperviseAsync(new Uri(endpoint), intervalMs, cts.Token));
        }
    }

    private async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _cts;
            task = _task;
            _cts = null;
            _task = null;
        }
        if (cts is null) return;
        cts.Cancel();
        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        }
        cts.Dispose();
        ClearValues();
    }

    private void ClearValues()
    {
        foreach (var id in new[] { Signals.SMeter, Signals.Mic, Signals.RadioPower, Signals.RadioSwr, Signals.Tune })
            _hub.Invalidate(id);
        if (SupplyTx) _hub.Invalidate(Signals.Tx);
    }

    private async Task SuperviseAsync(Uri uri, int intervalMs, CancellationToken ct)
    {
        var delay = ReconnectMin;
        while (!ct.IsCancellationRequested)
        {
            lock (_gate) _state = "connecting";
            var started = DateTime.UtcNow;
            try
            {
                await RunAsync(uri, intervalMs, ct).ConfigureAwait(false);
                lock (_gate) { _state = "disconnected"; _error = "Zeus closed the TCI connection"; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var message = ex is WebSocketException or System.Net.Http.HttpRequestException or OperationCanceledException
                    ? $"Cannot reach Zeus TCI at {uri.Authority} (is TCI enabled in Zeus?)"
                    : ex.Message;
                lock (_gate) { _state = "disconnected"; _error = message; }
                _log.LogInformation("TCI connection ended: {Message}", message);
            }
            ClearValues();
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(1)) delay = ReconnectMin;
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, ReconnectMax.Ticks));
        }
    }

    private async Task RunAsync(Uri uri, int intervalMs, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(8));
            await ws.ConnectAsync(uri, connect.Token).ConfigureAwait(false);
        }

        var parser = new TciParser(_hub) { SupplyTx = SupplyTx };
        var asked = 0;
        async Task AskOnceAsync()
        {
            if (Interlocked.Exchange(ref asked, 1) != 0) return;
            await AskForSensorsAsync(ws, intervalMs, ct).ConfigureAwait(false);
            MarkConnected();
        }

        // A server that never says "ready;" still gets asked for the sensor streams after a few seconds.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fallback = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), session.Token).ConfigureAwait(false);
            await AskOnceAsync().ConfigureAwait(false);
        }, session.Token);

        try
        {
            var buffer = new ArrayBufferWriter<byte>(8192);
            while (true)
            {
                buffer.Clear();
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer.GetMemory(8192), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    buffer.Advance(r.Count);
                    if (buffer.WrittenCount > 1024 * 1024) throw new InvalidOperationException("TCI sent an oversized message");
                } while (!r.EndOfMessage);

                if (r.MessageType != WebSocketMessageType.Text) continue;   // audio / IQ frames are never asked for
                if (parser.Feed(Encoding.UTF8.GetString(buffer.WrittenSpan))) await AskOnceAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            session.Cancel();
            try { await fallback.ConfigureAwait(false); } catch { /* cancelled or socket gone */ }
        }
    }

    private void MarkConnected()
    {
        lock (_gate) { _state = "connected"; _error = null; }
    }

    private static Task AskForSensorsAsync(ClientWebSocket ws, int intervalMs, CancellationToken ct)
    {
        var ms = Math.Clamp(intervalMs, 30, 1000).ToString(CultureInfo.InvariantCulture);
        var text = $"rx_sensors_enable:true,{ms};tx_sensors_enable:true,{ms};";
        return ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}

/// <summary>Turns TCI text ("name:arg,arg;name:arg;") into signal values. Only TRX 0, receiver channel 0.</summary>
internal sealed class TciParser(SignalHub hub)
{
    public bool SupplyTx { get; init; }

    /// <summary>Applies every command in a frame. Returns true if the frame contained "ready".</summary>
    public bool Feed(string text)
    {
        var ready = false;
        foreach (var raw in text.Split(';'))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            var colon = part.IndexOf(':');
            var name = (colon < 0 ? part : part[..colon]).Trim().ToLowerInvariant();
            var args = colon < 0 ? [] : part[(colon + 1)..].Split(',', StringSplitOptions.TrimEntries);
            if (name == "ready") ready = true;
            else Apply(name, args);
        }
        return ready;
    }

    private void Apply(string name, string[] a)
    {
        switch (name)
        {
            case "trx" when a.Length >= 2 && Int(a[0]) == 0:
                var on = Bool(a[1]);
                if (SupplyTx) hub.SetFlag(Signals.Tx, on);
                if (on == false)
                {
                    // Sensor reports stop on receive; do not leave the last transmit values standing.
                    hub.Set(Signals.RadioPower, 0);
                    hub.Set(Signals.Mic, null);
                    hub.Set(Signals.RadioSwr, null);
                }
                break;
            case "tune" when a.Length >= 2 && Int(a[0]) == 0:
                hub.SetFlag(Signals.Tune, Bool(a[1]));
                break;
            case "rx_smeter" or "rx_channel_sensors" when a.Length >= 3 && Int(a[0]) == 0 && Int(a[1]) == 0:
                hub.Set(Signals.SMeter, Num(a[2]));
                break;
            case "tx_sensors" when a.Length >= 5 && Int(a[0]) == 0:
                hub.Set(Signals.Mic, Num(a[1]));
                hub.Set(Signals.RadioPower, Num(a[2]));
                hub.Set(Signals.RadioSwr, Num(a[4]));
                break;
            case "tx_power" when a.Length >= 1:
                hub.Set(Signals.RadioPower, Num(a[^1]));
                break;
            case "tx_swr" when a.Length >= 1:
                hub.Set(Signals.RadioSwr, Num(a[^1]));
                break;
        }
    }

    private static int? Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    private static bool? Bool(string s) => s.ToLowerInvariant() switch { "true" => true, "false" => false, _ => null };
}
