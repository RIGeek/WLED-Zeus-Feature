// SPDX-License-Identifier: GPL-2.0-or-later
//
// Self-contained test runner (a .NET 10 file-based app, so no test project or
// test-framework packages are needed):
//
//     dotnet run tests/Wled.Tests.cs
//
// It runs the real backend against mock WLED controllers (the JSON API), a mock Zeus TCI
// server, and a mock of the amplifier feature's status route. Nothing here touches real
// lights or a real radio.
#:sdk Microsoft.NET.Sdk.Web
#:property AssemblyName=Zeus.Community.Wled.Tests
#:property PublishAot=false
#:project ../src/Zeus.Community.Wled/Zeus.Community.Wled.csproj

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Community.Wled;
using Zeus.Community.Wled.Sources;
using Zeus.Community.Wled.Wled;

var tests = new List<(string Name, Func<Task> Run)>();
void Test(string name, Func<Task> run) => tests.Add((name, run));
void Check(bool condition, string message) { if (!condition) throw new Exception("FAILED: " + message); }
async Task<bool> Until(Func<bool> condition, int timeoutMs = 4000)
{
    var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(20); }
    return condition();
}

// Development host instead of the tests: mock WLED strips you can watch, the real backend,
// and the panel harness.   WLED_DEV_HOST=harness dotnet run tests/Wled.Tests.cs
if (Environment.GetEnvironmentVariable("WLED_DEV_HOST") is { Length: > 0 } devRoot)
{
    await DevHost.RunAsync(devRoot);
    return 0;
}

MeterSpec Spec(double min = 0, double max = 100, double? warn = 60, double? bad = 80, bool low = false) =>
    new(min, max, warn, bad, low, "00FF00", "FFA000", "FF0000", "000000", 255, 0, true);

async Task<(WledService Service, FakeRadioFeed Radio, MockWled Wled)> Running(
    int segments = 3, bool lightOn = true, Func<string, RuleInput[]>? rules = null, int maxRate = 40)
{
    var wled = await MockWled.StartAsync(segments, lightOn);
    var radio = new FakeRadioFeed();
    var s = new WledService(NullLogger.Instance, new MemorySettingsStore(), radio);
    await s.StartAsync(CancellationToken.None);
    await s.SetSourcesAsync(new SourceInput(false, null, null, null, false, null), CancellationToken.None);
    var (r, cs) = await s.SetControllersAsync([new ControllerInput(null, "Desk", wled.Host, true, maxRate, true)], CancellationToken.None);
    Check(r.Ok, "controller accepted: " + r.Message);
    var id = cs![0].Id;
    Check(await Until(() => s.BuildStatus().Controllers[0].State == "connected"), "controller connected");
    if (rules is not null)
    {
        var (rr, _) = await s.SetRulesAsync(rules(id), CancellationToken.None);
        Check(rr.Ok, "rules accepted: " + rr.Message);
    }
    return (s, radio, wled);
}

RuleInput Rule(string controller, string signal, int segment, string? style = null, string? gate = null,
    LookInput? look = null, MeterInput? meter = null, string? label = null) =>
    new(null, label, true, signal, controller, segment, style, gate, look, meter, null);

// ------------------------------------------------------------------ settings

Test("settings: addresses, colours, and at most four controllers", () =>
{
    Check(Validate.Host("192.168.1.50") == "http://192.168.1.50", "bare IP");
    Check(Validate.Host("wled-desk.local:8080") == "http://wled-desk.local:8080", "name and port");
    Check(Validate.Host("http://10.0.0.9/") == "http://10.0.0.9", "trailing slash");
    Check(Validate.Host("https://x") is null && Validate.Host("http://x/json") is null && Validate.Host("http://u:p@x") is null, "https, path, credentials refused");
    Check(Validate.ZeusUrl("127.0.0.1:5005") == "http://127.0.0.1:5005" && Validate.ZeusUrl("https://zeus.lan/app/") == "https://zeus.lan/app", "zeus url");
    Check(Validate.Color("#f80") == "FF8800" && Validate.Color("00ff00") == "00FF00" && Validate.Color("red") is null, "colours");
    var five = Enumerable.Range(1, 5).Select(i => new ControllerInput(null, null, $"10.0.0.{i}", null, null, null)).ToList();
    var (c, err) = Validate.Controllers(five);
    Check(c is null && err!.Contains("At most 4"), "five refused");
    (c, err) = Validate.Controllers([new(null, null, "10.0.0.1", null, null, null), new(null, null, "http://10.0.0.1", null, null, null)]);
    Check(c is null && err!.Contains("twice"), "duplicate refused");
    (c, _) = Validate.Controllers([new(null, "  ", "10.0.0.1", null, 99, null)]);
    Check(c![0].Name == "10.0.0.1" && c[0].MaxRate == 40 && c[0].TurnOn && c[0].Enabled, "defaults and clamps");
    return Task.CompletedTask;
});

