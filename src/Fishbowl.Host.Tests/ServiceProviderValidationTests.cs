using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fishbowl.Host.Tests;

// The Development host validates the container at start (scopes and every
// registration); the Testing one doesn't. A singleton that takes a scoped
// service passes every test and then refuses to start in dev — this builds
// the container the way Development does, so such a registration fails here.
public class ServiceProviderValidationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ServiceProviderValidationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder
            .UseEnvironment("Testing")
            .UseDefaultServiceProvider(o => { o.ValidateScopes = true; o.ValidateOnBuild = true; }));
    }

    [Fact]
    public void Container_ValidatesLikeDevelopment_Test()
    {
        using var client = _factory.CreateClient();
        Assert.NotNull(_factory.Services.GetService<Fishbowl.Data.Mail.MailCredentials>());
    }
}
