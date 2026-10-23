using Microsoft.Extensions.DependencyInjection;
using Plooto.Ap.Messaging.Channels;

namespace Plooto.Ap.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>Registers the local outbox + channel backbone. Requires ApDbContext to be registered by the host.</summary>
    public static IServiceCollection AddApMessaging(this IServiceCollection services, Action<OutboxOptions>? configure = null)
    {
        services.AddOptions<OutboxOptions>();
        if (configure is not null) services.Configure(configure);
        services.AddSingleton<ChannelRegistry>();
        services.AddSingleton<IMessageTransport>(sp => sp.GetRequiredService<ChannelRegistry>());
        services.AddScoped<IEventPublisher, OutboxEventPublisher>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
        services.AddSingleton<CdcProjector>();
        services.AddHostedService(sp => sp.GetRequiredService<CdcProjector>());
        return services;
    }
}
