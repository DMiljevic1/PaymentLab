using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace PaymentLab.ServiceDefaults;

/// <summary>
/// OpenTelemetry for all .NET services: traces, metrics and logs exported over OTLP to the collector.
/// The endpoint comes from the standard OTEL_EXPORTER_OTLP_ENDPOINT variable (set in docker-compose).
/// Custom instrumentation uses ActivitySource / Meter names starting with "PaymentLab.".
/// </summary>
public static class Telemetry
{
    public const string SourcePrefix = "PaymentLab.";

    private static readonly string[] IgnoredPaths = ["/healthz", "/ready", "/swagger", "/openapi", "/favicon.ico"];

    public static WebApplicationBuilder AddTelemetry(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.IncludeFormattedMessage = true;
            o.ParseStateValues = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceNamespace: "paymentlab", autoGenerateServiceInstanceId: false,
                    serviceInstanceId: Environment.MachineName)
                .AddEnvironmentVariableDetector())
            .WithTracing(t => t
                .SetSampler(new ParentBasedSampler(new DropOrphanClientSpans()))
                .AddSource(SourcePrefix + "*")
                .AddSource("Npgsql")
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = ctx => !IgnoredPaths.Any(p => ctx.Request.Path.StartsWithSegments(p));
                    o.RecordException = true;
                })
                .AddHttpClientInstrumentation(o => o.RecordException = true))
            .WithMetrics(m => m
                .SetExemplarFilter(ExemplarFilterType.TraceBased)
                .AddMeter(SourcePrefix + "*")
                .AddMeter("Npgsql")
                .AddMeter("Microsoft.AspNetCore.RateLimiting")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .UseOtlpExporter();

        return builder;
    }

    /// <summary>
    /// Root spans of kind Client (a DB query or HTTP call that is not part of any request, message or saga run,
    /// e.g. background polling) are dropped; they would flood Tempo with one-span traces. Everything else is kept.
    /// </summary>
    private sealed class DropOrphanClientSpans : Sampler
    {
        public override SamplingResult ShouldSample(in SamplingParameters p) =>
            p.Kind == ActivityKind.Client ? new SamplingResult(SamplingDecision.Drop) : new SamplingResult(SamplingDecision.RecordAndSample);
    }

    /// <summary>Parses a W3C traceparent stored earlier (DB column, message header).</summary>
    public static ActivityContext ParentFrom(string? traceParent) =>
        traceParent is not null && ActivityContext.TryParse(traceParent, null, isRemote: true, out var ctx) ? ctx : default;
}
