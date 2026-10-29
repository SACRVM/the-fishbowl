using Fishbowl.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fishbowl.Host.Tests;

// "Connect an agent": which hosts a claude.ai connector can't reach, and the
// address a program should use (ClientAddresses).
public class ClientAddressesTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("fishbowl.localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("10.0.0.2")]
    [InlineData("172.20.1.1")]
    [InlineData("192.168.1.5")]
    [InlineData("169.254.3.4")]
    [InlineData("100.100.1.1")]
    [InlineData("[fd00::1]")]
    [InlineData("[fe80::1]")]
    [InlineData("nas")]
    [InlineData("fishbowl.local")]
    [InlineData("box.lan")]
    [InlineData("box.home.arpa")]
    [InlineData("FISHBOWL.LAN.")]
    public void IsLocal_OnlyThisComputerOrNetworkReaches(string host) =>
        Assert.True(ClientAddresses.IsLocal(host));

    [Theory]
    [InlineData("fishbowl.example.com")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("[2001:db8::1]")]
    public void IsLocal_PublicNamesAndAddresses_AreNot(string host) =>
        Assert.False(ClientAddresses.IsLocal(host));

    [Theory]
    [InlineData("https", "fishbowl.example.com", true)]
    [InlineData("http", "fishbowl.example.com", false)]
    [InlineData("https", "localhost:7180", false)]
    [InlineData("https", "192.168.1.5", false)]
    public void ConnectorPossible_OnlyOverHttpsToAPublicHost(string scheme, string host, bool expected) =>
        Assert.Equal(expected, ClientAddresses.ConnectorPossible(scheme, new HostString(host)));

    [Fact]
    public void ProgramOrigin_DevHost_IsThePlainHttpPort()
    {
        var bound = new[] { "https://localhost:7180", "http://localhost:7181" };
        Assert.Equal("http://localhost:7181", ClientAddresses.ProgramOrigin("https", new HostString("localhost:7180"), bound));
        Assert.Equal("http://[::1]:7181", ClientAddresses.ProgramOrigin("https", new HostString("[::1]:7180"), ["https://[::]:7180", "http://[::]:7181"]));
        Assert.Equal("http://127.0.0.1:7181", ClientAddresses.ProgramOrigin("https", new HostString("127.0.0.1:7180"), ["https://*:7180", "http://*:7181"]));
    }

    [Fact]
    public void ProgramOrigin_Otherwise_IsTheRequestsOwn()
    {
        var bound = new[] { "https://localhost:7180", "http://localhost:7181" };
        // A public host: its certificate is a real one.
        Assert.Equal("https://fishbowl.example.com", ClientAddresses.ProgramOrigin("https", new HostString("fishbowl.example.com"), bound));
        // Already plain http.
        Assert.Equal("http://127.0.0.1:5000", ClientAddresses.ProgramOrigin("http", new HostString("127.0.0.1:5000"), bound));
        // Loopback https without a plain http bind.
        Assert.Equal("https://localhost:7180", ClientAddresses.ProgramOrigin("https", new HostString("localhost:7180"), ["https://localhost:7180"]));
        // An http bind that isn't on loopback isn't this computer's.
        Assert.Equal("https://localhost:7180", ClientAddresses.ProgramOrigin("https", new HostString("localhost:7180"), ["http://192.168.1.5:80"]));
    }
}
