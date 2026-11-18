using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PaymentLab.ServiceDefaults;

public static class Defaults
{
    /// <summary>Logging, health checks and other cross-cutting setup shared by all .NET services.</summary>
    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });
        builder.Logging.Configure(o => o.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

        builder.Services.AddHealthChecks();
        builder.Services.AddSingleton(new ServiceInfo(serviceName));
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // liveness: process is up; readiness: dependencies (registered per service with tag "ready")
        app.MapHealthChecks("/healthz", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => false
        });
        app.MapHealthChecks("/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready")
        });
        return app;
    }
}

public sealed record ServiceInfo(string Name);
