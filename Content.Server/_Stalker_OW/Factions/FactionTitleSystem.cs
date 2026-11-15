using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.Mind;
using Content.Shared._Stalker.Bands;
using Content.Shared._Stalker_OW.PublicName;
using Content.Shared.Administration;
using Content.Shared.Administration.Logs;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.IdentityManagement;
using Content.Shared.Verbs;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._Stalker_OW.FactionTitles;

public enum STFactionTitleResult : byte
{
    Changed,
    Unchanged,
    Locked,
    NotReady,
    Failed,
    NotAllowed,
}

public sealed class STFactionTitleSystem : EntitySystem
{
    [Dependency] private readonly IServerDbManager _db = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLog = default!;
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IdentitySystem _identity = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly QuickDialogSystem _quickDialog = default!;
    [Dependency] private readonly STPublicNameSystem _publicName = default!;

    private static readonly TimeSpan LoadRetryDelay = TimeSpan.FromSeconds(30);
    
    public sealed record TitleEntry(string Title, bool AdminLocked, Guid? SetByUserId);
    
    private readonly record struct TitleDecision(STFactionTitleResult Result, TitleEntry? Entry, Func<Task>? Write = null);

    private enum LoadState : byte
    {
        Loading,
        Loaded,
        Failed,
    }

    private readonly Dictionary<(Guid UserId, string BandId), TitleEntry> _titles = new();
    private readonly Dictionary<(Guid UserId, string BandId), Task<STFactionTitleResult>> _queues = new();
    private LoadState _loadState = LoadState.Loading;
    private readonly CancellationTokenSource _retryCts = new();

    public bool IsReady => _loadState == LoadState.Loaded;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<GetVerbsEvent<Verb>>(OnGetVerbs);

