using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Quipu.Api;
using Quipu.Config;

namespace Quipu.Tests;

public sealed class SecurityMiddlewareTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("quipu-mw-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Sends one request from this PC through the middleware; 200 = it reached the endpoint.</summary>
    private async Task<int> Send(string? configuredKey = null, string host = "localhost:5250", string? origin = null, string? key = null)
    {
        var file = Path.Combine(_dir, "quipu.yaml");
        File.WriteAllText(file, $"paths: {{ models: {_dir}, installs: {_dir} }}\n" + (configuredKey is null ? "" : $"server: {{ apiKey: {configuredKey} }}\n"));
        using var store = new ConfigStore(file, NullLogger.Instance);
        var reached = false;
        var middleware = new SecurityMiddleware(_ => { reached = true; return Task.CompletedTask; }, store);

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/api/models/x/start";
        ctx.Request.Host = new HostString(host);
        if (origin is not null) ctx.Request.Headers.Origin = origin;
        if (key is not null) ctx.Request.Headers["X-Api-Key"] = key;
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);
        return reached ? 200 : ctx.Response.StatusCode;
    }

    [Fact]
    public async Task Dashboard_on_the_same_origin_passes() => Assert.Equal(200, await Send(origin: "http://localhost:5250"));

    [Fact]
    public async Task Other_website_is_refused() => Assert.Equal(403, await Send(origin: "http://evil.example.com"));

    [Fact]
    public async Task Website_rebinding_its_name_to_localhost_is_refused() =>
        Assert.Equal(403, await Send(host: "evil.example.com:5250", origin: "http://evil.example.com:5250"));

    [Theory]
    [InlineData(null, 401)]
    [InlineData("wrong", 401)]
    [InlineData("s3cret", 200)]
    public async Task Api_key_is_required_when_set(string? key, int expected) =>
        Assert.Equal(expected, await Send(configuredKey: "s3cret", key: key));
}
