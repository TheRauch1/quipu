using System.Security.Cryptography;
using System.Text;
using Quipu.Config;

namespace Quipu.Api;

/// <summary>
/// API key check (when server.apiKey is set) and a cross-origin guard: browsers attach an Origin header
/// to cross-site requests, so a random web page can't drive the local API (start downloads, read tokens…).
/// </summary>
public sealed class SecurityMiddleware(RequestDelegate next, ConfigStore config)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var denied = NetworkAccess.Check(ctx.Connection.RemoteIpAddress, config.Current.Server)
                     ?? (NetworkAccess.IsAllowedHostName(ctx.Request.Host.Host) ? null : NetworkAccess.DeniedHostName);
        if (denied is not null)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = denied });
            return;
        }

        var path = ctx.Request.Path;
        var guarded = path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp");
        if (!guarded)
        {
            await next(ctx);
            return;
        }

        if (ctx.Request.Headers.Origin is { Count: > 0 } origin && !IsSameOrigin(origin.ToString(), ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "Cross-origin requests are not allowed" });
            return;
        }

        var key = config.Current.Server.ApiKey;
        if (!string.IsNullOrEmpty(key))
        {
            var supplied = ctx.Request.Headers["X-Api-Key"].ToString();
            if (string.IsNullOrEmpty(supplied) && ctx.Request.Headers.Authorization.ToString() is { } auth
                && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                supplied = auth[7..].Trim();
            if (!FixedEquals(supplied, key))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "API key required (X-Api-Key header or Bearer token)" });
                return;
            }
        }
        await next(ctx);
    }

    private static bool IsSameOrigin(string origin, HttpRequest req) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var o) &&
        string.Equals($"{o.Host}:{o.Port}", $"{req.Host.Host}:{req.Host.Port ?? (req.IsHttps ? 443 : 80)}", StringComparison.OrdinalIgnoreCase);

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>Maps service exceptions to clean JSON errors.</summary>
public sealed class ErrorMiddleware(RequestDelegate next, ILogger<ErrorMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (Exception ex) when (!ctx.Response.HasStarted && ex is not OperationCanceledException)
        {
            var (status, errors) = ex switch
            {
                ConfigValidationException cv => (400, cv.Errors),
                YamlDotNet.Core.YamlException ye => (400, (IReadOnlyList<string>)[$"YAML error at line {ye.Start.Line}: {ye.InnerException?.Message ?? ye.Message}"]),
                KeyNotFoundException => (404, null),
                ArgumentException => (400, null),
                FileNotFoundException => (409, null),
                InvalidOperationException => (409, null),
                System.Text.Json.JsonException => (400, null),
                BadHttpRequestException b => (b.StatusCode, b.InnerException is System.Text.Json.JsonException je
                    ? (IReadOnlyList<string>)[$"Invalid JSON: {je.Message}"] : null),
                HttpRequestException => (502, null),
                _ => (500, null),
            };
            if (status >= 500) log.LogError(ex, "Unhandled error on {Path}", ctx.Request.Path);
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(new ApiError(errors is { Count: > 0 } ? string.Join("; ", errors) : ex.Message, errors));
        }
    }
}

public sealed record ApiError(string Error, IReadOnlyList<string>? Details = null);
