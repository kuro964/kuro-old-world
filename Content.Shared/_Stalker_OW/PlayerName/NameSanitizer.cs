using System.Text.RegularExpressions;

namespace Content.Shared._Stalker_OW.PublicName;

internal static class STNameSanitizer
{
    private static readonly Regex UnsafeRegex = new(@"[\[\]\p{C}]");
    private static readonly Regex WhitespaceRegex = new(@"\s+");

    internal static string Sanitize(string? value, int maxLength, Regex? restrictedRegex)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var result = WhitespaceRegex.Replace(value, " ");
        result = UnsafeRegex.Replace(result, string.Empty);
        if (restrictedRegex != null)
            result = restrictedRegex.Replace(result, string.Empty);

        result = result.Trim();
        return result.Length > maxLength ? result[..maxLength].TrimEnd() : result;
    }
}