namespace Inventory.Domain.Common;
public sealed record ReserveResult
{
    public bool Succeeded { get; }
    public string? Reason { get; }

    private ReserveResult(bool succeeded, string? reason) => (Succeeded, Reason) = (succeeded, reason);

    public static ReserveResult Success() => new(true, null);
    public static ReserveResult Fail(string reason) => new(false, reason);
}
