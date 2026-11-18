using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PaymentLab.ServiceDefaults;

/// <summary>Readiness check that runs a delegate (e.g. "can the DbContext connect").</summary>
public sealed class DelegateHealthCheck(Func<IServiceProvider, CancellationToken, Task<bool>> probe, IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            return await probe(scope.ServiceProvider, ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}

public static class HealthCheckExtensions
{
    public static IHealthChecksBuilder AddReadinessProbe(this IHealthChecksBuilder builder, string name,
        Func<IServiceProvider, CancellationToken, Task<bool>> probe) =>
        builder.Add(new HealthCheckRegistration(name,
            sp => new DelegateHealthCheck(probe, sp), HealthStatus.Unhealthy, ["ready"], TimeSpan.FromSeconds(3)));
}