Test("settings: rules get the right style, gate and bar defaults for their signal", () =>
{
    var controllers = new[] { new Controller("abcd1234", "Desk", "http://10.0.0.1", true, 20, true) };
    var (rules, err) = Validate.Rules([
        Rule("abcd1234", "tx", 0),
        Rule("abcd1234", "smeter", 1),
        Rule("abcd1234", "radioSwr", -1, "leds", "always"),
        Rule("abcd1234", "band", 2, "percent"),
    ], controllers);
    Check(rules is not null, "accepted: " + err);
    Check(rules![0].Style == "look" && rules[0].Look!.Color == "FF0000" && rules[0].Gate == "always", "tx: look, red");
    Check(rules[1].Style == "percent" && rules[1].Gate == "rx" && rules[1].Meter!.Min == -121 && rules[1].Meter!.Max == -33, "s-meter: percent, rx, S1..S9+40");
    Check(rules[2].Style == "leds" && rules[2].Gate == "always" && rules[2].Meter!.Warn == 1.5 && rules[2].Meter!.Bad == 2, "swr: zones");
    Check(rules[3].Style == "band" && rules[3].BandColors!.Count == Signals.BandDefaults.Count, "band: style forced, colours filled");
    Check(rules.Select(r => r.Id).Distinct().Count() == 4, "ids");

    (rules, err) = Validate.Rules([Rule("nope", "tx", 0)], controllers);
    Check(rules is null && err!.Contains("controller"), "unknown controller");
    (rules, err) = Validate.Rules([Rule("abcd1234", "mic", 0, meter: new MeterInput(0, 10, 8, 5, false, null, null, null, null, null, null, null))], controllers);
    Check(rules is null && err!.Contains("yellow"), "thresholds out of order");
    (rules, err) = Validate.Rules([Rule("abcd1234", "mic", 0, meter: new MeterInput(10, 10, null, null, null, null, null, null, null, null, null, null))], controllers);
    Check(rules is null && err!.Contains("below"), "empty range");
    return Task.CompletedTask;
});

// ------------------------------------------------------------------ rendering

Test("render: Percent bar, zone colours and LED runs with a peak", () =>
{
    var m = Spec();
    Check(Render.Zone(10, m) == "ok" && Render.Zone(60, m) == "warn" && Render.Zone(95, m) == "bad", "zones");
    Check(Render.Zone(5, Spec(0, 100, 30, 10, true)) == "bad" && Render.Zone(20, Spec(0, 100, 30, 10, true)) == "warn", "low is bad");
    var p = Render.Percent(m, 65, 98);
    Check(p.Contains("\"fx\":98") && p.Contains("\"ix\":65") && p.Contains("\"col\":[[255,160,0],[0,0,0]") && p.Contains("\"sx\":255"), "percent: " + p);
    Check(Render.Percent(m, 500, 98).Contains("\"ix\":100") && Render.Percent(m, -5, 98).Contains("\"ix\":0"), "clamped");
    // 10 LEDs, value 90: LEDs 0-5 green (0..60), 6-7 amber, 8 red, 9 off.
    var leds = Render.Leds(m, 90, null, 10);
    Check(leds == "\"i\":[0,6,\"00FF00\",6,8,\"FFA000\",8,9,\"FF0000\",9,10,\"000000\"]", "runs: " + leds);
    // Peak above the value lights one white LED.
    leds = Render.Leds(m, 20, 70, 10);
    Check(leds == "\"i\":[0,2,\"00FF00\",2,6,\"000000\",6,7,\"FFFFFF\",7,10,\"000000\"]", "peak: " + leds);
    Check(Render.Leds(m, 0, null, 4) == "\"i\":[0,4,\"000000\"]", "empty bar");
    return Task.CompletedTask;
});

// ------------------------------------------------------------------ sources

