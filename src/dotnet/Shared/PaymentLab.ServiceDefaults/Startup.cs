using Microsoft.Extensions.Logging;

namespace PaymentLab.ServiceDefaults;

public static class Startup
{
    /// <summary>Retries an action until it succeeds; used for DB migrations and broker setup at startup.</summary>
    public static async Task RetryAsync(string what, Func<CancellationToken, Task> action, ILogger logger,
        CancellationToken ct, int maxAttempts = 60, TimeSpan? delay = null)
    {
        var wait = delay ?? TimeSpan.FromSeconds(2);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action(ct);
                logger.LogInformation("{What} succeeded on attempt {Attempt}", what, attempt);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                logger.LogWarning("{What} failed (attempt {Attempt}/{Max}): {Error}", what, attempt, maxAttempts, ex.Message);
                await Task.Delay(wait, ct);
            }
        }
    }
}
