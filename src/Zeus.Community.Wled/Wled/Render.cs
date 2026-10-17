// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;

namespace Zeus.Community.Wled.Wled;

/// <summary>
/// Turns looks and meter readings into WLED JSON segment members. Pure functions, so every
/// payload the engine can send is covered by tests without a controller.
/// </summary>
internal static class Render
{
    public const string PeakColor = "FFFFFF";

    public static string Rgb(string hex)
    {
        var (r, g, b) = Validate.Rgb(hex);
        return $"[{r},{g},{b}]";
    }

    public static double Fraction(double value, MeterSpec m) => Math.Clamp((value - m.Min) / (m.Max - m.Min), 0, 1);

    public static string Zone(double value, MeterSpec m)
    {
        if (m.Warn is not { } w || m.Bad is not { } b) return "ok";
        if (m.LowIsBad) return value <= b ? "bad" : value <= w ? "warn" : "ok";
        return value >= b ? "bad" : value >= w ? "warn" : "ok";
    }

    public static string ZoneColor(string zone, MeterSpec m) => zone switch
    {
        "bad" => m.BadColor,
        "warn" => m.WarnColor,
        _ => m.OkColor,
    };

    /// <summary>A plain look: colour, effect and sliders.</summary>
    public static string Look(Look l, int fx) =>
        $"\"on\":true,\"bri\":{l.Brightness},\"col\":[{Rgb(l.Color)},[0,0,0],[0,0,0]],\"fx\":{fx},\"sx\":{l.Speed},\"ix\":{l.Intensity},\"pal\":0,\"frz\":false";

    /// <summary>
    /// A bar using WLED's own Percent effect: intensity is the fill (0-100), the primary colour the
    /// lit part (coloured by zone), the secondary colour the unlit part. Speed 255 makes it jump
    /// straight to the value; smoothing is done here instead.
    /// </summary>
    public static string Percent(MeterSpec m, double value, int percentFx)
    {
        var pct = (int)Math.Round(Fraction(value, m) * 100);
        var color = ZoneColor(Zone(value, m), m);
        return $"\"on\":true,\"bri\":{m.Brightness},\"col\":[{Rgb(color)},{Rgb(m.OffColor)},[0,0,0]],\"fx\":{percentFx},\"sx\":255,\"ix\":{pct},\"pal\":0,\"o1\":true,\"frz\":false";
    }

    /// <summary>The whole segment in the colour of the reading's zone.</summary>
    public static string ZoneFill(MeterSpec m, double value)
    {
        var color = ZoneColor(Zone(value, m), m);
        return $"\"on\":true,\"bri\":{m.Brightness},\"col\":[{Rgb(color)},[0,0,0],[0,0,0]],\"fx\":0,\"pal\":0,\"frz\":false";
    }

    /// <summary>Set-up for a per-LED bar: on, brightness, Solid. The LEDs themselves follow in <see cref="Leds"/>.</summary>
    public static string LedSetup(MeterSpec m) =>
        $"\"on\":true,\"bri\":{m.Brightness},\"col\":[{Rgb(m.OffColor)},[0,0,0],[0,0,0]],\"fx\":0,\"pal\":0,\"frz\":false";

    /// <summary>Number of lit LEDs for a reading on a segment of this (virtual) length.</summary>
    public static int Lit(double value, MeterSpec m, int length) =>
        Math.Clamp((int)Math.Round(Fraction(value, m) * length), 0, length);

    /// <summary>
    /// Per-LED bar, as runs ("i":[start,stop,"RRGGBB",...]). Each lit LED takes the colour of the zone
    /// its position stands for, like the amplifier panel's banded bars; the unlit part takes the off
    /// colour; an optional peak LED is drawn in white.
    /// </summary>
    public static string Leds(MeterSpec m, double value, double? peak, int length)
    {
        var colors = new string[length];
        var lit = Lit(value, m, length);
        for (var i = 0; i < length; i++)
        {
            if (i >= lit) { colors[i] = m.OffColor; continue; }
            var at = m.Min + (m.Max - m.Min) * ((i + 0.5) / length);
            colors[i] = ZoneColor(Zone(at, m), m);
        }
        if (peak is { } p && m.PeakHold)
        {
            var pi = Lit(p, m, length) - 1;
            if (pi >= lit && pi >= 0) colors[pi] = PeakColor;
        }

        var sb = new StringBuilder("\"i\":[");
        var first = true;
        for (var start = 0; start < length;)
        {
            var end = start + 1;
            while (end < length && colors[end] == colors[start]) end++;
            if (!first) sb.Append(',');
            first = false;
            sb.Append(start.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(end.ToString(CultureInfo.InvariantCulture)).Append(",\"").Append(colors[start]).Append('"');
            start = end;
        }
        return sb.Append(']').ToString();
    }
}
