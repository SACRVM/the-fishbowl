using Fishbowl.Core.Repositories;
using Fishbowl.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Fishbowl.Scheduler;

public static class ServiceCollectionExtensions
{
    // Wires the reminder + daily-digest dispatchers and their repos. Caller
    // must have already registered DatabaseFactory,
    // INotificationChannelRepository, IEventRepository, ITodoRepository,
    // ISystemRepository, and at least one IBotClient (both dispatchers are
    // silent no-ops without one — useful for installs without chat-platform
    // integration). The digest additionally needs Digest:Enabled=true in
    // system config; it defaults to off.
    public static IServiceCollection AddFishbowlScheduler(this IServiceCollection services)
    {
        services.AddScoped<IReminderRepository, ReminderRepository>();
        // When the jobs last ran — the admin's System page reads it.
        services.AddSingleton<Fishbowl.Core.SchedulerStatus>();
        services.AddHostedService<ReminderDispatcher>();
        services.AddHostedService<DailyDigestDispatcher>();
        // Needs IFileService (registered by the host).
        services.AddHostedService<FilesMaintenanceService>();
        // Needs the mail services (registered by the host).
        services.AddHostedService<MailSyncService>();
        return services;
    }
}
