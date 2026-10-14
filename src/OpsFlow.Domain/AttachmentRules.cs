namespace OpsFlow.Domain;

public static class AttachmentRules
{
    public const long DefaultMaxBytes = 10 * 1024 * 1024;
    public const int DefaultMaxCount = 5;

    private static readonly Dictionary<string, string[]> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ["25504446"],
        ["image/png"] = ["89504E47"],
        ["image/jpeg"] = ["FFD8FF"],
        ["image/gif"] = ["474946383761", "474946383961"],
        ["image/webp"] = ["52494646"],
        ["text/plain"] = [],
        ["text/csv"] = []
    };

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv"
    };

    public static bool TryResolveContentType(string fileName, ReadOnlySpan<byte> header, out string contentType, out string error)
    {
        contentType = "";
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension) || !Extensions.TryGetValue(extension, out var expected))
        {
            error = "File type is not allowed. Allowed types: pdf, png, jpg, gif, webp, txt, csv.";
            return false;
        }

        if (!HeaderMatches(expected, header))
        {
            error = "File contents do not match the declared type.";
            return false;
        }

        contentType = expected;
        error = "";
        return true;
    }

    public static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return "file";
        }

        var cleaned = new string(name.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "file" : cleaned[..Math.Min(cleaned.Length, 120)];
    }

    public static bool IsSafeStorageKey(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || storageKey.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return !Path.IsPathRooted(storageKey) && storageKey.IndexOfAny(['\\', ':']) < 0;
    }

    private static bool HeaderMatches(string contentType, ReadOnlySpan<byte> header)
    {
        if (contentType is "text/plain" or "text/csv")
        {
            var sample = header.Length > 512 ? header[..512] : header;
            return sample.IndexOf((byte)0) < 0;
        }

        var hex = Convert.ToHexString(header);
        if (contentType == "image/webp")
        {
            return hex.StartsWith("52494646", StringComparison.OrdinalIgnoreCase)
                && hex.Contains("57454250", StringComparison.OrdinalIgnoreCase);
        }

        return Signatures[contentType].Any(signature => hex.StartsWith(signature, StringComparison.OrdinalIgnoreCase));
    }
}
