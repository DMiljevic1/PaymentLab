using System.Net;
using System.Net.Http.Json;
using PaymentLab.Contracts;

namespace Payments.Clients;

public enum WalletCallKind { Ok, Rejected, Conflict, NotFound, Transient }

/// <summary>Result of a Wallet call. Transient = timeout, connection error or 5xx: the outcome is unknown, retry.</summary>
public sealed record WalletCallResult(WalletCallKind Kind, string? Status = null, string? Reason = null)
{
    public override string ToString() => $"{Kind}{(Status is null ? "" : $" {Status}")}{(Reason is null ? "" : $" ({Reason})")}";
}

public sealed class WalletOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8082";
    public double TimeoutSeconds { get; set; } = 3;
}

public sealed class WalletClient(HttpClient http, ILogger<WalletClient> logger)
{
    private sealed record ReservationResponse(string Status, string? Reason);

    public Task<WalletCallResult> ReserveAsync(Guid paymentId, Guid accountId, long amountMinor, string currency, CancellationToken ct) =>
        CallAsync("reserve", paymentId, () => http.PostAsJsonAsync("reservations",
            new { paymentId, accountId, amountMinor, currency }, Json.Options, ct), ct);

    public Task<WalletCallResult> CommitAsync(Guid paymentId, Guid? creditAccountId, CancellationToken ct) =>
        CallAsync("commit", paymentId, () => http.PostAsJsonAsync($"reservations/{paymentId}/commit",
            new { creditAccountId }, Json.Options, ct), ct);

    public Task<WalletCallResult> ReleaseAsync(Guid paymentId, CancellationToken ct) =>
        CallAsync("release", paymentId, () => http.PostAsync($"reservations/{paymentId}/release", null, ct), ct);

    private async Task<WalletCallResult> CallAsync(string operation, Guid paymentId, Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            using var response = await send();
            ReservationResponse? body = null;
            if (response.Content.Headers.ContentType?.MediaType == "application/json")
                body = await response.Content.ReadFromJsonAsync<ReservationResponse>(Json.Options, ct);

            var kind = response.StatusCode switch
            {
                HttpStatusCode.OK => WalletCallKind.Ok,
                HttpStatusCode.UnprocessableEntity => WalletCallKind.Rejected,
                HttpStatusCode.Conflict => WalletCallKind.Conflict,
                HttpStatusCode.NotFound => WalletCallKind.NotFound,
                _ => WalletCallKind.Transient,
            };
            if (kind == WalletCallKind.Transient)
                logger.LogWarning("Wallet {Operation} for {PaymentId} returned {StatusCode}", operation, paymentId, (int)response.StatusCode);
            return new WalletCallResult(kind, body?.Status, body?.Reason ?? (kind == WalletCallKind.Transient ? $"HTTP {(int)response.StatusCode}" : null));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Wallet {Operation} for {PaymentId} failed: {Error}", operation, paymentId, ex.Message);
            return new WalletCallResult(WalletCallKind.Transient, Reason: ex is TaskCanceledException ? "timeout" : ex.Message);
        }
    }
}
