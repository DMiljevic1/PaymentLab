using System.Diagnostics;
using Microsoft.Extensions.Options;
using Payments.Data;
using Payments.Observability;
using Payments.Saga.Steps;
using PaymentLab.Contracts;

namespace Payments.Saga;

// ============================================================================================
//  The payment saga in one place:
//    1. SagaState          – every state a payment saga can be in
//    2. PaymentSagaDefinition – which steps run, in which order (add new steps HERE)
//    3. SagaStateMachine   – the transition rules (what happens after each step outcome)
//
//  Forward:      Created → ReserveFunds → SubmitToBank (external only) → CommitFunds → Completed
//  Compensation: completed compensatable steps are undone in reverse order   → Failed
//  Declined before any effect (e.g. insufficient funds)                      → Rejected
//  Cannot decide automatically (retries exhausted after the pivot, unknown bank outcome,
//  compensation failing)                                                     → RequiresAttention
//
//  I/O (loading, locking, saving) lives in SagaProcessor; this file is pure decision logic.
// ============================================================================================

public enum SagaState
{
    Created,
    ReservingFunds,       // ReserveFundsStep
    AwaitingBank,         // SubmitToBankStep: command sent, waiting for the result
    VerifyingBankStatus,  // SubmitToBankStep: result overdue, asking the bank for the status
    CommittingFunds,      // CommitFundsStep
    Compensating,         // undoing completed steps in reverse order
    Completed,            // final
    Failed,               // final: compensated
    Rejected,             // final: declined before any effect
    RequiresAttention,    // parked for an operator (POST /admin/sagas/{id}/retry | /abort)
}

public sealed class PaymentSagaDefinition(ReserveFundsStep reserveFunds, SubmitToBankStep submitToBank, CommitFundsStep commitFunds)
{
    /// <summary>
    /// The ordered list of steps. To add a step (e.g. a fraud/limits check), implement ISagaStep,
    /// register it in DI, add its state to SagaState and insert it here.
    /// </summary>
    public IReadOnlyList<ISagaStep> Steps { get; } =
    [
        reserveFunds,   // Compensatable: release the reservation
        submitToBank,   // Pivot (external payments only): once the bank accepted, there is no way back
        commitFunds,    // Retriable: debit (and credit for internal payments) the reserved funds
    ];

    public ISagaStep Get(string name) =>
        Steps.FirstOrDefault(s => s.Name == name) ?? throw new InvalidOperationException($"Unknown saga step '{name}'");

    public ISagaStep? FirstStep(Payment p) => Steps.FirstOrDefault(s => s.AppliesTo(p));

    public ISagaStep? NextStep(string current, Payment p)
    {
        var index = Steps.ToList().FindIndex(s => s.Name == current);
        return Steps.Skip(index + 1).FirstOrDefault(s => s.AppliesTo(p));
    }

    public static bool IsFinal(SagaState s) => s is SagaState.Completed or SagaState.Failed or SagaState.Rejected;

    public static PaymentStatus ToPaymentStatus(SagaState s) => s switch
    {
        SagaState.Completed => PaymentStatus.Completed,
        SagaState.Failed => PaymentStatus.Failed,
        SagaState.Rejected => PaymentStatus.Rejected,
        SagaState.RequiresAttention => PaymentStatus.UnderReview,
        _ => PaymentStatus.Pending,
    };
}

public sealed class SagaStateMachine(PaymentSagaDefinition definition, IOptions<SagaOptions> options)
{
    private const int MaxStepsPerRun = 10;
    private readonly SagaOptions _opt = options.Value;

    /// <summary>Apply pending reply messages, then run whatever is due. Steps that complete synchronously are chained.</summary>
    public async Task RunAsync(SagaContext ctx, IReadOnlyList<BankTransferResult> messages, CancellationToken ct)
    {
        var saga = ctx.Saga;

        foreach (var message in messages)
        {
            if (saga.State is SagaState.AwaitingBank or SagaState.VerifyingBankStatus && saga.CurrentStep is not null)
            {
                var step = definition.Get(saga.CurrentStep);
                await ApplyForward(ctx, step, await Safe(ct, step, "message", () => step.OnMessageAsync(ctx, message, ct)));
            }
            else
            {
                ctx.Logger.LogInformation("Ignoring {Outcome} result for payment {PaymentId} in state {State}",
                    message.Outcome, saga.PaymentId, saga.State);
            }
        }

        for (var i = 0; i < MaxStepsPerRun; i++)
        {
            if (saga.NextAttemptAt is null || saga.NextAttemptAt > ctx.Now) return;

            if (saga.State == SagaState.Created)
            {
                MoveToStep(ctx, definition.FirstStep(ctx.Payment));
                continue;
            }

            var step = definition.Get(saga.CurrentStep!);

            if (saga.State == SagaState.Compensating)
            {
                ApplyCompensation(ctx, step, await Safe(ct, step, "compensate", () => step.CompensateAsync(ctx, ct)));
                continue;
            }

            var waiting = saga.WaitingUntil is not null;
            var outcome = waiting
                ? await Safe(ct, step, "timeout", () => step.OnTimeoutAsync(ctx, ct))
                : await Safe(ct, step, "execute", () => step.ExecuteAsync(ctx, ct));
            await ApplyForward(ctx, step, outcome);
        }
    }