Test("tci: sensors, tune and transmit become signals", () =>
{
    var hub = new SignalHub();
    var p = new TciParser(hub) { SupplyTx = true };
    Check(p.Feed("protocol:ExpertSDR3,1.9;ready;"), "ready seen");
    p.Feed("rx_smeter:0,0,-73.5;rx_smeter:1,0,-20;rx_smeter:0,1,-10;");
    Check(hub.Get(Signals.SMeter).Number == -73.5, "trx 0 channel 0 only");
    p.Feed("trx:0,true;tx_sensors:0,-12.5,87.0,95.2,1.35;tune:0,true;");
    Check(hub.IsOn(Signals.Tx) && hub.IsOn(Signals.Tune) && !hub.IsOn(Signals.Rx), "tx and tune");
    Check(hub.Get(Signals.Mic).Number == -12.5 && hub.Get(Signals.RadioPower).Number == 87 && hub.Get(Signals.RadioSwr).Number == 1.35, "tx sensors");
    p.Feed("trx:0,false;");
    Check(hub.IsOn(Signals.Rx) && hub.Get(Signals.RadioPower).Number == 0 && !hub.Get(Signals.RadioSwr).Valid, "back to receive clears transmit values");
    var noTx = new TciParser(hub) { SupplyTx = false };
    noTx.Feed("trx:0,true;");
    Check(!hub.IsOn(Signals.Tx), "trx ignored when Zeus supplies MOX");
    return Task.CompletedTask;
});

Test("tci: connects to a TCI server, asks for sensors, follows it", async () =>
{
    await using var tci = await MockTci.StartAsync();
    var hub = new SignalHub();
    await using var src = new TciSource(NullLogger.Instance, hub) { ReconnectMin = TimeSpan.FromMilliseconds(100) };
    await src.StartAsync("127.0.0.1", tci.Port, 50);
    Check(await Until(() => src.Describe().State == "connected"), "connected");
    Check(await Until(() => tci.Received.Contains("rx_sensors_enable:true,50;tx_sensors_enable:true,50;")), "asked for sensors: " + tci.Received);
    await tci.SendAsync("rx_smeter:0,0,-80;");
    Check(await Until(() => hub.Get(Signals.SMeter).Number == -80), "s-meter arrived");
    await tci.DropAsync();
    Check(await Until(() => !hub.Get(Signals.SMeter).Valid), "values dropped with the connection");
    Check(await Until(() => src.Describe().State == "connected"), "reconnected");
});

Test("amp: reads forward power and SWR from the amplifier feature", async () =>
{
    var amp = await MockAmp.StartAsync();
    var hub = new SignalHub();
    await using var src = new AmpSource(NullLogger.Instance, hub) { Interval = TimeSpan.FromMilliseconds(30) };
    await src.StartAsync(amp.Url);
    amp.Set(connected: true, fresh: true, forward: 612.5, swr: 1.4);
    Check(await Until(() => hub.Get(Signals.AmpPower).Number == 612.5 && hub.Get(Signals.AmpSwr).Number == 1.4), "readings");
    Check(src.Describe() is ("connected", null, 1300), "full scale: " + src.Describe());
    amp.Set(connected: true, fresh: false, forward: 0, swr: null);
    Check(await Until(() => hub.Get(Signals.AmpPower).Number == 0 && !hub.Get(Signals.AmpSwr).Valid), "stale meters read as 0 W");
    amp.Set(connected: false, fresh: false, forward: 0, swr: null);
    Check(await Until(() => !hub.Get(Signals.AmpPower).Valid && src.Describe().State == "amp_offline"), "amp offline");
    await amp.App.StopAsync();
    Check(await Until(() => src.Describe().State == "error"), "Zeus unreachable");
});

Test("amp: missing feature is reported plainly", async () =>
{
    var amp = await MockAmp.StartAsync(installed: false);
    var hub = new SignalHub();
    await using var src = new AmpSource(NullLogger.Instance, hub) { Interval = TimeSpan.FromMilliseconds(30) };
    await src.StartAsync(amp.Url);
    Check(await Until(() => src.Describe().Error?.Contains("not installed") == true), "404 explained: " + src.Describe().Error);
    await amp.App.StopAsync();
});

// ------------------------------------------------------------------ engine against a mock WLED

Test("engine: TX look takes its segment, then gives the segment's own look back", async () =>
{
    var (s, radio, wled) = await Running(rules: id => [Rule(id, "tx", 1, look: new LookInput("FF0000", "Blink", 200, 180, 100))]);
    await using var _ = s;
    var before = wled.Segment(1).ToJsonString();
    radio.SetTx(true);
    Check(await Until(() => wled.Segment(1)["fx"]!.GetValue<int>() == wled.FxId("Blink")), "blink on segment 1");
    var seg = wled.Segment(1);
    Check(seg["bri"]!.GetValue<int>() == 200 && seg["col"]![0]!.ToJsonString() == "[255,0,0]" && seg["sx"]!.GetValue<int>() == 180, "look applied: " + seg.ToJsonString());
    Check(wled.Segment(0)["fx"]!.GetValue<int>() == 7 && wled.Segment(2)["fx"]!.GetValue<int>() == 7, "other segments untouched");
    Check(s.BuildStatus().Rules[0] is { Active: true, Segments: [1] }, "status shows ownership");
    radio.SetTx(false);
    Check(await Until(() => wled.Segment(1).ToJsonString() == before), "own look restored: " + wled.Segment(1).ToJsonString());
    Check(s.BuildStatus().Controllers[0].Driven.Count == 0, "nothing driven");
});

