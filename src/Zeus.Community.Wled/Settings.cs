// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeus.Community.Wled;

/// <summary>Outcome of a request. Status is the HTTP status the API layer should use.</summary>
internal sealed record CommandResult(bool Ok, string Message, int Status)
{
    public static CommandResult Success(string message) => new(true, message, 200);
    public static CommandResult Blocked(string message) => new(false, message, 409);
    public static CommandResult Invalid(string message) => new(false, message, 400);
    public static CommandResult Upstream(string message) => new(false, message, 502);
}

/// <summary>One WLED controller. Host is normalised to http://host[:port].</summary>
internal sealed record Controller(string Id, string Name, string Host, bool Enabled, int MaxRate, bool TurnOn);

/// <summary>Where the radio's meters come from.</summary>
internal sealed record SourceSettings(
    bool TciEnabled, string TciHost, int TciPort, int TciIntervalMs,
    bool AmpEnabled, string? ZeusUrl)
{
    public static SourceSettings Default { get; } = new(true, "127.0.0.1", 40001, 100, false, null);
}

/// <summary>A colour, an effect (by name, looked up on each controller), and its sliders.</summary>
internal sealed record Look(string Color, string? Effect, int Brightness, int Speed, int Intensity);

/// <summary>How a level becomes light.</summary>
internal sealed record MeterSpec(
    double Min, double Max, double? Warn, double? Bad, bool LowIsBad,
    string OkColor, string WarnColor, string BadColor, string OffColor,
    int Brightness, int Smoothing, bool PeakHold);

/// <summary>
/// One mapping: a signal, a place on a controller (one segment or all of them), and how to show it.
/// Rules earlier in the list win a segment when two want it at the same time.
/// </summary>
internal sealed record Rule(
    string Id, string? Label, bool Enabled, string Signal, string Controller, int Segment,
    string Style, string Gate, Look? Look, MeterSpec? Meter, IReadOnlyDictionary<string, string>? BandColors);

internal static class Styles
{
    public const string Look = "look", Percent = "percent", Leds = "leds", Color = "color", Band = "band";

    public static string[] For(SignalKind kind) => kind switch
    {
        SignalKind.Flag => [Look],
        SignalKind.Level => [Percent, Leds, Color],
        _ => [Band],
    };
}

internal static class Gates
{
    public static readonly string[] All = ["always", "tx", "rx"];
}

internal sealed record WledSettings(IReadOnlyList<Controller> Controllers, SourceSettings Sources, IReadOnlyList<Rule> Rules)
{
    public static WledSettings Empty { get; } = new([], SourceSettings.Default, []);
}

// ---- input shapes (everything optional, validated into the records above) ----

internal sealed record ControllerInput(string? Id, string? Name, string? Host, bool? Enabled, int? MaxRate, bool? TurnOn);

internal sealed record SourceInput(bool? TciEnabled, string? TciHost, int? TciPort, int? TciIntervalMs, bool? AmpEnabled, string? ZeusUrl);

internal sealed record LookInput(string? Color, string? Effect, int? Brightness, int? Speed, int? Intensity);

internal sealed record MeterInput(
    double? Min, double? Max, double? Warn, double? Bad, bool? LowIsBad,
    string? OkColor, string? WarnColor, string? BadColor, string? OffColor,
    int? Brightness, int? Smoothing, bool? PeakHold);

internal sealed record RuleInput(
    string? Id, string? Label, bool? Enabled, string? Signal, string? Controller, int? Segment,
    string? Style, string? Gate, LookInput? Look, MeterInput? Meter, Dictionary<string, string>? BandColors);

internal static class Validate
{
    public const int MaxControllers = 4;
    public const int MaxRules = 32;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // ---- controllers ----