    // ---------------------------------------------------------------- forward transitions

    private Task ApplyForward(SagaContext ctx, ISagaStep step, StepOutcome outcome)
    {
        var saga = ctx.Saga;
        switch (outcome)
        {
            case StepOutcome.Completed:
                saga.CompletedSteps.Add(step.Name);
                MoveToStep(ctx, definition.NextStep(step.Name, ctx.Payment));
                break;

            case StepOutcome.Wait w:
                saga.WaitingUntil = ctx.Now + w.Timeout;
                saga.NextAttemptAt = saga.WaitingUntil;
                Transition(ctx, w.State ?? step.State, $"waiting up to {w.Timeout.TotalSeconds:0}s");
                break;

            case StepOutcome.Retry r:
                saga.Attempt++;
                saga.LastError = r.Error;
                PaymentsTelemetry.StepRetries.Add(1, new("step", step.Name), new("phase", "forward"));
                if (saga.Attempt < step.MaxAttempts)
                {
                    saga.WaitingUntil = null;
                    saga.NextAttemptAt = ctx.Now + Backoff(saga.Attempt);
                    ctx.RecordTransition(saga.State, saga.State, $"retry {saga.Attempt}/{step.MaxAttempts}: {r.Error}");
                }
                else if (step.Kind == StepKind.Retriable)
                {
                    Escalate(ctx, $"{step.Name}: retries exhausted ({r.Error})");
                }
                else
                {
                    // The step may have taken effect before we lost the reply, so it is compensated too.
                    StartCompensation(ctx, includeCurrent: step.Kind == StepKind.Compensatable,
                        reason: $"{step.Name}: retries exhausted ({r.Error})");
                }
                break;

            case StepOutcome.Reject r when step.Kind == StepKind.Retriable:
                Escalate(ctx, $"{step.Name} rejected after the pivot: {r.Reason}");
                break;

            case StepOutcome.Reject r:
                StartCompensation(ctx, includeCurrent: false, reason: r.Reason);
                break;

            case StepOutcome.Escalate e:
                Escalate(ctx, e.Reason);
                break;

            case StepOutcome.Ignore i:
                ctx.Logger.LogInformation("Saga {PaymentId}: {Why}", saga.PaymentId, i.Why);
                break;
        }
        return Task.CompletedTask;
    }

    private void MoveToStep(SagaContext ctx, ISagaStep? next)
    {
        var saga = ctx.Saga;
        saga.Attempt = 0;
        saga.WaitingUntil = null;
        saga.LastError = null;

        if (next is null)
        {
            saga.CurrentStep = null;
            saga.NextAttemptAt = null;
            Transition(ctx, SagaState.Completed, null);
            return;
        }

        saga.CurrentStep = next.Name;
        saga.NextAttemptAt = ctx.Now;
        Transition(ctx, next.State, null);
    }

    // ---------------------------------------------------------------- compensation

    private void StartCompensation(SagaContext ctx, bool includeCurrent, string reason)
    {
        var saga = ctx.Saga;
        ctx.Payment.FailureReason = reason;

        if (includeCurrent && saga.CurrentStep is not null && !saga.CompletedSteps.Contains(saga.CurrentStep))
            saga.CompletedSteps.Add(saga.CurrentStep);

        if (saga.CompletedSteps.Any(s => definition.Get(s).Kind == StepKind.Pivot))
        {
            Escalate(ctx, $"cannot compensate after the pivot: {reason}");
            return;
        }

        saga.Attempt = 0;
        saga.WaitingUntil = null;
        if (saga.CompletedSteps.Count == 0)
        {
            saga.CurrentStep = null;
            saga.NextAttemptAt = null;
            Transition(ctx, SagaState.Rejected, reason);
            return;
        }

        saga.CurrentStep = saga.CompletedSteps[^1];
        saga.NextAttemptAt = ctx.Now;
        Transition(ctx, SagaState.Compensating, reason);
    }

