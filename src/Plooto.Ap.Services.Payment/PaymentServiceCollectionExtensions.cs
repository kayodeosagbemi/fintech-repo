using Microsoft.Extensions.DependencyInjection;

namespace Plooto.Ap.Services.Payment;

public static class PaymentServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentServices(this IServiceCollection services,
        Action<PaymentProcessorOptions>? configureProcessor = null, Action<SimulatedRailOptions>? configureRail = null)
    {
        services.AddOptions<PaymentProcessorOptions>();
        services.AddOptions<SimulatedRailOptions>();
        if (configureProcessor is not null) services.Configure(configureProcessor);
        if (configureRail is not null) services.Configure(configureRail);

        services.AddScoped<IPaymentService, PaymentService>();
        services.AddSingleton<IPaymentRailClient, SimulatedPaymentRailClient>();
        services.AddSingleton<PaymentProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<PaymentProcessor>());
        services.AddSingleton<StatementProjector>();
        services.AddHostedService(sp => sp.GetRequiredService<StatementProjector>());
        services.AddSingleton<PaymentRailsDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<PaymentRailsDispatcher>());
        return services;
    }
}
