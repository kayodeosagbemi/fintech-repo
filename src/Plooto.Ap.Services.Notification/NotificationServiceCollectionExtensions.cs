using Microsoft.Extensions.DependencyInjection;

namespace Plooto.Ap.Services.Notification;

public static class NotificationServiceCollectionExtensions
{
    public static IServiceCollection AddNotificationServices(this IServiceCollection services)
    {
        services.AddSingleton<NotificationDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<NotificationDispatcher>());
        return services;
    }
}
