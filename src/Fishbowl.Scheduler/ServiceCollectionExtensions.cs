using Fishbowl.Core.Repositories;
using Fishbowl.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Fishbowl.Scheduler;

public static class ServiceCollectionExtensions
{
    // Wires the reminder dispatcher and its repos. Caller must have already
    // registered DatabaseFactory, INotificationChannelRepository,
    // IEventRepository, ISystemRepository, and at least one IBotClient (the
    // dispatcher is a silent no-op without one — useful for installs without
    // chat-platform integration).
    public static IServiceCollection AddFishbowlScheduler(this IServiceCollection services)
    {
        services.AddScoped<IReminderRepository, ReminderRepository>();
        services.AddHostedService<ReminderDispatcher>();
        return services;
    }
}
