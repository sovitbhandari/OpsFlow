namespace OpsFlow.Domain;

public static class SearchText
{
    public static string EscapeLike(string value)
    {
        return value
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);
    }
}
