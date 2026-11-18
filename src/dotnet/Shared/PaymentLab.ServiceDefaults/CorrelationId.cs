using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PaymentLab.ServiceDefaults;

/// <summary>Reads (or creates) X-Correlation-Id, echoes it in the response and puts it into the logging scope.</summary>
public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "CorrelationId";

    public static string? Current(HttpContext? ctx) => ctx?.Items[ItemKey] as string;

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var id = ctx.Request.Headers[HeaderName].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(id))
            {
                id = Guid.NewGuid().ToString("N");
                ctx.Request.Headers[HeaderName] = id; // a proxy (YARP) then forwards it downstream
            }
            ctx.Items[ItemKey] = id;
            ctx.Response.Headers[HeaderName] = id;
            System.Diagnostics.Activity.Current?.SetTag("correlation.id", id);

            var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CorrelationId");
            using (logger.BeginScope(new Dictionary<string, object> { ["correlation_id"] = id }))
            {
                await next(ctx);
            }
        });
}
