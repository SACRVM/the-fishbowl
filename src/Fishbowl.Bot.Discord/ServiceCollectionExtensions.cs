using Fishbowl.Bot.Discord.Commands;
using Fishbowl.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Fishbowl.Bot.Discord;

public static class ServiceCollectionExtensions
{
    // Adds the Discord bot to the host. Caller is responsible for registering
    // the underlying repos (INotificationChannelRepository, IDiscordLinkRepository,
    // ISystemRepository, INoteRepository) and ISearchService — those live in
    // Fishbowl.Data and are already wired by Program.cs. Token binding is also
    // the caller's job (see AddOptions<DiscordBotOptions>().Configure(...)).
    public static IServiceCollection AddFishbowlDiscordBot(this IServiceCollection services)
    {
        services.AddSingleton<DiscordBotClient>();
        services.AddSingleton<IBotClient>(sp => sp.GetRequiredService<DiscordBotClient>());

        // Resolver is per-scope because it depends on a scoped ISystemRepository.
        services.AddScoped<DiscordUserResolver>();

        // One registration per handler so GetServices<ISlashCommandHandler>()
        // returns the full set. Handlers are scoped — they hold scoped repos.
        services.AddScoped<ISlashCommandHandler, LinkCommandHandler>();
        services.AddScoped<ISlashCommandHandler, UnlinkCommandHandler>();
        services.AddScoped<ISlashCommandHandler, RememberCommandHandler>();
        services.AddScoped<ISlashCommandHandler, SearchCommandHandler>();
        services.AddScoped<ISlashCommandHandler, RecentCommandHandler>();
        services.AddScoped<ISlashCommandHandler, UpcomingCommandHandler>();
        services.AddScoped<ISlashCommandHandler, HelpCommandHandler>();

        services.AddHostedService<DiscordBotHostedService>();

        return services;
    }
}
