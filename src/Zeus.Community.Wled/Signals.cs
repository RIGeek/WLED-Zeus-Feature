// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Community.Wled;

internal enum SignalKind { Flag, Level, Text }

/// <summary>Where a signal's value comes from.</summary>
internal enum SignalSource { Radio, Tci, Amp }

/// <summary>Description of one radio signal a rule can follow, with sensible bar defaults.</summary>
internal sealed record SignalInfo(
    string Id, string Name, SignalKind Kind, SignalSource Source, string? Unit,
    double Min, double Max, double? Warn, double? Bad, bool LowIsBad, string DefaultGate, int Digits);

internal static class Signals
{
    public const string Tx = "tx", Rx = "rx", Tune = "tune", Band = "band";
    public const string SMeter = "smeter", Mic = "mic", RadioPower = "radioPower", RadioSwr = "radioSwr";
    public const string AmpPower = "ampPower", AmpSwr = "ampSwr";

    public static readonly IReadOnlyList<SignalInfo> All =
    [
        new(Tx, "Transmitting", SignalKind.Flag, SignalSource.Radio, null, 0, 1, null, null, false, "always", 0),
        new(Rx, "Receiving", SignalKind.Flag, SignalSource.Radio, null, 0, 1, null, null, false, "always", 0),
        new(Tune, "Tuning", SignalKind.Flag, SignalSource.Tci, null, 0, 1, null, null, false, "always", 0),
        new(Band, "Band", SignalKind.Text, SignalSource.Radio, null, 0, 1, null, null, false, "always", 0),
        // S1 = -121 dBm ... S9 = -73 dBm, S9+40 = -33 dBm.
        new(SMeter, "S-meter", SignalKind.Level, SignalSource.Tci, "dBm", -121, -33, null, null, false, "rx", 0),
        new(Mic, "Mic level", SignalKind.Level, SignalSource.Tci, "dB", -40, 0, -10, -3, false, "tx", 1),
        new(RadioPower, "Radio power", SignalKind.Level, SignalSource.Tci, "W", 0, 100, null, null, false, "tx", 1),
        new(RadioSwr, "SWR (radio)", SignalKind.Level, SignalSource.Tci, ":1", 1, 3, 1.5, 2, false, "tx", 2),
        new(AmpPower, "Amp power", SignalKind.Level, SignalSource.Amp, "W", 0, 1300, null, null, false, "tx", 0),
        new(AmpSwr, "SWR (amp)", SignalKind.Level, SignalSource.Amp, ":1", 1, 3, 1.5, 2, false, "tx", 2),
    ];

    public static SignalInfo? Get(string? id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>The bands a band rule can colour, with a default colour each (hex RRGGBB).</summary>
    public static readonly IReadOnlyList<(string Band, string Color)> BandDefaults =
    [
        ("160m", "8000FF"), ("80m", "0000FF"), ("60m", "0080FF"), ("40m", "00C0C0"), ("30m", "00FF40"),
        ("20m", "80FF00"), ("17m", "FFFF00"), ("15m", "FFB000"), ("12m", "FF6000"), ("10m", "FF0000"),
        ("6m", "FF00A0"), ("other", "404040"),
    ];

    /// <summary>Amateur band for a frequency (IARU region 2 edges, slightly widened), or null outside them.</summary>
    public static string? BandOf(long hz)
    {
        var khz = hz / 1000.0;
        return khz switch
        {
            >= 1800 and <= 2000 => "160m",
            >= 3500 and <= 4000 => "80m",
            >= 5250 and <= 5450 => "60m",
            >= 7000 and <= 7300 => "40m",
            >= 10100 and <= 10150 => "30m",
            >= 14000 and <= 14350 => "20m",
            >= 18068 and <= 18168 => "17m",
            >= 21000 and <= 21450 => "15m",
            >= 24890 and <= 24990 => "12m",
            >= 28000 and <= 29700 => "10m",
            >= 50000 and <= 54000 => "6m",
            _ => null,
        };
    }
}

/// <summary>A signal's latest value. Text is set only for the band.</summary>
internal readonly record struct SignalValue(double? Number, string? Text, bool Valid);

/// <summary>
/// Latest value of every signal, written by the sources (Zeus radio state, TCI, the amplifier
/// feature) and read by the rule engine. Tests and the panel's "Test" button can override a
/// signal for a few seconds without touching the radio.
/// </summary>
internal sealed class SignalHub
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SignalValue> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (SignalValue Value, DateTime UntilUtc)> _overrides = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _now;

    public SignalHub(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.UtcNow);

    public void Set(string id, double? number) => Put(id, new SignalValue(number, null, number is not null));
    public void SetFlag(string id, bool? on) => Put(id, new SignalValue(on is null ? null : on.Value ? 1 : 0, null, on is not null));
    public void SetText(string id, string? text) => Put(id, new SignalValue(null, text, text is not null));
    public void Invalidate(string id) => Put(id, default);

    private void Put(string id, SignalValue v)
    {
        lock (_gate) _values[id] = v;
    }

    public SignalValue Get(string id)
    {
        lock (_gate)
        {
            if (_overrides.TryGetValue(id, out var o))
            {
                if (o.UntilUtc > _now()) return o.Value;
                _overrides.Remove(id);
            }
            if (id == Signals.Rx)
            {
                // Receiving is simply "not transmitting", so it follows whichever source supplies TX.
                var tx = _values.TryGetValue(Signals.Tx, out var t) ? t : default;
                if (_overrides.TryGetValue(Signals.Tx, out var ot) && ot.UntilUtc > _now()) tx = ot.Value;
                return tx.Valid ? new SignalValue(tx.Number > 0 ? 0 : 1, null, true) : default;
            }
            return _values.TryGetValue(id, out var v) ? v : default;
        }
    }

    public bool IsOn(string id) => Get(id) is { Valid: true, Number: > 0 };

    public void Override(string id, SignalValue value, TimeSpan duration)
    {
        lock (_gate) _overrides[id] = (value, _now() + duration);
    }

    public void ClearOverrides()
    {
        lock (_gate) _overrides.Clear();
    }

    public bool Overridden(string id)
    {
        lock (_gate) return _overrides.TryGetValue(id, out var o) && o.UntilUtc > _now();
    }
}
