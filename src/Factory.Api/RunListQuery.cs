public sealed record RunListQuery(int Page, int Size)
{
    public static RunListQuery Normalize(int? page, int? pageSize) => new(
        Math.Max(page ?? 1, 1),
        Math.Clamp(pageSize ?? 25, 1, 100));
}
