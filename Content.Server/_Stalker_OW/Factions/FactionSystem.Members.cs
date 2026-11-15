using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._Stalker_OW.Bands;
using Content.Server._Stalker_OW.FactionTitles;
using Content.Shared._Stalker.Bands;
using Content.Shared._Stalker.Bands.Components;
using Content.Shared._Stalker_EN.FactionRelations;
using Content.Shared._Stalker_OW.PublicName;
using Content.Shared.Administration;
using Content.Shared.Administration.Logs;
using Content.Shared.Administration.Managers;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.Popups;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._Stalker.Bands;

public enum STBandActionResult : byte
{
    Done,
    NoPermission,
    Busy,
    InvalidInput,
    NotFound,
    Ambiguous,
    Self,
    AlreadyMember,
    Full,
    InOtherBand,
    NotMember,
    Outranked,
    TitleLocked,
    TitlesNotReady,
    Unchanged,
    NotEnoughPoints,
    Failed,
}

public sealed partial class BandsSystem
{
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly ISharedAdminManager _adminManager = default!;
    [Dependency] private readonly SharedJobSystem _jobs = default!;
    [Dependency] private readonly STFactionTitleSystem _titles = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLog = default!;

    private const int MaxMemberLookupLength = 64;
    private readonly HashSet<string> _bandActionsInProgress = new();
    private readonly HashSet<Guid> _targetActionsInProgress = new();
    private readonly HashSet<(Guid UserId, string BandId)> _revokedMemberships = new();
    private readonly Dictionary<(EntityUid Igor, EntityUid Viewer), int> _uiRequests = new();
    private int _nextUiRequestId;
    private bool _refreshAllPending;

    internal Func<string, Task>? BeforeMemberLoadForTests;

    internal Action<EntityUid, EntityUid, BandsManagingBoundUserInterfaceState>? StateSentForTests;
    internal int TrackedUiRequestsForTests => _uiRequests.Count;

    private sealed class BandMemberData
    {
        public Guid UserId;
        public string UserName = string.Empty;
        public string? CharacterName;
        public string? Nickname;
        public STNicknameDisplay DisplayMode;
        public int Rank = int.MinValue;
        public string RoleId = string.Empty;
    }

    private enum PlayerLookup : byte
    {
        NotFound,
        Found,
        Ambiguous,
    }

    private void InitializeMembers()
    {
        SubscribeLocalEvent<BandsManagingComponent, BandsManagingSetTitleMessage>(OnSetTitle);
        SubscribeLocalEvent<BandsManagingComponent, ComponentShutdown>(OnManagingShutdown);
        SubscribeLocalEvent<BandsManagingComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<STFactionTitleAdminChangedEvent>(OnAdminTitleChanged);
        SubscribeLocalEvent<STBandPointsChangedEvent>(OnBandPointsChanged);
        SubscribeLocalEvent<STFactionRelationsChangedEvent>(OnFactionRelationsChanged);
        SubscribeLocalEvent<STWarZoneChangedEvent>(OnWarZoneChanged);
    }

    private void OnManagingShutdown(Entity<BandsManagingComponent> ent, ref ComponentShutdown args)
    {
        _loadedShopItems.Remove(ent.Owner);

        foreach (var key in _uiRequests.Keys.Where(k => k.Igor == ent.Owner).ToList())
        {
            _uiRequests.Remove(key);
        }
    }

    private void OnUiClosed(Entity<BandsManagingComponent> ent, ref BoundUIClosedEvent args)
    {
        _uiRequests.Remove((ent.Owner, args.Actor));
    }

    private void OnAdminTitleChanged(STFactionTitleAdminChangedEvent args)
    {
        RefreshAllViewers(args.BandId);
    }

    private void OnBandPointsChanged(STBandPointsChangedEvent args)
    {
        _refreshAllPending = true;
    }

    private void OnFactionRelationsChanged(STFactionRelationsChangedEvent args)
    {
        _refreshAllPending = true;
    }

    private void OnWarZoneChanged(STWarZoneChangedEvent args)
    {
        _refreshAllPending = true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_refreshAllPending)
            return;