        _ = LoadFromDatabaseAsync();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _retryCts.Cancel();
    }

    #region Loading

    internal async Task LoadFromDatabaseAsync()
    {
        _loadState = LoadState.Loading;

        List<StalkerBandMemberTitle> rows;
        try
        {
            rows = await _db.GetAllStalkerBandMemberTitlesAsync();
        }
        catch (Exception e)
        {
            _loadState = LoadState.Failed;
            Log.Error($"Failed to load faction titles; title edits are disabled until loading succeeds. " +
                      $"Retrying in {LoadRetryDelay.TotalSeconds} seconds. {e}");

            Robust.Shared.Timing.Timer.Spawn(LoadRetryDelay, () => _ = LoadFromDatabaseAsync(), _retryCts.Token);
            return;
        }

        _titles.Clear();
        foreach (var row in rows)
        {
            _titles[(row.PlayerUserId, row.BandId)] = new TitleEntry(row.Title, row.AdminLocked, row.SetByUserId);
        }

        _loadState = LoadState.Loaded;

        // Apply titles to spawned characters while database is loading
        var query = EntityQueryEnumerator<BandsComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (_mind.TryGetMind(uid, out _, out var mind) && mind.UserId is { } userId)
                ApplyTo(uid, userId.UserId);
        }
    }

    internal void SimulateNotLoadedForTests()
    {
        _loadState = LoadState.Failed;
    }

    internal Func<Guid, string, Task>? BeforeWriteForTests;

    internal Action<Guid>? BeforeRefreshForTests;

    #endregion

    #region Serialized changes

    private Task<STFactionTitleResult> EnqueueAsync(
        Guid userId,
        string bandId,
        Func<TitleEntry?, TitleDecision> decide,
        Func<Task<bool>>? authorize = null)
    {
        var key = (userId, bandId);
        var previous = _queues.GetValueOrDefault(key);
        var task = RunAfterAsync(previous, key, authorize, decide);
        _queues[key] = task;
        ForgetWhenDone(key, task);
        return task;
    }

    private async void ForgetWhenDone((Guid UserId, string BandId) key, Task<STFactionTitleResult> task)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            Log.Error($"Unexpected error in the faction title queue for {key.UserId} in {key.BandId}: {e}");
        }
        finally
        {
            if (_queues.TryGetValue(key, out var current) && current == task)
                _queues.Remove(key);
        }
    }

    private async Task<STFactionTitleResult> RunAfterAsync(
        Task<STFactionTitleResult>? previous,
        (Guid UserId, string BandId) key,
        Func<Task<bool>>? authorize,
        Func<TitleEntry?, TitleDecision> decide)
    {
        if (previous != null)
        {
            try
            {
                await previous;
            }
            catch
            {
                // Earlier failures do not block later changes, ForgetWhenDone logs it
            }
        }

        if (_loadState != LoadState.Loaded)
            return STFactionTitleResult.NotReady;

        // Check permissions again after earlier changes finish
        if (authorize != null)
        {
            bool allowed;
            try
            {
                allowed = await authorize();
            }
            catch (Exception e)
            {
                Log.Error($"Error authorizing faction title change for {key.UserId} in {key.BandId}: {e}");
                return STFactionTitleResult.Failed;
            }

            if (!allowed)
                return STFactionTitleResult.NotAllowed;

            if (_loadState != LoadState.Loaded)
                return STFactionTitleResult.NotReady;
        }

        _titles.TryGetValue(key, out var current);

        TitleDecision decision;
        try
        {
            decision = decide(current);
        }
        catch (Exception e)
        {
            Log.Error($"Error deciding faction title change for {key.UserId} in {key.BandId}: {e}");
            return STFactionTitleResult.Failed;
        }

        if (decision.Result != STFactionTitleResult.Changed)
            return decision.Result;

        try
        {
            if (BeforeWriteForTests is { } hook)
                await hook(key.UserId, key.BandId);

            if (decision.Write is { } write)
            {
                await write();
            }
            else if (decision.Entry == null)
            {
                await _db.DeleteStalkerBandMemberTitleAsync(key.UserId, key.BandId);
            }
            else
            {
                await _db.SetStalkerBandMemberTitleAsync(
                    key.UserId, key.BandId, decision.Entry.Title, decision.Entry.AdminLocked, decision.Entry.SetByUserId);
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to save faction title for {key.UserId} in {key.BandId}: {e}");
            return STFactionTitleResult.Failed;
        }

        if (decision.Entry == null)
            _titles.Remove(key);
        else
            _titles[key] = decision.Entry;

        try
        {
            RefreshLive(key.UserId);
        }
        catch (Exception e)
        {
            Log.Error($"Saved the faction title change for {key.UserId} in {key.BandId}, " +
                      $"but failed to update their in-round character: {e}");
        }

        return STFactionTitleResult.Changed;
    }

    #endregion

    #region Public API

    public string Sanitize(string? raw)
    {
        return STFactionTitleRules.Sanitize(raw, _cfg.GetCVar(CCVars.RestrictedNames));
    }

    public bool TryGetTitle(Guid userId, string bandId, [NotNullWhen(true)] out TitleEntry? entry)
    {
        return _titles.TryGetValue((userId, bandId), out entry);
    }

    public IEnumerable<(Guid UserId, string BandId, TitleEntry Entry)> GetTitles(string? bandId = null)
    {
        foreach (var ((userId, band), entry) in _titles)
        {
            if (bandId == null || band == bandId)
                yield return (userId, band, entry);
        }
    }

    // Sets or clears a title after checking for faction leader permissions
    public async Task<STFactionTitleResult> SetTitleAsLeaderAsync(
        Guid targetUserId,
        string bandId,
        string rawTitle,
        EntityUid leader,
        Guid leaderUserId,
        Func<Task<bool>> authorize)
    {
        var title = Sanitize(rawTitle);
        var leaderName = ToPrettyString(leader);

        var result = await EnqueueAsync(targetUserId, bandId, current =>
        {
            if (current is { AdminLocked: true })
                return new TitleDecision(STFactionTitleResult.Locked, null);

            if ((current?.Title ?? string.Empty) == title)
                return new TitleDecision(STFactionTitleResult.Unchanged, null);

            var entry = title.Length == 0 ? null : new TitleEntry(title, false, leaderUserId);
            return new TitleDecision(STFactionTitleResult.Changed, entry);
        }, authorize);

        if (result == STFactionTitleResult.Changed)
        {
            _adminLog.Add(LogType.Identity, LogImpact.Low,
                $"{leaderName:player} set the {bandId} faction title of {targetUserId} to '{title}'");
        }

        return result;
    }

    /// Sets a title as an admin
    public async Task<STFactionTitleResult> SetTitleAsAdminAsync(
        Guid targetUserId,
        string bandId,
        string rawTitle,
        bool locked,
        Guid? adminUserId,
        string adminName)
    {
        var title = Sanitize(rawTitle);

        var result = await EnqueueAsync(targetUserId, bandId, current =>
        {
            var entry = title.Length == 0 && !locked ? null : new TitleEntry(title, locked, adminUserId);

            if (current == null && entry == null)
                return new TitleDecision(STFactionTitleResult.Unchanged, null);

            if (current != null && entry != null && current.Title == entry.Title && current.AdminLocked == entry.AdminLocked)
                return new TitleDecision(STFactionTitleResult.Unchanged, null);

            return new TitleDecision(STFactionTitleResult.Changed, entry);
        });

        if (result == STFactionTitleResult.Changed)
        {
            _adminLog.Add(LogType.Identity, LogImpact.Medium,
                $"Admin {adminName} set the {bandId} faction title of {targetUserId} to '{title}' (locked: {locked})");
            RaiseLocalEvent(new STFactionTitleAdminChangedEvent(targetUserId, bandId));
        }

        return result;
    }

    // Changes the admin lock without changing titles
    public async Task<STFactionTitleResult> SetLockedAsync(
        Guid targetUserId,
        string bandId,
        bool locked,
        Guid? adminUserId,
        string adminName)
    {
        var result = await EnqueueAsync(targetUserId, bandId, current =>
        {
            if ((current?.AdminLocked ?? false) == locked)
                return new TitleDecision(STFactionTitleResult.Unchanged, null);

            var title = current?.Title ?? string.Empty;
            var entry = title.Length == 0 && !locked ? null : new TitleEntry(title, locked, adminUserId);
            return new TitleDecision(STFactionTitleResult.Changed, entry);
        });

        if (result == STFactionTitleResult.Changed)
        {
            _adminLog.Add(LogType.Identity, LogImpact.Medium,
                $"Admin {adminName} {(locked ? "locked" : "unlocked")} the {bandId} faction title of {targetUserId}");
            RaiseLocalEvent(new STFactionTitleAdminChangedEvent(targetUserId, bandId));
        }

        return result;
    }

    // Queues removal of faction role whitelists and title in one transaction
    public Task<STFactionTitleResult> RemoveMembershipAsync(
        Guid targetUserId,
        string bandId,
        Func<Task<IReadOnlyCollection<string>?>> authorize)
    {
        List<string>? roles = null;
        return EnqueueAsync(targetUserId, bandId,
            _ => new TitleDecision(
                STFactionTitleResult.Changed,
                null,
                () => _db.RemoveStalkerBandMemberAsync(targetUserId, bandId, roles!)),
            async () =>
            {
                roles = (await authorize())?.ToList();
                return roles != null;
            });
    }

    // Removes title and any admin lock
    public async Task<STFactionTitleResult> ClearAsync(Guid targetUserId, string bandId, string? adminName = null)
    {
        var result = await EnqueueAsync(targetUserId, bandId, current => current == null
            ? new TitleDecision(STFactionTitleResult.Unchanged, null)
            : new TitleDecision(STFactionTitleResult.Changed, null));

        if (result == STFactionTitleResult.Changed)
        {
            if (adminName != null)
            {
                _adminLog.Add(LogType.Identity, LogImpact.Medium,
                    $"Admin {adminName} cleared the {bandId} faction title of {targetUserId}");
            }

            RaiseLocalEvent(new STFactionTitleAdminChangedEvent(targetUserId, bandId));
        }

        return result;
    }

    #endregion

    #region Applying to characters

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        ApplyTo(args.Mob, args.Player.UserId.UserId);
    }

    private void RefreshLive(Guid userId)
    {
        BeforeRefreshForTests?.Invoke(userId);

        if (!_mind.TryGetMind(new NetUserId(userId), out var mind) || mind.Value.Comp.OwnedEntity is not { } owned)
            return;

        if (TerminatingOrDeleted(owned))
            return;

        ApplyTo(owned, userId);
    }

    private void ApplyTo(EntityUid uid, Guid userId)
    {
        var title = string.Empty;
        if (TryComp<BandsComponent>(uid, out var bands)
            && bands.BandProto is { } bandId
            && TryGetTitle(userId, bandId.Id, out var entry))
        {
            title = entry.Title;
        }

        if (title.Length == 0 && !HasComp<STFactionTitleComponent>(uid))
            return;

        _publicName.SetFactionTitle(uid, title);
        _identity.QueueIdentityUpdate(uid);
    }

    #endregion

    #region Admin verbs

    private VerbCategory? _titleVerbs;

    private void OnGetVerbs(GetVerbsEvent<Verb> args)
    {
        if (!TryComp<ActorComponent>(args.User, out var actor))
            return;

        var admin = actor.PlayerSession;
        if (!_admin.HasAdminFlag(admin, AdminFlags.Admin))
            return;

        if (!TryComp<BandsComponent>(args.Target, out var bands) || bands.BandProto is not { } bandProtoId)
            return;

        if (!_mind.TryGetMind(args.Target, out _, out var mind) || mind.UserId is not { } targetNetId)
            return;

        var bandId = bandProtoId.Id;
        var target = targetNetId.UserId;
        var adminId = admin.UserId.UserId;
        var adminName = admin.Name;
        TryGetTitle(target, bandId, out var entry);

        // One submenu for all title actions. Priorities keep them in the order Set, Unlock, Clear.
        _titleVerbs ??= new VerbCategory("stow-factions-title-verb-category", null);

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("stow-factions-title-verb-set"),
            Message = Loc.GetString("stow-factions-title-verb-set-desc"),
            Category = _titleVerbs,
            Impact = LogImpact.Medium,
            Act = () =>
            {
                _quickDialog.OpenDialog(admin,
                    Loc.GetString("stow-factions-title-verb-category"),
                    Loc.GetString("stow-factions-title-hint", ("max", STFactionTitleRules.MaxLength)),
                    (string newTitle) => ReportToAdmin(admin,
                        SetTitleAsAdminAsync(target, bandId, newTitle, true, adminId, adminName)));
            },
        });

        if (entry == null)
            return;

        if (entry.AdminLocked)
        {
            args.Verbs.Add(new Verb
            {
                Text = Loc.GetString("stow-factions-title-verb-unlock"),
                Message = Loc.GetString("stow-factions-title-verb-unlock-desc"),
                Category = _titleVerbs,
                Priority = -1,
                Impact = LogImpact.Low,
                Act = () => ReportToAdmin(admin, SetLockedAsync(target, bandId, false, adminId, adminName)),
            });
        }

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("stow-factions-title-verb-clear"),
            Message = Loc.GetString("stow-factions-title-verb-clear-desc"),
            Category = _titleVerbs,
            Priority = -2,
            Impact = LogImpact.Low,
            Act = () => ReportToAdmin(admin, ClearAsync(target, bandId, adminName)),
        });
    }

    private async void ReportToAdmin(ICommonSession admin, Task<STFactionTitleResult> change)
    {
        var result = await change;
        _chat.DispatchServerMessage(admin, Loc.GetString(GetResultLocId(result)));
    }

    public static string GetResultLocId(STFactionTitleResult result)
    {
        return result switch
        {
            STFactionTitleResult.Changed => "stow-factions-title-saved",
            STFactionTitleResult.Unchanged => "stow-factions-title-unchanged",
            STFactionTitleResult.Locked => "stow-factions-title-locked",
            STFactionTitleResult.NotReady => "stow-factions-busy",
            STFactionTitleResult.NotAllowed => "stow-factions-leader-required",
            _ => "stow-factions-failed",
        };
    }

    #endregion
}

public sealed class STFactionTitleAdminChangedEvent(Guid userId, string bandId) : EntityEventArgs
{
    public Guid UserId { get; } = userId;
    public string BandId { get; } = bandId;
}