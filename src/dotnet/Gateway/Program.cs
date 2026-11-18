using System.Threading.RateLimiting;
using PaymentLab.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("gateway");

builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Per-client token bucket. Client = X-Client-Id header, falling back to the remote IP.
var rl = builder.Configuration.GetSection("RateLimit");
var tokenLimit = rl.GetValue("TokenLimit", 200);
var tokensPerSecond = rl.GetValue("TokensPerSecond", 200);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("per-client", ctx =>
    {
        var client = ctx.Request.Headers["X-Client-Id"].FirstOrDefault()
                     ?? ctx.Connection.RemoteIpAddress?.ToString()
                     ?? "anonymous";
        return RateLimitPartition.GetTokenBucketLimiter(client, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokenLimit,
            TokensPerPeriod = tokensPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "1";
        return ValueTask.CompletedTask;
    };
});

var app = builder.Build();

app.UseCorrelationId();
app.UseRateLimiter();
app.MapDefaultEndpoints();
app.MapReverseProxy();

app.Run();
