using Content.Shared._Stalker.Bands;
using Robust.Shared.Utility;

namespace Content.Shared._Stalker_OW.PublicName;

public sealed class STPublicNameSystem : EntitySystem
{
    private const string LocNameWithRank = "st-public-name-with-rank";
    
    // Returns the public name for an entity, given its real (metadata) name
    public string GetPublicName(EntityUid uid, string realName)
    {
        var name = GetUntitledName(uid, realName);

        var rank = GetRankPrefix(uid);
        if (rank == null)
            return name;

        return Loc.GetString(LocNameWithRank, ("rank", rank), ("name", name));
        
    }

    public string GetPublicName(EntityUid uid)
    {
        return GetPublicName(uid, Name(uid));
    }

    // The nickname or real name, without the faction title
    private string GetUntitledName(EntityUid uid, string realName)
    {
        if (TryComp<STPublicNameComponent>(uid, out var comp) && !string.IsNullOrWhiteSpace(comp.Nickname))
            return STNicknameRules.FormatDisplayName(realName, comp.Nickname, comp.DisplayMode);

        return realName;
    }
    
    public string GetNameMarkup(EntityUid uid, string shownName)
    {
        var rank = GetRankPrefix(uid);
        if (rank == null || shownName != GetPublicName(uid))
            return FormattedMessage.EscapeText(shownName);
        
        return Loc.GetString(LocNameWithRank,
            ("rank", $"[bolditalic]{FormattedMessage.EscapeText(rank)}[/bolditalic]"),
            ("name", FormattedMessage.EscapeText(GetUntitledName(uid, Name(uid)))));
    }

    // Returns the faction title assigned by faction leader or an admin
    // Null = No title set or disguised
    public string? GetRankPrefix(EntityUid uid)
    {
        if (!TryComp<STFactionTitleComponent>(uid, out var title) || string.IsNullOrWhiteSpace(title.Title))
            return null;

        // A disguised member must not leak their true faction's title.
        if (TryComp<BandsComponent>(uid, out var bands) && bands.IsDisguised)
            return null;

        return title.Title;
    }
    
    // Set player nickname
    // The caller must queue an identity update after changing this value
    public void SetNickname(EntityUid uid, string nickname, STNicknameDisplay display = STNicknameDisplay.NicknameOnly)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            RemComp<STPublicNameComponent>(uid);
            return;
        }

        var comp = EnsureComp<STPublicNameComponent>(uid);
        display = STNicknameRules.ValidateDisplay(display);
        if (comp.Nickname == nickname && comp.DisplayMode == display)
            return;

        comp.Nickname = nickname;
        comp.DisplayMode = display;
        Dirty(uid, comp);
    }

    // Set faction title
    // The caller must queue an identity update after changing this value
    public void SetFactionTitle(EntityUid uid, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            RemComp<STFactionTitleComponent>(uid);
            return;
        }

        var comp = EnsureComp<STFactionTitleComponent>(uid);
        if (comp.Title == title)
            return;

        comp.Title = title;
        Dirty(uid, comp);
    }
}
