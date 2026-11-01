namespace Kibo.Web.Features.Payments;

public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/payments/callback/{provider}", async (string provider, HttpContext http, PaymentService payments,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            if (body.Length > 64_000)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var outcome = await payments.HandleCallbackAsync(provider, body, http.Request.Headers, ct);
            return outcome.StartsWith("rejected") ? Results.Unauthorized() : Results.Ok(new { outcome });
        }).DisableAntiforgery();
    }
}