Test("engine: the higher rule wins a segment; the lower one takes over when it lets go", async () =>
{
    var (s, radio, wled) = await Running(rules: id => [
        Rule(id, "tx", 0, look: new LookInput("FF0000", null, 255, 128, 128)),
        Rule(id, "band", -1, look: new LookInput(null, null, 255, 128, 128)),
    ]);
    await using var _ = s;
    radio.SetFrequency(14_074_000);
    Check(await Until(() => wled.Segment(0)["col"]![0]!.ToJsonString() == "[128,255,0]" && wled.Segment(2)["col"]![0]!.ToJsonString() == "[128,255,0]"), "20 m green-yellow everywhere");
    radio.SetTx(true);
    Check(await Until(() => wled.Segment(0)["col"]![0]!.ToJsonString() == "[255,0,0]"), "TX wins segment 0");
    Check(wled.Segment(1)["col"]![0]!.ToJsonString() == "[128,255,0]", "band keeps segment 1");
    var posts = wled.Posts.Count;
    radio.SetTx(false);
    Check(await Until(() => wled.Segment(0)["col"]![0]!.ToJsonString() == "[128,255,0]"), "band takes segment 0 back");
    Check(!wled.Posts.Skip(posts).Any(p => p.Contains("\"col\":[[0,0,255]")), "no flash of the original look in between");
    radio.SetFrequency(7_100_000);
    Check(await Until(() => wled.Segment(1)["col"]![0]!.ToJsonString() == "[0,192,192]"), "40 m");
});

Test("engine: S-meter Percent bar only while receiving, and no faster than the rate limit", async () =>
{
    var (s, radio, wled) = await Running(maxRate: 10, rules: id => [Rule(id, "smeter", 2, "percent",
        meter: new MeterInput(-121, -33, -60, -40, false, null, null, null, null, 180, 0, false))]);
    await using var _ = s;
    radio.SetTx(false);
    s.Hub.Set(Signals.SMeter, -77);   // half scale
    Check(await Until(() => wled.Segment(2)["fx"]!.GetValue<int>() == wled.FxId("Percent") && wled.Segment(2)["ix"]!.GetValue<int>() == 50), "50 %: " + wled.Segment(2).ToJsonString());
    Check(wled.Segment(2)["col"]![0]!.ToJsonString() == "[0,255,0]" && wled.Segment(2)["bri"]!.GetValue<int>() == 180, "green, brightness");
    s.Hub.Set(Signals.SMeter, -50);
    Check(await Until(() => wled.Segment(2)["col"]![0]!.ToJsonString() == "[255,160,0]"), "amber over S9+20");

    var start = wled.Posts.Count;
    var t0 = DateTime.UtcNow;
    for (var i = 0; i < 100; i++) { s.Hub.Set(Signals.SMeter, -121 + i % 80); await Task.Delay(10); }
    var rate = (wled.Posts.Count - start) / (DateTime.UtcNow - t0).TotalSeconds;
    Check(rate <= 11.5, $"rate limited to 10/s, got {rate:0.0}");

    radio.SetTx(true);
    Check(await Until(() => wled.Segment(2)["fx"]!.GetValue<int>() == 7), "released on transmit");
});

Test("engine: LED-by-LED SWR bar with zones, after a set-up request", async () =>
{
    var (s, radio, wled) = await Running(rules: id => [Rule(id, "radioSwr", 0, "leds", "always")]);
    await using var _ = s;
    s.Hub.Set(Signals.RadioSwr, 2.5);   // 1..3, 10 LEDs: 75 % lit
    Check(await Until(() => wled.Leds(0) is not null), "LEDs sent");
    Check(wled.Segment(0)["fx"]!.GetValue<int>() == 0 && wled.Segment(0)["frz"]!.GetValue<bool>(), "solid, frozen by the LED data");
    var setup = wled.Posts.FindIndex(p => p.Contains("\"id\":0,\"on\":true"));
    var first = wled.Posts.FindIndex(p => p.Contains("\"i\":"));
    Check(setup >= 0 && first > setup, "set-up first, LEDs after");
    // LED centres sit at 1.1, 1.3, 1.5 ... : two green, then amber from 1.5, red from 2.0.
    Check(wled.Leds(0) == "[0,2,\"00FF00\",2,5,\"FFA000\",5,8,\"FF0000\",8,10,\"000000\"]", "zones: " + wled.Leds(0));
    var posts = wled.Posts.Count;
    s.Hub.Set(Signals.RadioSwr, 1.2);
    Check(await Until(() => wled.Leds(0) == "[0,1,\"00FF00\",1,7,\"000000\",7,8,\"FFFFFF\",8,10,\"000000\"]"), "drops, peak held: " + wled.Leds(0));
    Check(!wled.Posts.Skip(posts).Any(p => p.Contains("\"fx\"")), "no set-up resent while drawing");
});

