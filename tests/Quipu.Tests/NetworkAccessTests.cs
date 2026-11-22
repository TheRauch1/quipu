using System.Net;
using Quipu.Config;

namespace Quipu.Tests;

public class NetworkAccessTests
{
    private static ServerConfig Server(string listen = "http://0.0.0.0:5250", string? apiKey = null, params string[] networks) =>
        new() { Listen = listen, ApiKey = apiKey, AllowedNetworks = [.. networks] };

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Localhost_is_always_allowed(string ip) =>
        Assert.Null(NetworkAccess.Check(IPAddress.Parse(ip), Server()));

    [Fact]
    public void Remote_is_refused_without_networks_or_key() =>
        Assert.Equal(NetworkAccess.DeniedNoRule, NetworkAccess.Check(IPAddress.Parse("10.0.0.5"), Server()));

    [Fact]
    public void Api_key_alone_lets_remote_through_to_the_key_check() =>
        Assert.Null(NetworkAccess.Check(IPAddress.Parse("10.0.0.5"), Server(apiKey: "secret")));

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("::ffff:10.0.0.200", true)]
    [InlineData("10.0.1.5", false)]
    [InlineData("192.168.1.5", false)]
    [InlineData("fe80::1", false)]
    public void Allowed_networks_decide_for_remote_clients(string ip, bool allowed)
    {
        var result = NetworkAccess.Check(IPAddress.Parse(ip), Server(apiKey: "secret", networks: "10.0.0.0/24"));
        Assert.Equal(allowed, result is null);
    }

    [Fact]
    public void Several_networks()
    {
        var s = Server(networks: ["10.0.0.0/24", "192.168.178.0/24"]);
        Assert.Null(NetworkAccess.Check(IPAddress.Parse("192.168.178.20"), s));
        Assert.NotNull(NetworkAccess.Check(IPAddress.Parse("192.168.1.20"), s));
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.181", true)]
    [InlineData("[::1]", true)]
    [InlineData("evil.example.com", false)]
    [InlineData("localhost.evil.example.com", false)]
    public void Only_ip_addresses_localhost_and_this_pc_pass_as_host_name(string host, bool allowed) =>
        Assert.Equal(allowed, NetworkAccess.IsAllowedHostName(host));

    [Fact]
    public void This_pcs_own_name_passes() => Assert.True(NetworkAccess.IsAllowedHostName(Environment.MachineName));

    [Theory]
    [InlineData("http://127.0.0.1:5250", false)]
    [InlineData("http://localhost:5250", false)]
    [InlineData("http://0.0.0.0:5250", true)]
    [InlineData("http://10.0.0.181:5250", true)]
    public void Listens_beyond_localhost(string listen, bool expected) =>
        Assert.Equal(expected, NetworkAccess.ListensBeyondLocalhost(Server(listen)));

    [Fact]
    public void Local_only_setup_has_no_warnings()
    {
        var cfg = new QuipuConfig();
        cfg.Models["a"] = new ModelProfile { File = "a.gguf" };
        Assert.Empty(NetworkAccess.Warnings(cfg));
    }

    [Fact]
    public void Warns_about_open_lan_and_exposed_model_servers()
    {
        var cfg = new QuipuConfig { Server = Server(networks: "10.0.0.0/24") };
        cfg.Defaults.Host = "0.0.0.0";
        cfg.Models["open"] = new ModelProfile { File = "a.gguf" };
        cfg.Models["keyed"] = new ModelProfile { File = "b.gguf", Args = ["--api-key", "k"] };
        cfg.Models["local"] = new ModelProfile { File = "c.gguf", Host = "127.0.0.1" };

        var warnings = NetworkAccess.Warnings(cfg);

        Assert.Equal(2, warnings.Count);
        Assert.Contains("10.0.0.0/24", warnings[0]);
        Assert.Contains("(open)", warnings[1]);
    }

    [Fact]
    public void Warns_that_lan_listen_without_rules_refuses_others()
    {
        var cfg = new QuipuConfig { Server = Server() };
        Assert.Contains(NetworkAccess.Warnings(cfg), w => w.Contains("refused"));
    }
}
