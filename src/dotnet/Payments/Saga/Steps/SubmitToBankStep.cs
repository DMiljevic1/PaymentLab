using Microsoft.Extensions.Options;
using Payments.Data;
using PaymentLab.Contracts;

namespace Payments.Saga.Steps;

/// <summary>
/// Send the transfer to the bank via the Connector (RabbitMQ) and wait for the result.
/// If the result is overdue the outcome is unknown, so the saga asks the bank for the status
/// instead of assuming failure. The bank call is idempotent per payment id, so resubmitting is safe.
/// </summary>
public sealed class SubmitToBankStep(IOptions<SagaOptions> options) : ISagaStep
{
    private readonly SagaOptions _opt = options.Value;

    public string Name => "SubmitToBank";
    public SagaState State => SagaState.AwaitingBank;
    public StepKind Kind => StepKind.Pivot;
    public int MaxAttempts => _opt.MaxBankInquiries;

    public bool AppliesTo(Payment payment) => payment.Type == PaymentType.External;

    public Task<StepOutcome> ExecuteAsync(SagaContext ctx, CancellationToken ct) => Task.FromResult(Submit(ctx));

    public Task<StepOutcome> OnTimeoutAsync(SagaContext ctx, CancellationToken ct)
    {
        var saga = ctx.Saga;
        saga.Attempt++;
        if (saga.Attempt > _opt.MaxBankInquiries)
            return Task.FromResult<StepOutcome>(new StepOutcome.Escalate(
                $"bank outcome unknown after {_opt.MaxBankInquiries} status inquiries"));

        var messageId = Guid.NewGuid();
        ctx.Send(Topology.QueryStatusKey, MessageTypes.QueryBankTransferStatus, messageId,
            new QueryBankTransferStatus(messageId, ctx.Payment.Id, saga.Attempt, ctx.Now));
        return Task.FromResult<StepOutcome>(new StepOutcome.Wait(
            TimeSpan.FromSeconds(_opt.BankInquiryTimeoutSeconds), SagaState.VerifyingBankStatus));
    }

    public Task<StepOutcome> OnMessageAsync(SagaContext ctx, BankTransferResult message, CancellationToken ct)
    {
        StepOutcome outcome = message.Outcome switch
        {
            BankOutcomes.Accepted => Accept(ctx, message),
            BankOutcomes.Rejected => new StepOutcome.Reject($"bank rejected: {message.ReasonCode ?? "unspecified"}"),
            // The bank never received the transfer (e.g. the command was lost): send it again.
            BankOutcomes.NotFound => Submit(ctx),
            // Connector could not tell; keep waiting, the deadline triggers the next status inquiry.
            _ => new StepOutcome.Ignore($"bank outcome {message.Outcome}, waiting for the deadline"),
        };
        return Task.FromResult(outcome);
    }

    private StepOutcome Submit(SagaContext ctx)
    {
        var p = ctx.Payment;
        var messageId = Guid.NewGuid();
        ctx.Send(Topology.SubmitTransferKey, MessageTypes.SubmitBankTransfer, messageId,
            new SubmitBankTransfer(messageId, p.Id, ctx.Saga.Attempt + 1, p.AmountMinor, p.Currency,
                p.SourceAccountId, p.CreditorIban!, p.Reference, ctx.Now));
        return new StepOutcome.Wait(TimeSpan.FromSeconds(_opt.BankTimeoutSeconds), SagaState.AwaitingBank);
    }

    private static StepOutcome Accept(SagaContext ctx, BankTransferResult message)
    {
        ctx.Payment.BankReference = message.BankReference;
        return StepOutcome.Done;
    }
}
