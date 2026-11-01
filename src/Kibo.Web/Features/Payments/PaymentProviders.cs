using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Kibo.Web.Features.Payments;

public enum ProviderPaymentStatus
{
    Pending,
    Succeeded,
    Failed,
    NotFound
}

public record ProviderInitiation(bool Accepted, string? ProviderReference, string? Error);

public record ProviderStatus(ProviderPaymentStatus Status, decimal? Amount, string? Currency, string? Reason);

public record ProviderCallback(string? ProviderReference, string? PaymentReference);

public interface IPaymentProvider
{
    string Name { get; }
    Task<ProviderInitiation> InitiateAsync(string paymentReference, string phoneNumber, decimal amount, string currency,
        string description, CancellationToken ct);
    Task<ProviderStatus> GetStatusAsync(string providerReference, CancellationToken ct);
    ProviderCallback? ParseAuthenticatedCallback(string body, IHeaderDictionary headers);
}

public class SimulatedMobileMoneyOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string CallbackSigningSecret { get; set; } = "";
    public string CallbackUrl { get; set; } = "";
}

public class SimulatedMobileMoneyProvider(HttpClient http, IOptions<SimulatedMobileMoneyOptions> options) : IPaymentProvider
{
    public const string ProviderName = "sim-momo";
    public string Name => ProviderName;

    public async Task<ProviderInitiation> InitiateAsync(string paymentReference, string phoneNumber, decimal amount,
        string currency, string description, CancellationToken ct)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/momo/collections");
        request.Headers.Add("X-Api-Key", settings.ApiKey);
        request.Content = JsonContent.Create(new SimCollectionRequest(paymentReference, phoneNumber, amount, currency,
            description, settings.CallbackUrl));

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new ProviderInitiation(false, null, $"Provider rejected the request ({(int)response.StatusCode}).");
            }

            var body = await response.Content.ReadFromJsonAsync<SimCollectionResponse>(ct);
            return body?.TransactionId is { Length: > 0 }
                ? new ProviderInitiation(true, body.TransactionId, null)
                : new ProviderInitiation(false, null, "Provider returned no transaction id.");
        }
        catch (HttpRequestException ex)
        {
            return new ProviderInitiation(false, null, $"Mobile money provider unreachable: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProviderInitiation(false, null, "Mobile money provider timed out.");
        }
    }

    public async Task<ProviderStatus> GetStatusAsync(string providerReference, CancellationToken ct)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{settings.BaseUrl.TrimEnd('/')}/momo/collections/{Uri.EscapeDataString(providerReference)}");
        request.Headers.Add("X-Api-Key", settings.ApiKey);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new ProviderStatus(ProviderPaymentStatus.NotFound, null, null, "Unknown transaction");
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<SimCollectionStatus>(ct)
                   ?? throw new InvalidOperationException("Empty status response from provider.");
        var status = body.Status switch
        {
            "SUCCESSFUL" => ProviderPaymentStatus.Succeeded,
            "FAILED" => ProviderPaymentStatus.Failed,
            _ => ProviderPaymentStatus.Pending
        };
        return new ProviderStatus(status, body.Amount, body.Currency, body.Reason);
    }

    public ProviderCallback? ParseAuthenticatedCallback(string body, IHeaderDictionary headers)
    {
        var signature = headers["X-Signature"].ToString();
        var secret = options.Value.CallbackSigningSecret;
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret))
        {
            return null;
        }

        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature)))
        {
            return null;
        }

        var callback = System.Text.Json.JsonSerializer.Deserialize<SimCallback>(body);
        return callback == null ? null : new ProviderCallback(callback.TransactionId, callback.ExternalReference);
    }

    private record SimCollectionRequest(
        [property: JsonPropertyName("externalReference")] string ExternalReference,
        [property: JsonPropertyName("phoneNumber")] string PhoneNumber,
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("callbackUrl")] string CallbackUrl);

    private record SimCollectionResponse([property: JsonPropertyName("transactionId")] string? TransactionId);

    private record SimCollectionStatus(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("reason")] string? Reason);

    private record SimCallback(
        [property: JsonPropertyName("transactionId")] string? TransactionId,
        [property: JsonPropertyName("externalReference")] string? ExternalReference);
}
