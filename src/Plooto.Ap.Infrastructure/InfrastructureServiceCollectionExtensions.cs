using Microsoft.Extensions.DependencyInjection;
using Plooto.Ap.Infrastructure.Repositories;

namespace Plooto.Ap.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Registers the repositories. The host registers ApDbContext itself (connection string is host policy).</summary>
    public static IServiceCollection AddApRepositories(this IServiceCollection services) => services
        .AddScoped<ICompanyRepository, CompanyRepository>()
        .AddScoped<IContractorRepository, ContractorRepository>()
        .AddScoped<IInvoiceRepository, InvoiceRepository>()
        .AddScoped<IPaymentRepository, PaymentRepository>()
        .AddScoped<IIdempotencyStore, IdempotencyStore>()
        .AddScoped<IApprovalRepository, ApprovalRepository>()
        .AddScoped<IStatementRepository, StatementRepository>();
}
