using System.Diagnostics.Metrics;

namespace Wallet.Observability;

public static class WalletTelemetry
{
    public const string Name = "PaymentLab.Wallet";
    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Operations =
        Meter.CreateCounter<long>("wallet.operations", "{operation}", "Reserve / commit / release calls by result");
    public static readonly Counter<long> AccountsCreated =
        Meter.CreateCounter<long>("wallet.accounts.created", "{account}", "Accounts created through the API");

    public static void Op(string operation, object result) =>
        Operations.Add(1, new("operation", operation), new("result", result.ToString()));
}
