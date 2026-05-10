using Fishbowl.Core.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fishbowl.Host.Configuration;

/// <summary>
/// Runs once at application startup, before the HTTP server listens.
/// Reads system_config into ConfigurationCache so options binders can
/// read synchronously.
///
/// Takes IServiceScopeFactory (singleton) and opens a per-invocation
/// scope to resolve ISystemRepository (scoped) — the standard pattern
/// for hosted services that need scoped dependencies. Without this,
/// Development-environment DI validation throws "Cannot consume scoped
/// service from singleton".
/// </summary>
public class ConfigurationInitializer : IHostedService
{
    private static readonly string[] TrackedKeys =
    {
        "Google:ClientId",
        "Google:ClientSecret",
        "Discord:BotToken",
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConfigurationCache _cache;
    private readonly ILogger<ConfigurationInitializer> _logger;

    public ConfigurationInitializer(
        IServiceScopeFactory scopeFactory,
        ConfigurationCache cache,
        ILogger<ConfigurationInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISystemRepository>();

        foreach (var key in TrackedKeys)
        {
            var value = await repo.GetConfigAsync(key, ct);
            _cache.Set(key, value);
        }
        _logger.LogInformation("Configuration cache populated with {Count} keys", TrackedKeys.Length);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
