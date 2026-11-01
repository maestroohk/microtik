namespace Kibo.Web.Features.Routers;

public class RouterLiveUpdates
{
    public event Func<Guid, Guid, Task>? RouterChanged;

    public async Task PublishAsync(Guid tenantId, Guid routerId)
    {
        var handlers = RouterChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Func<Guid, Guid, Task>>())
        {
            try
            {
                await handler(tenantId, routerId);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
