using Kibo.Web.Data;
using Kibo.Web.Features.Payments;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Portal;

public static class PortalEndpoints
{
    public static void MapPortalEndpoints(this WebApplication app)
    {
        app.MapGet("/captive-portal/api/{slug}", async (string slug, HttpContext http, KiboDb kiboDb) =>
        {
            await using var db = kiboDb.ForSystem();
            if (!await db.Sites.AnyAsync(s => s.Slug == slug))
            {
                return Results.NotFound();
            }

            var portalUrl = $"{http.Request.Scheme}://{http.Request.Host}/p/{slug}";
            return Results.Json(new Dictionary<string, object>
            {
                ["captive"] = true,
                ["user-portal-url"] = portalUrl
            }, contentType: "application/captive+json");
        });

        app.MapGet("/p/{slug}/payment/{token}/status", async (string token, PaymentService payments, CancellationToken ct) =>
        {
            var payment = await payments.FindByPortalTokenAsync(token, ct);
            return payment == null ? Results.NotFound() : Results.Json(new { status = payment.Status.ToString() });
        });
    }
}