        _refreshAllPending = false;
        RefreshAllViewers();
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (TryComp<BandsComponent>(args.Mob, out var bands) && bands.BandProto is { } bandId)
            _revokedMemberships.Remove((args.Player.UserId.UserId, bandId.Id));
    }

    #region Authority

    internal bool TryGetActorBand(EntityUid actor, [NotNullWhen(true)] out STBandPrototype? band, out int rank)
    {
        band = null;
        rank = 0;

        if (!TryComp<BandsComponent>(actor, out var bands)
            || bands.BandProto is not { } bandId
            || !_prototypeManager.TryIndex(bandId, out band))
        {
            return false;
        }

        var session = TryGetSession(actor, out var actorSession) ? actorSession : null;
        var revoked = session != null && _revokedMemberships.Contains((session.UserId.UserId, band.ID));

        if (!_mindSystem.TryGetMind(actor, out var mindId, out _)
            || !_jobs.MindTryGetJobId(mindId, out var jobId)
            || jobId is not { } job)
        {
            // Ghost roles use BandRankId unless their membership was revoked
            rank = revoked ? 0 : bands.BandRankId;
            return true;
        }

        foreach (var (hierarchyRank, hierarchyJob) in band.Hierarchy)
        {
            if (hierarchyJob != job)
                continue;

            rank = hierarchyRank;
            break;
        }

        if (rank <= 0 || session == null)
            return true;

        // Restore authority when an admin grants the required whitelist again
        if (revoked)
            revoked = !TryRestoreRevokedMembership(session, band.ID, job);

        // Respects the role whitelist cvar and non-whitelisted jobs
        if (revoked || !_jobWhitelistManager.IsAllowed(session, job))
            rank = 0;

        return true;
    }

    // Reconcile an admin whitelist restoration without changing patch, job or equipment
    private bool TryRestoreRevokedMembership(ICommonSession session, string bandId, ProtoId<JobPrototype> job)
    {
        if (_targetActionsInProgress.Contains(session.UserId.UserId) || !HoldsWhitelistFor(session, job))
            return false;

        return _revokedMemberships.Remove((session.UserId.UserId, bandId));
    }

    // Requires enabled role whitelists and a current whitelist for a restricted job
    private bool HoldsWhitelistFor(ICommonSession session, ProtoId<JobPrototype> job)
    {
        if (!_cfg.GetCVar(CCVars.GameRoleWhitelist))
            return false;

        if (!_prototypeManager.TryIndex(job, out var jobPrototype) || !jobPrototype.Whitelisted)
            return false;

        return _jobWhitelistManager.IsWhitelisted(session.UserId, job);
    }

    internal static bool CanManageBand(STBandPrototype band, int rank)
    {
        return band.ManagingRankId is { } managingRank && rank >= managingRank;
    }

    private bool TryGetLeader(EntityUid actor, [NotNullWhen(true)] out STBandPrototype? band, out int rank, out Guid userId)
    {
        userId = Guid.Empty;
        if (!TryGetActorBand(actor, out band, out rank) || !CanManageBand(band, rank) || !TryGetSession(actor, out var session))
        {
            band = null;
            return false;
        }

        userId = session.UserId.UserId;
        return true;
    }

    // Make sure player is still a Faction Leader in case they change characters
    private bool StillLeads(EntityUid igor, EntityUid actor, string bandId, Guid actorUserId, out int rank)
    {
        rank = 0;
        if (TerminatingOrDeleted(igor) || TerminatingOrDeleted(actor))
            return false;

        return TryGetLeader(actor, out var band, out rank, out var userId)
               && band.ID == bandId
               && userId == actorUserId;
    }

    private bool IsAdmin(EntityUid actor)
    {
        return _adminManager.HasAdminFlag(actor, AdminFlags.Admin);
    }

    // Leaders can manage their own faction at a matching Igor; Admins can manage any faction Igor
    internal string? ResolvePlayerFaction(BandsManagingComponent component, EntityUid actor)
    {
        if (TryGetActorBand(actor, out var band, out var rank) && CanManageBand(band, rank))
        {
            var ownFaction = _factionRelations.GetBandFactionName(band.Name);

            if (string.IsNullOrEmpty(component.Faction))
                return string.IsNullOrEmpty(ownFaction) ? null : ownFaction;

            if (!string.IsNullOrEmpty(ownFaction)
                && _factionRelations.ResolvePrimary(ownFaction) == _factionRelations.ResolvePrimary(component.Faction))
            {
                return component.Faction;
            }
        }

        if (!string.IsNullOrEmpty(component.Faction) && IsAdmin(actor))
            return component.Faction;

        return null;
    }

    private void Popup(EntityUid actor, string locId, params (string, object)[] args)
    {
        if (TerminatingOrDeleted(actor))
            return;

        _popup.PopupEntity(Loc.GetString(locId, args), actor, actor);
    }

    #endregion

    #region UI state

    private async void UpdateUiState(
        Entity<BandsManagingComponent> ent,
        EntityUid? actor = null,
        Task<Dictionary<Guid, BandMemberData>>? memberLoad = null)
    {
        try
        {
            var (uid, component) = ent;

            if (!_loadedShopItems.ContainsKey(uid))
                LoadShopItems(ent);

            if (actor is not { } viewer)
                return;

            var request = (uid, viewer);
            var requestId = ++_nextUiRequestId;
            _uiRequests[request] = requestId;

            var members = new List<BandMemberInfo>();
            string? bandName = null;
            var maxMembers = 0;
            var canManage = false;

            if (TryGetActorBand(viewer, out var band, out var viewerRank))
            {
                var memberData = await (memberLoad ?? LoadMembersAsync(band));

                if (!_uiRequests.TryGetValue(request, out var latest) || latest != requestId)
                    return;

                if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(viewer) || !_uiSystem.IsUiOpen(uid, BandsUiKey.Key, viewer))
                {
                    _uiRequests.Remove(request);
                    return;
                }

                if (!TryGetActorBand(viewer, out var current, out var currentRank)
                    || current.ID != band.ID
                    || currentRank != viewerRank)
                {
                    UpdateUiState(ent, viewer);
                    return;
                }

                bandName = band.Name;
                maxMembers = band.MaxMembers;
                canManage = CanManageBand(band, viewerRank);

                var viewerUserId = TryGetSession(viewer, out var session) ? session.UserId.UserId : Guid.Empty;
                members = BuildMemberInfos(band, memberData, viewerUserId, viewerRank, canManage);
            }

            // Checked before every send
            if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(viewer) || !_uiSystem.IsUiOpen(uid, BandsUiKey.Key, viewer))
            {
                _uiRequests.Remove(request);
                return;
            }

            var canManageRelations = ResolvePlayerFaction(component, viewer) != null;

            var state = BuildState(ent, band, bandName, maxMembers, members, canManage, canManageRelations);
            StateSentForTests?.Invoke(uid, viewer, state);
            _uiSystem.ServerSendUiMessage(uid, BandsUiKey.Key, new BandsManagingStateMessage(state), viewer);
        }
        catch (Exception e)
        {
            Log.Error($"Error updating band UI for {ToPrettyString(ent.Owner)}: {e}");
        }
    }

    // Read each faction roster once per refresh and calculate permissions separately for each viewer
    private void RefreshAllViewers(string? affectedBandId = null)
    {
        var memberLoads = new Dictionary<string, Task<Dictionary<Guid, BandMemberData>>>();
        var query = EntityQueryEnumerator<BandsManagingComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            foreach (var viewer in _uiSystem.GetActors(uid, BandsUiKey.Key).ToList())
            {
                Task<Dictionary<Guid, BandMemberData>>? memberLoad = null;
                if (TryGetActorBand(viewer, out var band, out _))
                {
                    if (affectedBandId != null && band.ID != affectedBandId)
                        continue;

                    if (!memberLoads.TryGetValue(band.ID, out memberLoad))
                    {
                        memberLoad = LoadMembersAsync(band);
                        memberLoads.Add(band.ID, memberLoad);
                    }
                }
                else if (affectedBandId != null)
                {
                    continue;
                }

                UpdateUiState((uid, comp), viewer, memberLoad);
            }
        }
    }

    private List<BandMemberInfo> BuildMemberInfos(
        STBandPrototype band,
        Dictionary<Guid, BandMemberData> memberData,
        Guid viewerUserId,
        int viewerRank,
        bool canManage)
    {
        var result = new List<BandMemberInfo>(memberData.Count);

        foreach (var member in memberData.Values
                     .OrderByDescending(m => m.Rank)
                     .ThenBy(m => m.CharacterName ?? m.UserName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(m => m.UserId))
        {
            var isSelf = member.UserId == viewerUserId;
            var outranked = member.Rank < viewerRank;

            _titles.TryGetTitle(member.UserId, band.ID, out var title);
            var locked = title?.AdminLocked ?? false;

            result.Add(new BandMemberInfo(
                new NetUserId(member.UserId),
                member.UserName,
                GetMemberDisplayName(member, band),
                member.RoleId,
                member.Rank,
                title?.Title ?? string.Empty,
                locked,
                canEditTitle: canManage && !locked && (isSelf || outranked),
                canRemove: canManage && !isSelf && outranked));
        }

        return result;
    }

    // Prefer the current character, then the selected profile, then the ckey
    // The client adds the title separately
    private string GetMemberDisplayName(BandMemberData member, STBandPrototype band)
    {
        if (_playerManager.TryGetSessionById(new NetUserId(member.UserId), out var session)
            && session.AttachedEntity is { } attached
            && TryComp<BandsComponent>(attached, out var bands)
            && bands.BandProto?.Id == band.ID)
        {
            return TryComp<STPublicNameComponent>(attached, out var publicName) && !string.IsNullOrWhiteSpace(publicName.Nickname)
                ? STNicknameRules.FormatDisplayName(Name(attached), publicName.Nickname, publicName.DisplayMode)
                : Name(attached);
        }

        if (string.IsNullOrWhiteSpace(member.CharacterName))
            return member.UserName;

        var nickname = STNicknameRules.Sanitize(member.Nickname, _cfg.GetCVar(CCVars.RestrictedNames));
        return nickname.Length > 0 && nickname != member.CharacterName
            ? STNicknameRules.FormatDisplayName(member.CharacterName, nickname, member.DisplayMode)
            : member.CharacterName;
    }

    internal async Task<List<BandMemberInfo>> GetMemberInfosForTestsAsync(STBandPrototype band)
    {
        var members = await LoadMembersAsync(band);
        return BuildMemberInfos(band, members, Guid.Empty, int.MinValue, false);
    }

    private string GetBandName(string bandId)
    {
        return _prototypeManager.TryIndex<STBandPrototype>(bandId, out var band) ? band.Name : bandId;
    }

    private BandsManagingBoundUserInterfaceState BuildState(
        Entity<BandsManagingComponent> ent,
        STBandPrototype? band,
        string? bandName,
        int maxMembers,
        List<BandMemberInfo> members,
        bool canManage,
        bool canManageRelations)
    {
        var (uid, component) = ent;
        var warZoneInfos = new List<WarZoneInfo>();
        foreach (var (_, wzComp) in _warZoneSystem.GetAllWarZones())
        {
            var owner = string.Empty;
            if (!string.IsNullOrEmpty(wzComp.DefendingBandProtoId))
                owner = GetBandName(wzComp.DefendingBandProtoId);
            else if (!string.IsNullOrEmpty(wzComp.DefendingFactionProtoId))
                owner = wzComp.DefendingFactionProtoId;

            var cooldown = wzComp.CooldownEndTime.HasValue
                ? (float) Math.Max(0, (wzComp.CooldownEndTime.Value - _warZoneSystem.CurrentTime).TotalSeconds)
                : 0f;

            var attacker = string.Empty;
            if (!string.IsNullOrEmpty(wzComp.CurrentAttackerBandProtoId))
                attacker = GetBandName(wzComp.CurrentAttackerBandProtoId);
            else if (!string.IsNullOrEmpty(wzComp.CurrentAttackerFactionProtoId))
                attacker = wzComp.CurrentAttackerFactionProtoId;

            var zoneName = string.IsNullOrWhiteSpace(wzComp.PortalName) ? wzComp.ZoneProto.Id : wzComp.PortalName;

            // Capture progress applies only while the zone has an attacker
            var progress = attacker.Length > 0 ? wzComp.CaptureProgress : 0f;
            warZoneInfos.Add(new WarZoneInfo(zoneName, owner, cooldown, attacker, owner, progress));
        }
        // Include the viewer's faction even when its points balance is zero
        var bandPointsInfos = new List<BandPointsInfo>();
        foreach (var (bandId, points) in _warZoneSystem.BandPoints)
        {
            bandPointsInfos.Add(new BandPointsInfo(bandId, GetBandName(bandId), points));
        }

        if (band != null && !_warZoneSystem.BandPoints.ContainsKey(band.ID))
            bandPointsInfos.Add(new BandPointsInfo(band.ID, band.Name, _warZoneSystem.GetBandPoints(band.ID)));

        var shopItems = _loadedShopItems.GetValueOrDefault(uid, new List<BandShopItem>());
        var playerFaction = component.Faction;
        if (string.IsNullOrEmpty(playerFaction) && band != null)
            playerFaction = _factionRelations.GetBandFactionName(band.Name);

        if (!string.IsNullOrEmpty(playerFaction))
            playerFaction = _factionRelations.ResolvePrimary(playerFaction);

        var relationsState = _factionRelations.BuildUiState();
        var hideRelationsTab = !string.IsNullOrEmpty(playerFaction) && _factionRelations.HidesRelationsUi(playerFaction);

        var allFactions = new List<string>();
        foreach (var f in relationsState.FactionIds)
        {
            if (!_factionRelations.IsAlias(f) && !_factionRelations.IsFactionRestricted(f) && !_factionRelations.IsFactionHidden(f))
                allFactions.Add(f);
        }

        var incomingProposals = new List<STFactionRelationProposalEntry>();
        var outgoingProposals = new List<STFactionRelationProposalEntry>();
        var cooldownsRemaining = new Dictionary<string, float>();

        if (!string.IsNullOrEmpty(playerFaction))
        {
            var (incoming, outgoing) = _factionRelations.GetProposalsForFaction(playerFaction);
            foreach (var p in incoming)
            {
                incomingProposals.Add(new STFactionRelationProposalEntry(
                    p.InitiatingFaction, p.TargetFaction, p.ProposedRelation, p.CustomMessage));
            }

            foreach (var p in outgoing)
            {
                outgoingProposals.Add(new STFactionRelationProposalEntry(
                    p.InitiatingFaction, p.TargetFaction, p.ProposedRelation, p.CustomMessage));
            }

            foreach (var f in allFactions)
            {
                if (f == playerFaction)
                    continue;

                var remaining = _factionRelations.GetCooldownRemaining(playerFaction, f);
                if (remaining > 0)
                    cooldownsRemaining[f] = remaining;
            }
        }

        return new BandsManagingBoundUserInterfaceState(
            bandName, maxMembers, members, canManage,
            warZoneInfos, bandPointsInfos, shopItems,
            playerFaction, allFactions, relationsState.Relations,
            incomingProposals, outgoingProposals, cooldownsRemaining,
            hideRelationsTab, _factionRelations.GetDisplayNames(),
            band?.ID, canManageRelations);
    }

    #endregion

    #region Members

    private async Task<Dictionary<Guid, BandMemberData>> LoadMembersAsync(STBandPrototype band)
    {
        var roleRanks = new Dictionary<string, int>();
        foreach (var (rank, job) in band.Hierarchy)
        {
            roleRanks[job.Id] = rank;
        }

        var result = new Dictionary<Guid, BandMemberData>();
        if (roleRanks.Count == 0)
            return result;

        if (BeforeMemberLoadForTests is { } beforeLoad)
            await beforeLoad(band.ID);

        var rows = await _dbManager.GetStalkerBandMembersAsync(roleRanks.Keys.ToList());
        foreach (var row in rows)
        {
            if (!roleRanks.TryGetValue(row.RoleId, out var rank))
                continue;

            if (!result.TryGetValue(row.UserId, out var data))
            {
                data = new BandMemberData
                {
                    UserId = row.UserId,
                    UserName = row.UserName,
                    CharacterName = row.CharacterName,
                    Nickname = row.Nickname,
                    DisplayMode = row.DisplayMode,
                };
                result[row.UserId] = data;
            }

            if (rank > data.Rank)
            {
                data.Rank = rank;
                data.RoleId = row.RoleId;
            }
        }

        return result;
    }

    // An exact ckey takes priority over character names and nicknames
    private async Task<(PlayerLookup Result, Guid UserId, string Ckey)> FindPlayerAsync(string input)
    {
        foreach (var session in _playerManager.Sessions)
        {
            if (string.Equals(session.Name, input, StringComparison.OrdinalIgnoreCase))
                return (PlayerLookup.Found, session.UserId.UserId, session.Name);
        }

        if (await _dbManager.GetPlayerRecordByUserName(input) is { } record)
            return (PlayerLookup.Found, record.UserId.UserId, record.LastSeenUserName);

        var name = StripQuotes(input);
        if (name.Length == 0)
            return (PlayerLookup.NotFound, Guid.Empty, string.Empty);

        ICommonSession? match = null;
        foreach (var session in _playerManager.Sessions)
        {
            if (session.AttachedEntity is not { } ent)
                continue;

            var matches = string.Equals(Name(ent), name, StringComparison.OrdinalIgnoreCase)
                          || TryComp<STPublicNameComponent>(ent, out var publicName)
                          && string.Equals(publicName.Nickname, name, StringComparison.OrdinalIgnoreCase);

            if (!matches)
                continue;

            if (match != null && match.UserId != session.UserId)
                return (PlayerLookup.Ambiguous, Guid.Empty, string.Empty);

            match = session;
        }

        return match == null
            ? (PlayerLookup.NotFound, Guid.Empty, string.Empty)
            : (PlayerLookup.Found, match.UserId.UserId, match.Name);
    }

    // Accept a nickname with surrounding straight or curly quotes
    internal static string StripQuotes(string input)
    {
        if (input.Length >= 2
            && (input[0] == '"' && input[^1] == '"' || input[0] == '\u201C' && input[^1] == '\u201D'))
        {
            return input[1..^1].Trim();
        }

        return input;
    }

    private async Task<string> GetPlayerNameAsync(Guid userId)
    {
        if (_playerManager.TryGetSessionById(new NetUserId(userId), out var session))
            return GetRecruitName(userId, session.Name);

        var record = await _dbManager.GetPlayerRecordByUserId(new NetUserId(userId));
        return record?.LastSeenUserName ?? userId.ToString();
    }

    private void PopupDenied(EntityUid actor, STBandActionResult denied)
    {
        Popup(actor, denied switch
        {
            STBandActionResult.NotMember => "stow-factions-not-member",
            STBandActionResult.Outranked => "stow-factions-outranked",
            _ => "stow-factions-leader-required",
        });
    }

    private string GetRecruitName(Guid userId, string ckey)
    {
        if (!_playerManager.TryGetSessionById(new NetUserId(userId), out var session) || session.AttachedEntity is not { } ent)
            return ckey;

        return TryComp<STPublicNameComponent>(ent, out var publicName) && !string.IsNullOrWhiteSpace(publicName.Nickname)
            ? STNicknameRules.FormatDisplayName(Name(ent), publicName.Nickname, publicName.DisplayMode)
            : Name(ent);
    }

    internal static int? ResolveMemberRank(STBandPrototype band, IEnumerable<string> whitelists)
    {
        var set = whitelists as ISet<string> ?? whitelists.ToHashSet();
        int? best = null;

        foreach (var (rank, job) in band.Hierarchy)
        {
            if (!set.Contains(job.Id))
                continue;

            if (best == null || rank > best)
                best = rank;
        }

        return best;
    }

    internal Func<Guid, Task<List<string>>>? TargetWhitelistLookupForTests;

    private Task<List<string>> GetTargetWhitelistsAsync(Guid target)
    {
        return TargetWhitelistLookupForTests is { } lookup
            ? lookup(target)
            : _dbManager.GetJobWhitelists(target);
    }

    private static List<string> GetBandRoles(STBandPrototype band, IEnumerable<string> whitelists)
    {
        var set = whitelists.ToHashSet();
        return band.Hierarchy.Values.Select(job => job.Id).Where(set.Contains).Distinct().ToList();
    }

    private STBandPrototype? FindOtherBand(IEnumerable<string> whitelists, STBandPrototype except)
    {
        var set = whitelists.ToHashSet();
        foreach (var proto in _prototypeManager.EnumeratePrototypes<STBandPrototype>())
        {
            if (proto.ID == except.ID)
                continue;

            foreach (var job in proto.Hierarchy.Values)
            {
                if (set.Contains(job.Id))
                    return proto;
            }
        }

        return null;
    }

    private void OnAddMember(EntityUid uid, BandsManagingComponent component, BandsManagingAddMemberMessage msg)
    {
        _ = AddMemberAsync(uid, msg.Actor, msg.PlayerName);
    }

    private void OnRemoveMember(EntityUid uid, BandsManagingComponent component, BandsManagingRemoveMemberMessage msg)
    {
        _ = RemoveMemberAsync(uid, msg.Actor, msg.PlayerUserId);
    }

    private void OnSetTitle(EntityUid uid, BandsManagingComponent component, BandsManagingSetTitleMessage msg)
    {
        _ = SetMemberTitleAndReplyAsync(uid, msg.Actor, msg.PlayerUserId, msg.Title, msg.RequestId);
    }

    private async Task SetMemberTitleAndReplyAsync(EntityUid igor, EntityUid actor, Guid target, string title, int requestId)
    {
        string? bandId = TryGetLeader(actor, out var band, out _, out _) ? band.ID : null;
        var result = await SetMemberTitleAsync(igor, actor, target, title);

        if (TerminatingOrDeleted(igor) || TerminatingOrDeleted(actor) || !_uiSystem.IsUiOpen(igor, BandsUiKey.Key, actor))
            return;

        var saved = IsTitleSaved(result);
        var savedTitle = saved && bandId != null && _titles.TryGetTitle(target, bandId, out var entry)
            ? entry.Title
            : string.Empty;

        _uiSystem.ServerSendUiMessage(igor, BandsUiKey.Key,
            new BandsManagingTitleResultMessage(target, requestId, saved, savedTitle), actor);
    }

    internal static bool IsTitleSaved(STBandActionResult result)
    {
        return result is STBandActionResult.Done or STBandActionResult.Unchanged;
    }

    internal async Task<STBandActionResult> AddMemberAsync(EntityUid igor, EntityUid actor, string rawInput)
    {
        if (!TryGetLeader(actor, out var band, out _, out var actorUserId))
        {
            Popup(actor, "stow-factions-leader-required");
            return STBandActionResult.NoPermission;
        }

        var input = rawInput.Trim();
        if (input.Length == 0 || input.Length > MaxMemberLookupLength)
        {
            Popup(actor, "stow-factions-members-add-hint");
            return STBandActionResult.InvalidInput;
        }

        if (!_bandActionsInProgress.Add(band.ID))
        {
            Popup(actor, "stow-factions-busy");
            return STBandActionResult.Busy;
        }

        Guid? lockedTarget = null;
        try
        {
            var (lookup, target, ckey) = await FindPlayerAsync(input);
            switch (lookup)
            {
                case PlayerLookup.Ambiguous:
                    Popup(actor, "stow-factions-player-ambiguous", ("name", input));
                    return STBandActionResult.Ambiguous;
                case PlayerLookup.NotFound:
                    Popup(actor, "stow-factions-player-not-found", ("name", input));
                    return STBandActionResult.NotFound;
            }

            if (target == actorUserId)
            {
                Popup(actor, "stow-factions-cannot-add-self");
                return STBandActionResult.Self;
            }

            if (!_targetActionsInProgress.Add(target))
            {
                Popup(actor, "stow-factions-busy");
                return STBandActionResult.Busy;
            }

            lockedTarget = target;

            var members = await LoadMembersAsync(band);
            var targetWhitelists = await _dbManager.GetJobWhitelists(target);

            if (!StillLeads(igor, actor, band.ID, actorUserId, out _))
            {
                Popup(actor, "stow-factions-leader-required");
                return STBandActionResult.NoPermission;
            }

            if (members.ContainsKey(target))
            {
                Popup(actor, "stow-factions-already-member", ("name", input));
                return STBandActionResult.AlreadyMember;
            }

            if (members.Count >= band.MaxMembers)
            {
                Popup(actor, "stow-factions-full", ("max", band.MaxMembers));
                return STBandActionResult.Full;
            }

            if (FindOtherBand(targetWhitelists, band) is { } otherBand)
            {
                Popup(actor, "stow-factions-in-other-band", ("name", input), ("band", otherBand.Name));
                return STBandActionResult.InOtherBand;
            }

            if (band.Hierarchy.Count == 0)
            {
                Log.Error($"Band {band.ID} has no hierarchy; can't add members.");
                Popup(actor, "stow-factions-failed");
                return STBandActionResult.Failed;
            }

            var lowestRole = band.Hierarchy[band.Hierarchy.Keys.Min()];

            // Save first so the refreshed member list includes them.
            // then update the in-memory whitelist cache and the player's lobby
            await _dbManager.AddJobWhitelist(target, lowestRole);
            _jobWhitelistManager.SetWhitelistCached(new NetUserId(target), lowestRole, true);
            _revokedMemberships.Remove((target, band.ID));

            _adminLog.Add(LogType.STFaction, LogImpact.Medium,
                $"{ToPrettyString(actor):player} recruited {ckey} ({target}) into {band.ID} as {lowestRole.Id}");

            Popup(actor, "stow-factions-member-added", ("name", GetRecruitName(target, ckey)), ("ckey", ckey));
            RefreshAllViewers(band.ID);
            return STBandActionResult.Done;
        }
        catch (Exception e)
        {
            Log.Error($"Error adding band member: {e}");
            Popup(actor, "stow-factions-failed");
            return STBandActionResult.Failed;
        }
        finally
        {
            _bandActionsInProgress.Remove(band.ID);
            if (lockedTarget is { } t)
                _targetActionsInProgress.Remove(t);
        }
    }

    // Remove a lower-ranked member and revoke their permissions
    internal async Task<STBandActionResult> RemoveMemberAsync(EntityUid igor, EntityUid actor, Guid target)
    {
        if (!TryGetLeader(actor, out var band, out _, out var actorUserId))
        {
            Popup(actor, "stow-factions-leader-required");
            return STBandActionResult.NoPermission;
        }

        if (target == actorUserId)
        {
            Popup(actor, "stow-factions-cannot-remove-self");
            return STBandActionResult.Self;
        }

        if (!_bandActionsInProgress.Add(band.ID))
        {
            Popup(actor, "stow-factions-busy");
            return STBandActionResult.Busy;
        }

        var targetLocked = false;
        try
        {
            if (!_targetActionsInProgress.Add(target))
            {
                Popup(actor, "stow-factions-busy");
                return STBandActionResult.Busy;
            }

            targetLocked = true;

            var members = await LoadMembersAsync(band);

            if (!StillLeads(igor, actor, band.ID, actorUserId, out var rank))
            {
                Popup(actor, "stow-factions-leader-required");
                return STBandActionResult.NoPermission;
            }

            if (!members.TryGetValue(target, out var member))
            {
                Popup(actor, "stow-factions-not-member");
                return STBandActionResult.NotMember;
            }

            if (member.Rank >= rank)
            {
                Popup(actor, "stow-factions-outranked");
                return STBandActionResult.Outranked;
            }

            if (!_titles.IsReady)
            {
                Popup(actor, "stow-factions-busy");
                return STBandActionResult.TitlesNotReady;
            }

            var revocation = (target, band.ID);
            var wasRevoked = !_revokedMemberships.Add(revocation);

            var bandId = band.ID;
            var bandPrototype = band;
            List<string>? removedRoles = null;
            var denied = STBandActionResult.NoPermission;

            async Task<IReadOnlyCollection<string>?> AuthorizeAtExecution()
            {
                if (!StillLeads(igor, actor, bandId, actorUserId, out _))
                    return null;

                var whitelists = await GetTargetWhitelistsAsync(target);

                if (!StillLeads(igor, actor, bandId, actorUserId, out var leaderRank))
                    return null;

                if (ResolveMemberRank(bandPrototype, whitelists) is not { } targetRank)
                {
                    denied = STBandActionResult.NotMember;
                    return null;
                }

                if (targetRank >= leaderRank)
                {
                    denied = STBandActionResult.Outranked;
                    return null;
                }

                removedRoles = GetBandRoles(bandPrototype, whitelists);
                return removedRoles;
            }

            var removal = await _titles.RemoveMembershipAsync(target, bandId, AuthorizeAtExecution);
            if (removal != STFactionTitleResult.Changed || removedRoles == null)
            {
                if (!wasRevoked)
                    _revokedMemberships.Remove(revocation);

                switch (removal)
                {
                    case STFactionTitleResult.NotReady:
                        Popup(actor, "stow-factions-busy");
                        return STBandActionResult.TitlesNotReady;
                    case STFactionTitleResult.NotAllowed:
                        PopupDenied(actor, denied);
                        return denied;
                    default:
                        Popup(actor, "stow-factions-failed");
                        return STBandActionResult.Failed;
                }
            }

            foreach (var role in removedRoles)
            {
                _jobWhitelistManager.SetWhitelistCached(new NetUserId(target), role, false);
            }

            _adminLog.Add(LogType.STFaction, LogImpact.Medium,
                $"{ToPrettyString(actor):player} removed {member.UserName} ({target}) from {band.ID}");

            Popup(actor, "stow-factions-member-removed", ("name", member.CharacterName ?? member.UserName));
            RefreshAllViewers(band.ID);
            return STBandActionResult.Done;
        }
        catch (Exception e)
        {
            Log.Error($"Error removing band member: {e}");
            Popup(actor, "stow-factions-failed");
            return STBandActionResult.Failed;
        }
        finally
        {
            _bandActionsInProgress.Remove(band.ID);
            if (targetLocked)
                _targetActionsInProgress.Remove(target);
        }
    }

    // Leaders can edit their own title and their members
    internal async Task<STBandActionResult> SetMemberTitleAsync(EntityUid igor, EntityUid actor, Guid target, string rawTitle)
    {
        if (!TryGetLeader(actor, out var band, out _, out var actorUserId))
        {
            Popup(actor, "stow-factions-leader-required");
            return STBandActionResult.NoPermission;
        }

        if (rawTitle.Length > STFactionTitleRules.MaxLength * 4)
        {
            Popup(actor, "stow-factions-title-hint", ("max", STFactionTitleRules.MaxLength));
            return STBandActionResult.InvalidInput;
        }

        if (!_targetActionsInProgress.Add(target))
        {
            Popup(actor, "stow-factions-busy");
            return STBandActionResult.Busy;
        }

        try
        {
            var bandId = band.ID;
            var bandPrototype = band;
            var denied = STBandActionResult.NoPermission;

            // Membership and rank are decided here, inside the title queue, from the target's current whitelists;
            // the leader is checked again after the lookup yields.
            async Task<bool> AuthorizeAtExecution()
            {
                if (!StillLeads(igor, actor, bandId, actorUserId, out _))
                    return false;

                var whitelists = await GetTargetWhitelistsAsync(target);

                if (!StillLeads(igor, actor, bandId, actorUserId, out var leaderRank))
                    return false;

                if (_revokedMemberships.Contains((target, bandId))
                    || ResolveMemberRank(bandPrototype, whitelists) is not { } targetRank)
                {
                    denied = STBandActionResult.NotMember;
                    return false;
                }

                if (target != actorUserId && targetRank >= leaderRank)
                {
                    denied = STBandActionResult.Outranked;
                    return false;
                }

                return true;
            }

            // For the popup. Looked up before saving, so a failed lookup can't turn a saved title into an error.
            var name = await GetPlayerNameAsync(target);

            switch (await _titles.SetTitleAsLeaderAsync(target, bandId, rawTitle, actor, actorUserId, AuthorizeAtExecution))
            {
                case STFactionTitleResult.Changed:
                    if (_titles.TryGetTitle(target, band.ID, out var saved) && saved.Title.Length > 0)
                        Popup(actor, "stow-factions-title-set", ("name", name), ("title", saved.Title));
                    else
                        Popup(actor, "stow-factions-title-cleared", ("name", name));

                    RefreshAllViewers(band.ID);
                    return STBandActionResult.Done;
                case STFactionTitleResult.Unchanged:
                    return STBandActionResult.Unchanged;
                case STFactionTitleResult.Locked:
                    Popup(actor, "stow-factions-title-locked");
                    return STBandActionResult.TitleLocked;
                case STFactionTitleResult.NotReady:
                    Popup(actor, "stow-factions-busy");
                    return STBandActionResult.TitlesNotReady;
                case STFactionTitleResult.NotAllowed:
                    PopupDenied(actor, denied);
                    return denied;
                default:
                    Popup(actor, "stow-factions-failed");
                    return STBandActionResult.Failed;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Error setting band member title: {e}");
            Popup(actor, "stow-factions-failed");
            return STBandActionResult.Failed;
        }
        finally
        {
            _targetActionsInProgress.Remove(target);
        }
    }

    #endregion

    #region Rewards

    // Check the reward, permissions and balance before charging points
    // Points are refunded if spawning fails
    internal STBandActionResult TryRedeem(EntityUid igor, EntityUid buyer, string itemId)
    {
        try
        {
            if (!TryGetLeader(buyer, out var band, out _, out var buyerUserId))
            {
                Popup(buyer, "stow-factions-leader-required");
                return STBandActionResult.NoPermission;
            }

            if (!TryComp<BandsManagingComponent>(igor, out var component))
                return STBandActionResult.Failed;

            if (!_loadedShopItems.ContainsKey(igor))
                LoadShopItems((igor, component));

            var item = _loadedShopItems[igor].FirstOrDefault(i => i.ProductEntity == itemId);
            if (item == null || !_prototypeManager.HasIndex<EntityPrototype>(item.ProductEntity))
            {
                Log.Warning($"Player {buyerUserId} tried to redeem unknown reward {itemId} at {ToPrettyString(igor)}.");
                Popup(buyer, "stow-factions-failed");
                return STBandActionResult.InvalidInput;
            }

            var points = _warZoneSystem.GetBandPoints(band.ID);
            if (points < item.Price)
            {
                Popup(buyer, "stow-factions-rewards-short", ("missing", item.Price - points));
                return STBandActionResult.NotEnoughPoints;
            }

            // Check where it would go before taking any points
            var coordinates = Transform(buyer).Coordinates;
            if (!coordinates.IsValid(EntityManager))
            {
                Log.Warning($"Can't spawn reward {itemId} for {buyerUserId}: invalid coordinates.");
                Popup(buyer, "stow-factions-failed");
                return STBandActionResult.Failed;
            }

            if (!_warZoneSystem.TryModifyBandPoints(band.ID, -item.Price))
            {
                Log.Error($"Failed to deduct {item.Price} points from band {band.ID} for reward {itemId}.");
                Popup(buyer, "stow-factions-failed");
                return STBandActionResult.Failed;
            }

            EntityUid product;
            try
            {
                product = Spawn(item.ProductEntity, coordinates);
            }
            catch (Exception e)
            {
                Log.Error($"Failed to spawn reward {itemId} for band {band.ID}, refunding {item.Price} points: {e}");
                _warZoneSystem.TryModifyBandPoints(band.ID, item.Price);
                Popup(buyer, "stow-factions-failed");
                return STBandActionResult.Failed;
            }

            _hands.PickupOrDrop(buyer, product);
            _adminLog.Add(LogType.STFaction, LogImpact.Low,
                $"{ToPrettyString(buyer):player} redeemed {itemId} for {item.Price} points from {band.ID}");
            Popup(buyer, "stow-factions-reward-redeemed", ("item", Name(product)));
            return STBandActionResult.Done;
        }
        catch (Exception e)
        {
            Log.Error($"Error redeeming band reward {itemId}: {e}");
            Popup(buyer, "stow-factions-failed");
            return STBandActionResult.Failed;
        }
    }

    #endregion
}
