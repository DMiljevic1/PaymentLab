using Payments.Clients;
using Payments.Data;

namespace Payments.Saga.Steps;

/// <summary>
/// Turn the reservation into a debit (and a credit on the destination for internal payments).
/// Runs after the pivot, so it is never compensated: it is retried, and escalated if it keeps failing.
/// </summary>
public sealed class CommitFundsStep(WalletClient wallet) : ISagaStep
{
    public string Name => "CommitFunds";
    public SagaState State => SagaState.CommittingFunds;
    public StepKind Kind => StepKind.Retriable;
    public int MaxAttempts => 8;

    public bool AppliesTo(Payment payment) => true;

    public async Task<StepOutcome> ExecuteAsync(SagaContext ctx, CancellationToken ct)
    {
        var p = ctx.Payment;
        var credit = p.Type == PaymentType.Internal ? p.DestinationAccountId : null;
        var result = await wallet.CommitAsync(p.Id, credit, ct);
        return result.Kind switch
        {
            WalletCallKind.Ok => StepOutcome.Done,
            WalletCallKind.Transient => new StepOutcome.Retry($"commit: {result}"),
            _ => new StepOutcome.Escalate($"commit failed: {result}"),
        };
    }
}