    private void ApplyCompensation(SagaContext ctx, ISagaStep step, StepOutcome outcome)
    {
        var saga = ctx.Saga;
        switch (outcome)
        {
            case StepOutcome.Completed:
                saga.CompletedSteps.Remove(step.Name);
                saga.Attempt = 0;
                saga.LastError = null;
                if (saga.CompletedSteps.Count == 0)
                {
                    saga.CurrentStep = null;
                    saga.NextAttemptAt = null;
                    Transition(ctx, SagaState.Failed, ctx.Payment.FailureReason);
                }
                else
                {
                    saga.CurrentStep = saga.CompletedSteps[^1];
                    saga.NextAttemptAt = ctx.Now;
                    ctx.RecordTransition(saga.State, saga.State, $"compensated {step.Name}");
                }
                break;

            case StepOutcome.Retry r:
                saga.Attempt++;
                saga.LastError = r.Error;
                PaymentsTelemetry.StepRetries.Add(1, new("step", step.Name), new("phase", "compensate"));
                if (saga.Attempt < step.MaxAttempts)
                {
                    saga.NextAttemptAt = ctx.Now + Backoff(saga.Attempt);
                    ctx.RecordTransition(saga.State, saga.State, $"compensation retry {saga.Attempt}/{step.MaxAttempts}: {r.Error}");
                }
                else
                {
                    Escalate(ctx, $"compensation of {step.Name} failed: {r.Error}");
                }
                break;

            default:
                Escalate(ctx, $"compensation of {step.Name} returned {outcome}");
                break;
        }
    }

    // ---------------------------------------------------------------- operator actions

    public void OperatorRetry(SagaContext ctx)
    {
        var saga = ctx.Saga;
        if (saga.State != SagaState.RequiresAttention || saga.ResumeState is null)
            throw new InvalidOperationException($"Saga is in {saga.State}, not RequiresAttention");

        var resume = saga.ResumeState.Value;
        saga.ResumeState = null;
        saga.Attempt = 0;
        saga.WaitingUntil = null;
        saga.NextAttemptAt = ctx.Now;
        Transition(ctx, resume, "operator retry");
    }

    public void OperatorAbort(SagaContext ctx)
    {
        var saga = ctx.Saga;
        if (saga.State != SagaState.RequiresAttention)
            throw new InvalidOperationException($"Saga is in {saga.State}, not RequiresAttention");

        var wasCompensating = saga.ResumeState == SagaState.Compensating;
        saga.ResumeState = null;
        var current = saga.CurrentStep is null ? null : definition.Get(saga.CurrentStep);
        StartCompensation(ctx, includeCurrent: !wasCompensating && current?.Kind == StepKind.Compensatable,
            reason: "aborted by operator");
    }

    // ---------------------------------------------------------------- helpers

    private void Escalate(SagaContext ctx, string reason)
    {
        var saga = ctx.Saga;
        saga.ResumeState = saga.State;
        saga.LastError = reason;
        saga.WaitingUntil = null;
        saga.NextAttemptAt = null;
        Transition(ctx, SagaState.RequiresAttention, reason);
        ctx.Logger.LogWarning("Saga {PaymentId} requires attention: {Reason}", saga.PaymentId, reason);
    }

    private static void Transition(SagaContext ctx, SagaState to, string? detail)
    {
        var saga = ctx.Saga;
        var from = saga.State;
        saga.State = to;
        saga.UpdatedAt = ctx.Now;
        ctx.Payment.Status = PaymentSagaDefinition.ToPaymentStatus(to);
        ctx.Payment.UpdatedAt = ctx.Now;
        if (PaymentSagaDefinition.IsFinal(to)) ctx.Payment.CompletedAt = ctx.Now;
        ctx.RecordTransition(from, to, detail);
        ctx.Logger.LogInformation("Payment {PaymentId} saga {FromState} -> {ToState} {Detail}", saga.PaymentId, from, to, detail ?? "");
        PaymentsTelemetry.Transitions.Add(1, new("from", from.ToString()), new("to", to.ToString()));
        Activity.Current?.AddEvent(new ActivityEvent("saga.transition", tags: new ActivityTagsCollection
        {
            ["from"] = from.ToString(), ["to"] = to.ToString(), ["detail"] = detail,
        }));
    }

    private TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(Math.Pow(2, attempt - 1) * 0.5, _opt.MaxBackoffSeconds);
        return TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.4));
    }

    /// <summary>Runs a step callback in its own span; exceptions become a Retry outcome.</summary>
    private static async Task<StepOutcome> Safe(CancellationToken ct, ISagaStep step, string phase, Func<Task<StepOutcome>> action)
    {
        using var activity = PaymentsTelemetry.Source.StartActivity($"saga.step {step.Name} {phase}");
        activity?.SetTag("saga.step", step.Name);
        activity?.SetTag("saga.step.phase", phase);
        var started = Stopwatch.GetTimestamp();
        StepOutcome outcome;
        try
        {
            outcome = await action();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            outcome = new StepOutcome.Retry($"{ex.GetType().Name}: {ex.Message}");
        }

        var name = outcome.GetType().Name;
        activity?.SetTag("saga.step.outcome", name);
        if (outcome is StepOutcome.Retry or StepOutcome.Escalate)
            activity?.SetStatus(ActivityStatusCode.Error, outcome.ToString());
        PaymentsTelemetry.StepDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
            new("step", step.Name), new("phase", phase), new("outcome", name));
        return outcome;
    }
}
