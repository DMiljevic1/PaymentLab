using Microsoft.EntityFrameworkCore;
using PaymentLab.ServiceDefaults;
using Wallet.Api;
using Wallet.Data;
using Wallet.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("wallet");

builder.Services.AddDbContext<WalletDbContext>(o => o
    .UseNpgsql(builder.Configuration.GetConnectionString("Wallet"))
    .UseSnakeCaseNamingConvention());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<WalletService>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddReadinessProbe("wallet-db", (sp, ct) => sp.GetRequiredService<WalletDbContext>().Database.CanConnectAsync(ct));

var app = builder.Build();

app.UseCorrelationId();
app.MapOpenApi();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Wallet API"));
app.MapDefaultEndpoints();
app.MapWalletEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WalletDbContext>();
    await Startup.RetryAsync("Wallet DB migration", ct => db.Database.MigrateAsync(ct), app.Logger, app.Lifetime.ApplicationStopping);
}

app.Run();
