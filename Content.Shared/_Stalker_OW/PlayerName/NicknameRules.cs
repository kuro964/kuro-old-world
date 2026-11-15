using System.Text.RegularExpressions;

namespace Content.Shared._Stalker_OW.PublicName;

/// <summary>
/// Validation rules for nicknames
/// Shared so the character editor, profile validation, and the spawn-time server check all agree
/// </summary>
public static class STNicknameRules
{
    public const int MaxLength = 32;

    private static readonly Regex RestrictedRegex = new(@"[^A-Za-z0-9 '\-]");

    public static string FormatDisplayName(string nickname) => $"\"{nickname}\"";

    public static STNicknameDisplay ValidateDisplay(STNicknameDisplay display) =>
        display == STNicknameDisplay.FullName ? display : STNicknameDisplay.NicknameOnly;

    public static string FormatDisplayName(string realName, string nickname, STNicknameDisplay display)
    {
        if (string.IsNullOrWhiteSpace(nickname) || nickname == realName)
            return realName;

        var quoted = FormatDisplayName(nickname);
        if (ValidateDisplay(display) != STNicknameDisplay.FullName)
            return quoted;

        // Insert after the first word
        var separator = realName.IndexOf(' ');
        return separator < 0
            ? $"{realName} {quoted}"
            : $"{realName[..separator]} {quoted}{realName[separator..]}";
    }

    public static string Sanitize(string? nickname, bool restrictedNames)
    {
        return STNameSanitizer.Sanitize(nickname, MaxLength, restrictedNames ? RestrictedRegex : null);
    }
}
