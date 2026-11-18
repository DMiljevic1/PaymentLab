using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Payments.Api;
using Payments.Clients;
using Payments.Data;
using Payments.Messaging;
using Payments.Observability;
using Payments.Saga;
using Payments.Saga.Steps;
using PaymentLab.Contracts;
using PaymentLab.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("payments");

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddDbContext<PaymentsDbContext>(o => o
    .UseNpgsql(builder.Configuration.GetConnectionString("Payments"), npgsql => npgsql
        .ConfigureDataSource(ds => ds.ConfigureTracing(t => t
            .ConfigureCommandSpanNameProvider(cmd => SqlSpanName.From(cmd.CommandText))
            .ConfigureCommandEnrichmentCallback((activity, _) => activity.SetTag("peer.service", "payments-db")))))
    .UseSnakeCaseNamingConvention());
builder.Services.AddSingleton(TimeProvider.System);

// Wallet client (HTTP). Retries are done by the saga, not by the HTTP client.
builder.Services.Configure<WalletOptions>(builder.Configuration.GetSection("Wallet"));
builder.Services.AddHttpClient<WalletClient>((sp, http) =>
{
    var o = sp.GetRequiredService<IOptions<WalletOptions>>().Value;
    http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
});

// Saga
builder.Services.Configure<SagaOptions>(builder.Configuration.GetSection("Saga"));
builder.Services.AddScoped<ReserveFundsStep>();
builder.Services.AddScoped<SubmitToBankStep>();
builder.Services.AddScoped<CommitFundsStep>();
builder.Services.AddScoped<PaymentSagaDefinition>();
builder.Services.AddScoped<SagaStateMachine>();
builder.Services.AddSingleton<SagaSignals>();
builder.Services.AddSingleton<SagaProcessor>();
builder.Services.AddHostedService<SagaWorker>();

// Messaging
builder.Services.AddSingleton(RabbitMqOptions.From(builder.Configuration));
builder.Services.AddSingleton<RabbitMqConnection>();
builder.Services.AddHostedService<OutboxRelay>();
builder.Services.AddHostedService<BankResultConsumer>();
builder.Services.AddHostedService<PaymentsStatsCollector>();

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddReadinessProbe("payments-db", (sp, ct) => sp.GetRequiredService<PaymentsDbContext>().Database.CanConnectAsync(ct));

var app = builder.Build();

app.UseCorrelationId();
app.MapOpenApi();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Payments API"));
app.MapDefaultEndpoints();
app.MapPaymentEndpoints();
app.MapAdminEndpoints();

// Migrations must finish before the background services touch the database.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
    await Startup.RetryAsync("Payments DB migration", ct => db.Database.MigrateAsync(ct), app.Logger, app.Lifetime.ApplicationStopping);
}

app.Run();
