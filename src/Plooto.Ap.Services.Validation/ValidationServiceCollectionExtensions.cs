using Microsoft.Extensions.DependencyInjection;

namespace Plooto.Ap.Services.Validation;

public static class ValidationServiceCollectionExtensions
{
    public static IServiceCollection AddValidationServices(this IServiceCollection services) =>
        services.AddScoped<IValidationService, ValidationService>();
}