Test("engine: a light that is off is switched on while used, and off again after", async () =>
{
    var (s, radio, wled) = await Running(lightOn: false, rules: id => [Rule(id, "tx", 0)]);
    await using var _ = s;
    radio.SetTx(true);
    Check(await Until(() => wled.LightOn), "switched on");
    radio.SetTx(false);
    Check(await Until(() => !wled.LightOn), "switched back off");
});

Test("engine: test button, identify, and restore on shutdown", async () =>
{
    var (s, radio, wled) = await Running(rules: id => [Rule(id, "mic", 1, "color", "tx")]);
    var before = wled.Segment(1).ToJsonString();
    var ruleId = s.GetSettings().Rules[0].Id;
    Check(s.Test(ruleId, 2).Ok, "test accepted");
    Check(await Until(() => wled.Segment(1)["col"]![0]!.ToJsonString() == "[0,255,0]"), "70 % of the mic range (-12 dB) is green");
    Check(s.Test(null, null).Ok && await Until(() => wled.Segment(1).ToJsonString() == before), "test ended, restored");

    var cid = s.GetSettings().Controllers[0].Id;
    Check(s.Identify(cid, 2).Ok && await Until(() => wled.Segment(2)["fx"]!.GetValue<int>() == wled.FxId("Blink")), "identify flashes");
    Check(!s.Identify(cid, 9).Ok, "unknown segment refused");

    s.Hub.Set(Signals.Mic, -1);
    radio.SetTx(true);
    Check(await Until(() => wled.Segment(1)["col"]![0]!.ToJsonString() == "[255,0,0]"), "red while transmitting hot");
    await s.DisposeAsync();
    Check(wled.Segment(1).ToJsonString() == before, "restored before stopping");
});

Test("engine: unreachable controller and missing segment are reported on the rule", async () =>
{
    var (s, radio, wled) = await Running(rules: id => [Rule(id, "tx", 7), Rule(id, "tx", 0)]);
    await using var _ = s;
    radio.SetTx(true);
    Check(await Until(() => s.BuildStatus().Rules[0].Error?.Contains("Segment 7") == true), "missing segment");
    await wled.App.StopAsync();
    Check(await Until(() => s.BuildStatus().Controllers[0].State == "error"), "controller error");
    Check(await Until(() => s.BuildStatus().Rules[1].Error?.Contains("not connected") == true), "rule says why");
});

// ------------------------------------------------------------------ HTTP surface

