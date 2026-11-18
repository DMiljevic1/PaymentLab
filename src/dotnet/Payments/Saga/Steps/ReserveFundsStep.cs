using Payments.Clients;
using Payments.Data;

namespace Payments.Saga.Steps;

/// <summary>Reserve the amount on the source account. Compensation: release the reservation.</summary>
public sealed class ReserveFundsStep(WalletClient wallet) : ISagaStep
{
    public string Name => "ReserveFunds";
    public SagaState State => SagaState.ReservingFunds;
    public StepKind Kind => StepKind.Compensatable;
    public int MaxAttempts => 5;

    public bool AppliesTo(Payment payment) => true;

    public async Task<StepOutcome> ExecuteAsync(SagaContext ctx, CancellationToken ct)
    {
        var p = ctx.Payment;
        var result = await wallet.ReserveAsync(p.Id, p.SourceAccountId, p.AmountMinor, p.Currency, ct);
        return result.Kind switch
        {
            WalletCallKind.Ok => StepOutcome.Done,
            WalletCallKind.Rejected => new StepOutcome.Reject(result.Reason ?? "reservation rejected"),
            WalletCallKind.Conflict => new StepOutcome.Escalate($"reservation conflict: {result}"),
            _ => new StepOutcome.Retry($"reserve: {result}"),
        };
    }

    public async Task<StepOutcome> CompensateAsync(SagaContext ctx, CancellationToken ct)
    {
        var result = await wallet.ReleaseAsync(ctx.Payment.Id, ct);
        return result.Kind switch
        {
            WalletCallKind.Ok => StepOutcome.Done,
            WalletCallKind.Conflict => new StepOutcome.Escalate("cannot release: reservation already committed"),
            _ => new StepOutcome.Retry($"release: {result}"),
        };
    }
}
