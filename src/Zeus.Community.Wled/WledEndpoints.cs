// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Zeus.Community.Wled;

internal sealed record ControllersRequest(List<ControllerInput>? Controllers);

internal sealed record RulesRequest(List<RuleInput>? Rules);

internal sealed record IdentifyRequest(string? Controller, int? Segment);

internal sealed record TestRequest(string? Rule, int? Seconds);

/// <summary>
/// HTTP surface, mounted by Zeus under /api/plugins/&lt;id&gt;/. State-changing routes refuse
/// cross-site browser requests and anything that is not JSON, the same rules as the amplifier
/// and Home Assistant features.
/// </summary>
internal static class WledEndpoints
{
    private const int MaxBodyBytes = 4096;
    private const int MaxRulesBytes = 96 * 1024;

    public static void Map(IEndpointRouteBuilder endpoints, Func<WledService?> service)
    {
        endpoints.MapGet("status", (HttpContext ctx) =>
            With(ctx, service, s => Results.Json(s.BuildStatus(), Validate.Json)));

        endpoints.MapGet("settings", (HttpContext ctx) =>
            With(ctx, service, s =>
            {
                var c = s.GetSettings();
                return Results.Json(new { controllers = c.Controllers, sources = c.Sources, rules = c.Rules, catalog = WledService.Catalog() }, Validate.Json);
            }));

        endpoints.MapGet("effects", (HttpContext ctx, string? controller) =>
            With(ctx, service, s => s.Effects(controller) is { } list
                ? Results.Json(new { effects = list }, Validate.Json)
                : Refuse(404, "Unknown controller")));

        endpoints.MapPost("controllers", async (HttpContext ctx) =>
        {
            var s = service();
            if (s is null) return Unavailable();
            var (body, error) = await ReadJsonAsync<ControllersRequest>(ctx);
            if (error is not null) return error;
            var (result, controllers) = await s.SetControllersAsync(body!.Controllers, ctx.RequestAborted);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { ok = result.Ok, message = result.Message, controllers }, Validate.Json, statusCode: result.Status);
        });

        endpoints.MapPost("sources", async (HttpContext ctx) =>
        {
            var s = service();
            if (s is null) return Unavailable();
            var (body, error) = await ReadJsonAsync<SourceInput>(ctx);
            if (error is not null) return error;
            return Reply(ctx, await s.SetSourcesAsync(body, ctx.RequestAborted));
        });

        endpoints.MapPost("rules", async (HttpContext ctx) =>
        {
            var s = service();
            if (s is null) return Unavailable();
            var (body, error) = await ReadJsonAsync<RulesRequest>(ctx, MaxRulesBytes);
            if (error is not null) return error;
            var (result, rules) = await s.SetRulesAsync(body!.Rules, ctx.RequestAborted);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { ok = result.Ok, message = result.Message, rules }, Validate.Json, statusCode: result.Status);
        });

        endpoints.MapPost("identify", async (HttpContext ctx) =>
        {
            var s = service();
            if (s is null) return Unavailable();
            var (body, error) = await ReadJsonAsync<IdentifyRequest>(ctx);
            if (error is not null) return error;
            return Reply(ctx, s.Identify(body!.Controller, body.Segment));
        });

        endpoints.MapPost("test", async (HttpContext ctx) =>
        {
            var s = service();
            if (s is null) return Unavailable();
            var (body, error) = await ReadJsonAsync<TestRequest>(ctx);
            if (error is not null) return error;
            return Reply(ctx, s.Test(body!.Rule, body.Seconds));
        });
    }

    private static IResult With(HttpContext ctx, Func<WledService?> service, Func<WledService, IResult> action)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        var s = service();
        return s is null ? Unavailable() : action(s);
    }

    private static IResult Unavailable() =>
        Results.Json(new { ok = false, message = "The WLED feature is not running" }, statusCode: 503);

    private static IResult Reply(HttpContext ctx, CommandResult result)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { ok = result.Ok, message = result.Message }, statusCode: result.Status);
    }

    private static IResult Refuse(int status, string message) =>
        Results.Json(new { ok = false, message }, statusCode: status);

    private static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpContext ctx, int maxBytes = MaxBodyBytes)
    {
        // The Zeus page may be served from another port of the same host (same-site), which is accepted;
        // a request started by another site is refused. JSON-only bodies add a second layer, since a
        // cross-origin JSON POST needs a CORS preflight that is never granted.
        var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site != "same-origin" && site != "same-site" && site != "none")
            return (default, Refuse(403, "Cross-site requests are refused"));

        if (!ctx.Request.HasJsonContentType())
            return (default, Refuse(415, "Content-Type must be application/json"));

        if (ctx.Request.ContentLength > maxBytes)
            return (default, Refuse(413, "Request body too large"));
        var limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = maxBytes;

        try
        {
            var value = await ctx.Request.ReadFromJsonAsync<T>(Validate.Json, ctx.RequestAborted);
            return value is null ? (default, Refuse(400, "Empty request")) : (value, null);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidOperationException)
        {
            return (default, ex is BadHttpRequestException { StatusCode: 413 }
                ? Refuse(413, "Request body too large")
                : Refuse(400, "Malformed request"));
        }
    }
}