    /// <summary>Accepts host, host:port, IPv4/IPv6, or http://... with no path. Returns http://host[:port].</summary>
    public static string? Host(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim();
        if (text.Length > 200) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme != "http") return null;
        if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return null;
        if (u.AbsolutePath is not ("/" or "")) return null;
        if (u.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)) return null;
        return $"http://{u.Authority}";
    }

    /// <summary>The Zeus address for the amplifier route: http(s), optional path prefix.</summary>
    public static string? ZeusUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim();
        if (text.Length > 300) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
        if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return null;
        if (u.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)) return null;
        return $"{u.Scheme}://{u.Authority}{u.AbsolutePath.TrimEnd('/')}";
    }

    public static (IReadOnlyList<Controller>? Value, string? Error) Controllers(IReadOnlyList<ControllerInput>? input)
    {
        if (input is null) return (null, "No controllers sent");
        if (input.Count > MaxControllers) return (null, $"At most {MaxControllers} WLED controllers");
        var list = new List<Controller>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in input)
        {
            var host = Host(c.Host);
            if (host is null) return (null, $"“{Cap(c.Host, 60)}” is not a WLED address (e.g. 192.168.1.50 or wled-desk.local)");
            if (!hosts.Add(host)) return (null, $"{host} is listed twice");
            var id = IsId(c.Id) ? c.Id! : NewId(ids);
            if (!ids.Add(id)) id = NewId(ids);
            ids.Add(id);
            var name = Cap(c.Name?.Trim(), 32);
            list.Add(new Controller(id, string.IsNullOrEmpty(name) ? host[7..] : name, host,
                c.Enabled ?? true, Math.Clamp(c.MaxRate ?? 20, 2, 40), c.TurnOn ?? true));
        }
        return (list, null);
    }

    public static (SourceSettings? Value, string? Error) Sources(SourceInput? s, SourceSettings current)
    {
        if (s is null) return (null, "No settings sent");
        var tciHost = current.TciHost;
        if (s.TciHost is not null)
        {
            var h = s.TciHost.Trim();
            if (h.Length == 0 || h.Length > 120 || h.Contains('/') || Uri.CheckHostName(h.Trim('[', ']')) == UriHostNameType.Unknown)
                return (null, "Enter the TCI host, e.g. 127.0.0.1");
            tciHost = h.Trim('[', ']');
        }
        var port = s.TciPort ?? current.TciPort;
        if (port is < 1 or > 65535) return (null, "TCI port must be 1 to 65535");
        var zeus = current.ZeusUrl;
        if (s.ZeusUrl is not null)
        {
            if (s.ZeusUrl.Trim().Length == 0) zeus = null;
            else if (ZeusUrl(s.ZeusUrl) is { } z) zeus = z;
            else return (null, "Enter the Zeus address: the one in your browser's address bar when Zeus is open");
        }
        var ampEnabled = s.AmpEnabled ?? current.AmpEnabled;
        return (new SourceSettings(
            s.TciEnabled ?? current.TciEnabled, tciHost, port,
            Math.Clamp(s.TciIntervalMs ?? current.TciIntervalMs, 30, 1000),
            ampEnabled, zeus), null);
    }

    // ---- rules ----

    public static (IReadOnlyList<Rule>? Value, string? Error) Rules(IReadOnlyList<RuleInput>? input, IReadOnlyList<Controller> controllers)
    {
        if (input is null) return (null, "No rules sent");
        if (input.Count > MaxRules) return (null, $"At most {MaxRules} rules");
        var list = new List<Rule>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < input.Count; i++)
        {
            var (rule, error) = One(input[i], controllers, ids);
            if (rule is null) return (null, $"Rule {i + 1}: {error}");
            list.Add(rule);
        }
        return (list, null);
    }

    private static (Rule? Rule, string? Error) One(RuleInput r, IReadOnlyList<Controller> controllers, HashSet<string> ids)
    {
        var signal = Signals.Get(r.Signal);
        if (signal is null) return (null, "choose what it follows");
        if (controllers.All(c => c.Id != r.Controller)) return (null, "choose a WLED controller");
        var segment = r.Segment ?? -1;
        if (segment is < -1 or > 63) return (null, "segment must be 0 to 63, or all");
        var styles = Styles.For(signal.Kind);
        var style = r.Style is { } st && styles.Contains(st) ? st : styles[0];
        var gate = r.Gate is { } g && Gates.All.Contains(g) ? g : signal.DefaultGate;
        if (signal.Kind != SignalKind.Level) gate = "always";

        Look? look = null;
        MeterSpec? meter = null;
        Dictionary<string, string>? bands = null;
        if (style is Styles.Look or Styles.Band)
        {
            var (l, err) = LookOf(r.Look, style == Styles.Look ? "FF0000" : "FFFFFF");
            if (l is null) return (null, err);
            look = l;
        }
        if (signal.Kind == SignalKind.Level)
        {
            var (m, err) = MeterOf(r.Meter, signal);
            if (m is null) return (null, err);
            meter = m;
        }
        if (style == Styles.Band)
        {
            bands = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (band, color) in Signals.BandDefaults)
            {
                var given = r.BandColors is { } bc && bc.TryGetValue(band, out var v) ? v : color;
                var hex = Color(given);
                if (hex is null) return (null, $"{band} colour is not a colour");
                bands[band] = hex;
            }
        }

        var id = IsId(r.Id) && !ids.Contains(r.Id!) ? r.Id! : NewId(ids);
        ids.Add(id);
        var label = Cap(r.Label?.Trim(), 40);
        return (new Rule(id, string.IsNullOrEmpty(label) ? null : label, r.Enabled ?? true, signal.Id, r.Controller!, segment,
            style, gate, look, meter, bands), null);
    }

    private static (Look? Look, string? Error) LookOf(LookInput? l, string defaultColor)
    {
        var color = Color(l?.Color ?? defaultColor);
        if (color is null) return (null, "colour must be a colour like FF0000");
        var effect = Cap(l?.Effect?.Trim(), 48);
        return (new Look(color, string.IsNullOrEmpty(effect) ? null : effect,
            Math.Clamp(l?.Brightness ?? 255, 1, 255), Math.Clamp(l?.Speed ?? 128, 0, 255), Math.Clamp(l?.Intensity ?? 128, 0, 255)), null);
    }

    private static (MeterSpec? Meter, string? Error) MeterOf(MeterInput? m, SignalInfo s)
    {
        var min = m?.Min ?? s.Min;
        var max = m?.Max ?? s.Max;
        if (!double.IsFinite(min) || !double.IsFinite(max) || !(min < max)) return (null, "bar minimum must be below its maximum");
        double? warn = m is null ? s.Warn : m.Warn;
        double? bad = m is null ? s.Bad : m.Bad;
        if ((warn is null) != (bad is null)) return (null, "fill both colour thresholds, or neither");
        var low = m?.LowIsBad ?? s.LowIsBad;
        if (warn is { } w && bad is { } b && (!double.IsFinite(w) || !double.IsFinite(b) || (low ? w < b : w > b)))
            return (null, low ? "with “low is bad”, yellow must not be below red" : "yellow must not be above red");
        var ok = Color(m?.OkColor ?? "00FF00");
        var wc = Color(m?.WarnColor ?? "FFA000");
        var bc = Color(m?.BadColor ?? "FF0000");
        var off = Color(m?.OffColor ?? "000000");
        if (ok is null || wc is null || bc is null || off is null) return (null, "bar colours must be colours like 00FF00");
        return (new MeterSpec(min, max, warn, bad, low, ok, wc, bc, off,
            Math.Clamp(m?.Brightness ?? 255, 1, 255), Math.Clamp(m?.Smoothing ?? 40, 0, 100), m?.PeakHold ?? true), null);
    }

    /// <summary>"#ff8800", "ff8800" or "F80" → "FF8800".</summary>
    public static string? Color(string? input)
    {
        if (input is null) return null;
        var t = input.Trim().TrimStart('#');
        if (t.Length == 3) t = string.Concat(t.Select(ch => new string(ch, 2)));
        if (t.Length != 6 || !t.All(char.IsAsciiHexDigit)) return null;
        return t.ToUpperInvariant();
    }

    public static (int R, int G, int B) Rgb(string hex) =>
        (int.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         int.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         int.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static bool IsId(string? id) => id is { Length: >= 4 and <= 16 } && id.All(char.IsAsciiLetterOrDigit);

    private static string NewId(HashSet<string> taken)
    {
        while (true)
        {
            var id = Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 4)).ToLowerInvariant();
            if (!taken.Contains(id)) return id;
        }
    }

    private static string? Cap(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    // ---- persistence ----

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return null; }
    }
}
