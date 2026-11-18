using Microsoft.Extensions.Options;

namespace Payments.Saga;

/// <summary>
/// Drives sagas forward. Two loops:
///  - poll loop: every PollIntervalMs claims a batch of due sagas (timeouts, retries, resumed after restart);
///  - nudge loop: reacts immediately to new payments and newly arrived bank results.
/// </summary>
public sealed class SagaWorker(SagaProcessor processor, SagaSignals signals, IOptions<SagaOptions> options, ILogger<SagaWorker> logger)
    : BackgroundService
{
    private readonly SagaOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Saga worker {Owner} started", processor.Owner);
        await Task.WhenAll(PollLoop(stoppingToken), NudgeLoop(stoppingToken));
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ids = await processor.ClaimDueAsync(ct);
                if (ids.Count > 0)
                {
                    await Parallel.ForEachAsync(ids,
                        new ParallelOptions { MaxDegreeOfParallelism = _opt.MaxParallelism, CancellationToken = ct },
                        async (id, token) => await processor.ProcessClaimedAsync(id, token));
                }
                if (ids.Count < _opt.BatchSize)
                    await Task.Delay(_opt.PollIntervalMs, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError("Saga poll failed: {Error}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private async Task NudgeLoop(CancellationToken ct)
    {
        await Parallel.ForEachAsync(signals.ReadySagas.ReadAllAsync(ct),
            new ParallelOptions { MaxDegreeOfParallelism = _opt.MaxParallelism, CancellationToken = ct },
            async (id, token) =>
            {
                try
                {
                    if (await processor.TryClaimAsync(id, token))
                        await processor.ProcessClaimedAsync(id, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    logger.LogWarning("Nudge for saga {PaymentId} failed: {Error}", id, ex.Message);
                }
            });
    }
}
