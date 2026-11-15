using System.Linq;
using Content.Shared._Stalker.Bands;

namespace Content.Client._Stalker_OW.Bands;

public sealed class STTitleDrafts
{
    private static int _nextRequestId;

    private readonly Dictionary<Guid, string> _drafts = new();
    private readonly Dictionary<int, (Guid Member, string Text)> _submitted = new();

    public bool TryGetDraft(Guid member, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text)
    {
        return _drafts.TryGetValue(member, out text);
    }

    public void SetDraft(Guid member, string text)
    {
        _drafts[member] = text;
    }

    public void Discard(Guid member)
    {
        _drafts.Remove(member);
    }
    
    public int Submit(Guid member, string text)
    {
        var requestId = ++_nextRequestId;
        _submitted[requestId] = (member, text);
        return requestId;
    }
    
    public bool OnResult(Guid member, int requestId, bool saved)
    {
        if (!_submitted.Remove(requestId, out var submission) || submission.Member != member)
            return false;

        if (!saved)
            return false;

        if (_drafts.TryGetValue(member, out var draft) && draft != submission.Text)
            return false;

        _drafts.Remove(member);
        return true;
    }

    public void RetainOnly(IReadOnlySet<Guid> members)
    {
        foreach (var id in _drafts.Keys.Where(id => !members.Contains(id)).ToList())
        {
            _drafts.Remove(id);
        }

        foreach (var id in _submitted.Where(s => !members.Contains(s.Value.Member)).Select(s => s.Key).ToList())
        {
            _submitted.Remove(id);
        }
    }
}

public static class STRosterView
{
    public static readonly IComparer<string> NameOrder =
        Comparer<string>.Create((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
    
    public static List<BandMemberInfo> Sort(IEnumerable<BandMemberInfo> members, Func<BandMemberInfo, string> displayName)
    {
        return members
            .OrderByDescending(m => m.Rank)
            .ThenBy(displayName, NameOrder)
            .ThenBy(m => m.UserId.UserId)
            .ToList();
    }
    
    public static bool Matches(BandMemberInfo member, string displayName, string roleName, string search)
    {
        var query = search.Trim();
        if (query.Length == 0)
            return true;

        return Contains(displayName, query)
               || Contains(member.Title, query)
               || Contains(roleName, query)
               || Contains(member.PlayerName, query);
    }

    private static bool Contains(string text, string query)
    {
        return text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
