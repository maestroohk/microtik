namespace Kibo.Web.Features;

public record OperationResult<T>(bool Succeeded, T? Value, string? Error)
{
    public static OperationResult<T> Ok(T value) => new(true, value, null);
    public static OperationResult<T> Fail(string error) => new(false, default, error);
}