Test("http: routes, validation and cross-site protection", async () =>
{
    var wled = await MockWled.StartAsync(2, true);
    var service = new WledService(NullLogger.Instance, new MemorySettingsStore(), new FakeRadioFeed());
    await service.StartAsync(CancellationToken.None);
    await using var svc = service;
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    var app = builder.Build();
    var group = app.MapGroup("/api/plugins/com.n1mjf.wled");
    WledEndpoints.Map(group, () => service);
    await app.StartAsync();
    var http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/api/plugins/com.n1mjf.wled/") };
    async Task<(int Code, JsonNode? Body)> Post(string path, string json, string? site = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (site is not null) req.Headers.Add("Sec-Fetch-Site", site);
        var res = await http.SendAsync(req);
        return ((int)res.StatusCode, JsonNode.Parse(await res.Content.ReadAsStringAsync()));
    }

    var settings = JsonNode.Parse(await http.GetStringAsync("settings"))!;
    Check(settings["catalog"]!["signals"]!.AsArray().Count == Signals.All.Count && settings["catalog"]!["maxControllers"]!.GetValue<int>() == 4, "catalog");
    var (code, body) = await Post("controllers", $"{{\"controllers\":[{{\"host\":\"{wled.Host}\",\"name\":\"Shack\"}}]}}", "cross-site");
    Check(code == 403, "cross-site refused");
    var res = await http.PostAsync("controllers", new StringContent("{}", Encoding.UTF8, "text/plain"));
    Check((int)res.StatusCode == 415, "json only");
    (code, body) = await Post("controllers", $"{{\"controllers\":[{{\"host\":\"{wled.Host}\",\"name\":\"Shack\"}}]}}", "same-origin");
    Check(code == 200 && body!["controllers"]![0]!["name"]!.GetValue<string>() == "Shack", "controller saved");
    var cid = body!["controllers"]![0]!["id"]!.GetValue<string>();
    Check(await Until(() => JsonNode.Parse(http.GetStringAsync("status").Result)!["controllers"]![0]!["state"]!.GetValue<string>() == "connected"), "connected");
    var effects = JsonNode.Parse(await http.GetStringAsync($"effects?controller={cid}"))!["effects"]!.AsArray();
    Check(effects.Any(e => e!.GetValue<string>() == "Percent") && !effects.Any(e => e!.GetValue<string>() == "RSVD"), "effects list");
    (code, body) = await Post("rules", $"{{\"rules\":[{{\"signal\":\"tx\",\"controller\":\"{cid}\",\"segment\":0}},{{\"signal\":\"nope\",\"controller\":\"{cid}\"}}]}}");
    Check(code == 400, "bad rule refused");
    (code, body) = await Post("rules", $"{{\"rules\":[{{\"signal\":\"ampPower\",\"controller\":\"{cid}\",\"segment\":1,\"style\":\"leds\"}}]}}");
    Check(code == 200 && body!["rules"]![0]!["gate"]!.GetValue<string>() == "tx" && body["rules"]![0]!["meter"]!["max"]!.GetValue<double>() == 1300, "rule saved with defaults");
    (code, body) = await Post("sources", "{\"ampEnabled\":true}");
    Check(code == 400 && body!["message"]!.GetValue<string>().Contains("Zeus address"), "amp needs the zeus address");
    (code, body) = await Post("sources", "{\"ampEnabled\":true,\"zeusUrl\":\"http://127.0.0.1:9\",\"tciEnabled\":false}");
    Check(code == 200, "sources saved");
    (code, body) = await Post("identify", $"{{\"controller\":\"{cid}\",\"segment\":1}}");
    Check(code == 200, "identify");
    (code, body) = await Post("controllers", "{\"controllers\":[]}");
    Check(code == 200 && service.GetSettings().Rules.Count == 0, "rules go with their controller");
    var status = JsonNode.Parse(await http.GetStringAsync("status"))!;
    Check(status["signals"]!.AsArray().Count == Signals.All.Count && status["sources"]!["amp"]!["enabled"]!.GetValue<bool>(), "status shape");
    await app.StopAsync();
    await wled.App.StopAsync();
});

Test("http: routes answer 503 before the feature is running", async () =>
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    var app = builder.Build();
    WledEndpoints.Map(app.MapGroup("/p"), () => null);
    await app.StartAsync();
    var http = new HttpClient { BaseAddress = new Uri(app.Urls.First() + "/p/") };
    Check((int)(await http.GetAsync("status")).StatusCode == 503, "status 503");
    var res = await http.PostAsync("rules", new StringContent("{\"rules\":[]}", Encoding.UTF8, "application/json"));
    Check((int)res.StatusCode == 503, "rules 503");
    await app.StopAsync();
});

Test("settings: saved settings survive a restart", async () =>
{
    var wled = await MockWled.StartAsync(2, true);
    var store = new MemorySettingsStore();
    var s = new WledService(NullLogger.Instance, store, new FakeRadioFeed());
    await s.StartAsync(CancellationToken.None);
    var (_, cs) = await s.SetControllersAsync([new ControllerInput(null, "Desk", wled.Host, true, 20, true)], CancellationToken.None);
    await s.SetRulesAsync([Rule(cs![0].Id, "smeter", 0, "leds")], CancellationToken.None);
    await s.DisposeAsync();
    // Through the JSON the plugin store uses.
    var round = Validate.Deserialize<List<Rule>>(Validate.Serialize(store.LoadAsync(default).Result.Rules))!;
    Check(round[0].Meter!.Min == -121 && round[0].Style == "leds", "rule round-trips");
    var s2 = new WledService(NullLogger.Instance, store, new FakeRadioFeed());
    await s2.StartAsync(CancellationToken.None);
    await using var _ = s2;
    Check(s2.GetSettings().Rules.Count == 1 && s2.GetSettings().Controllers[0].Name == "Desk", "reloaded");
    await wled.App.StopAsync();
});

// ------------------------------------------------------------------ run

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(30));
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}\n      {ex.Message}");
    }
}
Console.WriteLine();
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

// ================================================================== mocks

/// <summary>Just enough of WLED's JSON API: /json/eff, /json/si, POST /json/state.</summary>
sealed class MockWled
{
    public static readonly string[] EffectNames =
        ["Solid", "Blink", "Breathe", "Wipe", "Wipe Random", "Random Colors", "Sweep", "Dynamic", "RSVD", "Percent", "-", "Fire 2012"];

