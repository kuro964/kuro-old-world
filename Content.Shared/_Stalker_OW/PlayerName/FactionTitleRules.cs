using System.Text.RegularExpressions;

namespace Content.Shared._Stalker_OW.PublicName;

public static class STFactionTitleRules
{
    public const int MaxLength = 24;

    private static readonly Regex RestrictedRegex = new(@"[^A-Za-z0-9 '\-\.]");

    public static string Sanitize(string? title, bool restrictedNames)
    {
        return STNameSanitizer.Sanitize(title, MaxLength, restrictedNames ? RestrictedRegex : null);
    }
}
