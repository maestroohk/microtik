using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Kibo.Lab.MobileMoney;

public record CollectionRequest(
    [property: JsonPropertyName("externalReference")] string ExternalReference,
    [property: JsonPropertyName("phoneNumber")] string PhoneNumber,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("callbackUrl")] string CallbackUrl);

public class Collection
{
    public required string TransactionId { get; init; }
    public required CollectionRequest Request { get; init; }
    public string Status { get; set; } = "PENDING";
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public List<string> CallbackLog { get; } = [];
}

public class MobileMoneySimulator(IOptions<LabOptions> options, IHttpClientFactory httpFactory, ILogger<MobileMoneySimulator> logger)
{
    private readonly ConcurrentDictionary<string, Collection> collections = new();

    public IReadOnlyList<Collection> Recent => collections.Values.OrderByDescending(c => c.CreatedAt).Take(20).ToList();

    public Collection? Find(string id) => collections.GetValueOrDefault(id);

    public Collection Create(CollectionRequest request)
    {
        var collection = new Collection { TransactionId = $"SIM{Convert.ToHexString(RandomNumberGenerator.GetBytes(6))}", Request = request };
        collections[collection.TransactionId] = collection;
        return collection;
    }

    public async Task<bool> CompleteAsync(string id, bool approve, int callbackCopies, CancellationToken ct)
    {
        if (!collections.TryGetValue(id, out var collection) || collection.Status != "PENDING")
        {
            return false;
        }

        collection.Status = approve ? "SUCCESSFUL" : "FAILED";
        collection.Reason = approve ? null : "Customer declined the payment prompt";
        for (var i = 0; i < callbackCopies; i++)
        {
            await SendCallbackAsync(collection, ct);
        }

        return true;
    }

    public async Task SendForgedCallbackAsync(string id, CancellationToken ct)
    {
        if (collections.TryGetValue(id, out var collection))
        {
            await PostAsync(collection, "SUCCESSFUL", "not-a-valid-signature", ct);
        }
    }

    private async Task SendCallbackAsync(Collection collection, CancellationToken ct)
    {
        var body = Serialize(collection, collection.Status);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.Value.MomoCallbackSecret), Encoding.UTF8.GetBytes(body)));
        await PostAsync(collection, collection.Status, signature, ct);
    }

    private async Task PostAsync(Collection collection, string status, string signature, CancellationToken ct)
    {
        var body = Serialize(collection, status);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, collection.Request.CallbackUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Signature", signature);
            using var response = await httpFactory.CreateClient().SendAsync(request, ct);
            collection.CallbackLog.Add($"{DateTime.UtcNow:HH:mm:ss} callback → {(int)response.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            collection.CallbackLog.Add($"{DateTime.UtcNow:HH:mm:ss} callback failed: {ex.Message}");
            logger.LogWarning("Callback to {Url} failed: {Message}", collection.Request.CallbackUrl, ex.Message);
        }
    }

    private static string Serialize(Collection collection, string status) => JsonSerializer.Serialize(new
    {
        transactionId = collection.TransactionId,
        externalReference = collection.Request.ExternalReference,
        status,
        amount = collection.Request.Amount,
        currency = collection.Request.Currency
    });
}

public static class MobileMoneyEndpoints
{
    public static void MapMobileMoney(this WebApplication app)
    {
        var api = app.MapGroup("/momo/collections").AddEndpointFilter(async (context, next) =>
        {
            var key = context.HttpContext.RequestServices.GetRequiredService<IOptions<LabOptions>>().Value.MomoApiKey;
            var supplied = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
            return !string.IsNullOrEmpty(key) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(supplied))
                ? await next(context)
                : Results.Unauthorized();
        });

        api.MapPost("", (CollectionRequest request, MobileMoneySimulator momo) =>
        {
            var collection = momo.Create(request);
            return Results.Accepted(value: new { transactionId = collection.TransactionId, status = collection.Status });
        });

        api.MapGet("/{id}", (string id, MobileMoneySimulator momo) =>
        {
            var collection = momo.Find(id);
            return collection == null
                ? Results.NotFound()
                : Results.Json(new
                {
                    transactionId = collection.TransactionId, status = collection.Status, reason = collection.Reason,
                    amount = collection.Request.Amount, currency = collection.Request.Currency
                });
        });
    }
}