    private readonly object _gate = new();
    private readonly JsonObject _state;
    private readonly Dictionary<int, string> _leds = [];
    private readonly List<string> _posts = [];
    public List<string> Posts { get { lock (_gate) return [.. _posts]; } }
    public required WebApplication App { get; init; }
    public required string Host { get; init; }

    private MockWled(int segments, bool lightOn)
    {
        var segs = new JsonArray();
        for (var i = 0; i < segments; i++)
        {
            segs.Add(new JsonObject
            {
                ["id"] = i, ["start"] = i * 10, ["stop"] = i * 10 + 10, ["len"] = 10, ["grp"] = 1, ["spc"] = 0,
                ["on"] = true, ["bri"] = 255, ["frz"] = false, ["n"] = $"Strip {i}",
                ["col"] = new JsonArray(new JsonArray(0, 0, 255), new JsonArray(0, 0, 0), new JsonArray(0, 0, 0)),
                ["fx"] = 7, ["sx"] = 100, ["ix"] = 90, ["pal"] = 3, ["o1"] = false,
            });
        }
        _state = new JsonObject { ["on"] = lightOn, ["bri"] = 128, ["seg"] = segs };
    }

    public int FxId(string name) => Array.IndexOf(EffectNames, name);
    public bool LightOn { get { lock (_gate) return _state["on"]!.GetValue<bool>(); } }
    public JsonObject Segment(int id) { lock (_gate) return (JsonObject)_state["seg"]![id]!.DeepClone(); }
    public string? Leds(int id) { lock (_gate) return _leds.GetValueOrDefault(id); }

    public static async Task<MockWled> StartAsync(int segments, bool lightOn)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        MockWled? self = null;
        app.MapGet("/json/eff", () => Results.Text(JsonSerializer.Serialize(EffectNames), "application/json"));
        app.MapGet("/json/si", () =>
        {
            lock (self!._gate)
                return Results.Text(new JsonObject { ["state"] = self._state.DeepClone(), ["info"] = new JsonObject { ["name"] = "Mock", ["ver"] = "0.15.0", ["leds"] = new JsonObject { ["count"] = segments * 10 } } }.ToJsonString(), "application/json");
        });
        app.MapPost("/json/state", async (HttpContext ctx) =>
        {
            var text = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            self!.Apply(text);
            return Results.Text("{\"success\":true}", "application/json");
        });
        await app.StartAsync();
        self = new MockWled(segments, lightOn) { App = app, Host = app.Urls.First() };
        return self;
    }

    private void Apply(string text)
    {
        var body = JsonNode.Parse(text)!.AsObject();
        lock (_gate)
        {
            _posts.Add(text);
            if (body["on"] is { } on) _state["on"] = on.GetValue<bool>();
            if (body["seg"] is not JsonArray segs) return;
            foreach (var s in segs.OfType<JsonObject>())
            {
                var id = s["id"]!.GetValue<int>();
                var target = _state["seg"]!.AsArray().OfType<JsonObject>().First(x => x["id"]!.GetValue<int>() == id);
                var any = false;
                foreach (var key in new[] { "on", "bri", "col", "fx", "sx", "ix", "pal", "frz", "o1" })
                {
                    if (s[key] is { } v) { target[key] = v.DeepClone(); any = true; }
                }
                if (any && s["frz"] is null) target["frz"] = false;   // any property change unfreezes
                if (s["i"] is JsonArray leds) { _leds[id] = leds.ToJsonString(); target["frz"] = true; }
            }
        }
    }
}

/// <summary>A TCI server: says ready, records what it is sent, can push text and drop the client.</summary>
sealed class MockTci : IAsyncDisposable
{
    private WebApplication _app = null!;
    private WebSocket? _ws;
    private readonly StringBuilder _received = new();
    public int Port { get; private set; }
    public string Received { get { lock (_received) return _received.ToString(); } }

    public static async Task<MockTci> StartAsync()
    {
        var self = new MockTci();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            self._ws = ws;
            await ws.SendAsync(Encoding.UTF8.GetBytes("protocol:Zeus,2.0;trx_count:1;ready;"), WebSocketMessageType.Text, true, default);
            var buf = new byte[4096];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf, default);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    lock (self._received) self._received.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                }
            }
            catch (WebSocketException) { }
        });
        await app.StartAsync();
        self._app = app;
        self.Port = new Uri(app.Urls.First()).Port;
        return self;
    }

    public Task SendAsync(string text) => _ws!.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, default);

    public Task DropAsync()
    {
        _ws?.Abort();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _app.StopAsync();
}

/// <summary>The amplifier feature's status route, as Zeus serves it.</summary>
sealed class MockAmp
{
    private JsonObject _status = new();
    public required WebApplication App { get; init; }
    public required string Url { get; init; }

    public void Set(bool connected, bool fresh, double forward, double? swr)
    {
        _status = new JsonObject
        {
            ["connection"] = new JsonObject { ["state"] = connected ? "connected" : "disconnected" },
            ["meters"] = new JsonObject { ["fresh"] = fresh, ["forwardW"] = forward, ["reflectedW"] = 2.0, ["swr"] = swr },
            ["limits"] = new JsonObject { ["forwardW"] = 1300.0 },
        };
    }

    public static async Task<MockAmp> StartAsync(bool installed = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        MockAmp? self = null;
        if (installed) app.MapGet($"/api/plugins/{AmpSource.AmpPluginId}/status", () => Results.Text(self!._status.ToJsonString(), "application/json"));
        await app.StartAsync();
        self = new MockAmp { App = app, Url = app.Urls.First() };
        self.Set(false, false, 0, null);
        return self;
    }
}

/// <summary>
/// Runs the backend with two mock WLED controllers and a simulated radio, and serves the panel
/// harness, for trying the UI without Zeus or real lights. /dev/tx?on=true, /dev/freq?hz=7074000,
/// /dev/signal?id=smeter&amp;value=-70 drive it; /dev/strips shows what the mock strips look like.
/// </summary>
static class DevHost
{
    public static async Task RunAsync(string root)
    {
        var desk = await MockWled.StartAsync(3, true);
        var shelf = await MockWled.StartAsync(1, false);
        var radio = new FakeRadioFeed();
        var store = new MemorySettingsStore();
        var service = new WledService(NullLogger.Instance, store, radio);
        await service.StartAsync(CancellationToken.None);
        await service.SetSourcesAsync(new SourceInput(false, null, null, null, false, null), CancellationToken.None);
        var (_, cs) = await service.SetControllersAsync([
            new ControllerInput(null, "Desk strip", desk.Host, true, 20, true),
            new ControllerInput(null, "Shelf", shelf.Host, true, 20, true),
        ], CancellationToken.None);
        var d = cs![0].Id;
        var sh = cs[1].Id;
        await service.SetRulesAsync([
            new RuleInput(null, "On air", true, "tx", sh, -1, "look", null, new LookInput("FF0000", null, 255, 128, 128), null, null),
            new RuleInput(null, "Power", true, "radioPower", d, 0, "leds", "tx", null, null, null),
            new RuleInput(null, "SWR", true, "radioSwr", d, 1, "leds", "tx", null, null, null),
            new RuleInput(null, "S-meter", true, "smeter", d, 0, "percent", "rx", null, null, null),
            new RuleInput(null, "Band", true, "band", d, 2, "band", null, null, null, null),
        ], CancellationToken.None);
        radio.SetFrequency(14_074_000);
        radio.SetTx(false);
        service.Hub.Set(Signals.SMeter, -79);
        service.Hub.SetFlag(Signals.Tune, false);
        service.Hub.Set(Signals.Mic, null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:5199");
        var app = builder.Build();
        WledEndpoints.Map(app.MapGroup("/api/plugins/com.n1mjf.wled"), () => service);
        var dir = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? "tests", root));
        app.MapGet("/", () => Results.Redirect("/index.html"));
        app.MapGet("/dev/tx", (bool on) =>
        {
            radio.SetTx(on);
            service.Hub.Set(Signals.RadioPower, on ? 72 : 0);
            service.Hub.Set(Signals.RadioSwr, on ? 1.6 : null);
            service.Hub.Set(Signals.Mic, on ? -9 : null);
            return "ok";
        });
        app.MapGet("/dev/freq", (long hz) => { radio.SetFrequency(hz); return "ok"; });
        app.MapGet("/dev/signal", (string id, double? value) => { service.Hub.Set(id, value); return "ok"; });
        app.MapGet("/dev/strips", () => Results.Text(desk.Segment(0).ToJsonString() + "\n" + desk.Segment(1).ToJsonString() + "\n" + desk.Leds(0) + "\n" + desk.Leds(1), "text/plain"));
        app.MapGet("/{*file}", (string file) =>
        {
            var path = Path.GetFullPath(Path.Combine(dir, file));
            if (!path.StartsWith(dir, StringComparison.Ordinal) || !File.Exists(path)) return Results.NotFound();
            var type = path.EndsWith(".js") ? "text/javascript" : path.EndsWith(".html") ? "text/html" : "application/octet-stream";
            return Results.File(path, type);
        });
        Console.WriteLine($"Dev host on http://127.0.0.1:5199 (harness from {dir})");
        await app.RunAsync();
    }
}
