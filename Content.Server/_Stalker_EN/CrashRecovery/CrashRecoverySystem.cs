using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server._Stalker.Storage;
using Content.Server.Database;
using Content.Server.GameTicking.Rules;
using Content.Server.Popups;
using Content.Shared._Stalker.StalkerRepository;
using Content.Shared._Stalker.Storage;
using Content.Shared._Stalker_EN.CCVar;
using Content.Shared._Stalker_EN.CrashRecovery;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Part;
using Content.Shared.Clothing.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Implants.Components;
using Content.Shared.Inventory;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Popups;
using Content.Shared.StatusEffectNew.Components;
using Content.Shared.Actions.Components;
using Content.Shared.Interaction.Components;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.CrashRecovery;

public sealed class CrashRecoverySystem : GameRuleSystem<CrashRecoveryRuleComponent>
{
    private const int MaxCaptureDepth = 64;
    private static readonly TimeSpan DirtyDelay = TimeSpan.FromSeconds(3);

    [Dependency] private readonly IServerDbManager _dbManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly StalkerStorageSystem _stalkerStorage = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private ISawmill _sawmill = default!;

    private bool _enabled = true;
    private float _saveInterval = 300f;
    private float _timeSinceLastSave;

    // Concurrent operation protection for claims
    private readonly ConcurrentDictionary<EntityUid, byte> _currentlyProcessingClaims = new();

    // Logins with unclaimed recovery data — snapshots must not overwrite these
    private readonly HashSet<string> _pendingRecoveryLogins = new();

    private readonly object _writeLock = new();
    private Task _writes = Task.CompletedTask;

    private readonly Dictionary<EntityUid, TimeSpan> _dirtyMinds = new();
    private bool _ruleActive;

    public bool PlannedShutdown { get; set; }

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = Logger.GetSawmill("crash-recovery");

        _cfg.OnValueChanged(STCCVars.CrashRecoveryEnabled, v => _enabled = v, true);
        _cfg.OnValueChanged(STCCVars.CrashRecoverySaveInterval, v => _saveInterval = v, true);

        // BUI message for crash recovery claim
        SubscribeLocalEvent<StalkerRepositoryComponent, CrashRecoveryClaimMessage>(OnCrashRecoveryClaim);

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestartCleanup);
        SubscribeLocalEvent<EntInsertedIntoContainerMessage>(ev => MarkCarrier(ev.Container.Owner));
        SubscribeLocalEvent<EntRemovedFromContainerMessage>(ev => MarkCarrier(ev.Container.Owner));
        SubscribeLocalEvent<MindContainerComponent, MindAddedMessage>((_, _, ev) => MarkMind(ev.Mind));
        SubscribeLocalEvent<MindContainerComponent, MindRemovedMessage>((_, _, ev) => MarkMind(ev.Mind));
    }

    private void MarkCarrier(EntityUid uid)
    {
        if (!_enabled || !_ruleActive)
            return;

        for (var i = 0; i < MaxCaptureDepth; i++)
        {
            if (TryComp<MindContainerComponent>(uid, out var mindContainer) && mindContainer.Mind is { } mind)
            {
                MarkMind(mind);
                return;
            }

            if (!_container.TryGetContainingContainer((uid, null, null), out var container))
                return;

            uid = container.Owner;
        }
    }

    private void MarkMind(EntityUid mind)
    {
        if (_enabled && _ruleActive)
            _dirtyMinds.TryAdd(mind, _timing.CurTime);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        // Safety net; EntryPoint.Shutdown drained earlier, but writes may have occurred since.
        FlushPendingWrites(TimeSpan.FromSeconds(5));

        _cfg.UnsubValueChanged(STCCVars.CrashRecoveryEnabled, v => _enabled = v);
        _cfg.UnsubValueChanged(STCCVars.CrashRecoverySaveInterval, v => _saveInterval = v);
    }

    /// <summary>
    /// Call BEFORE EntryPoint.Dispose so the Npgsql pool isn't saturated when it runs ClearAllCrashRecovery.
    /// </summary>
    public bool FlushPendingWrites(TimeSpan timeout)
    {
        var pending = PendingWrites();
        if (pending.IsCompleted)
        {
            _sawmill.Info("[shutdown] CrashRecoverySystem: no pending writes");
            return true;
        }

        _sawmill.Info("[shutdown] CrashRecoverySystem flushing pending writes");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var completed = false;
        try
        {
            completed = pending.Wait(timeout);
        }
        catch (Exception e)
        {
            _sawmill.Error($"[shutdown] CrashRecoverySystem error flushing writes: {e}");
        }
        sw.Stop();
        _sawmill.Info($"[shutdown] CrashRecoverySystem flush {(completed ? "completed" : "TIMED OUT")} after {sw.ElapsedMilliseconds}ms");
        return completed;
    }

    private Task PendingWrites()
    {
        lock (_writeLock)
            return _writes;
    }

    private void TrackDbWrite(Func<Task> dbCall, string label)
    {
        lock (_writeLock)
            _writes = RunAfter(_writes, dbCall, label);
    }

    private async Task RunAfter(Task previous, Func<Task> dbCall, string label)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await dbCall().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _sawmill.Error($"{label}: {e}");
        }
    }

    #region Game Rule Lifecycle

    protected override void Started(EntityUid uid, CrashRecoveryRuleComponent component,
        GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);
        _timeSinceLastSave = 0;
        _pendingRecoveryLogins.Clear();
        _ruleActive = true;
        _sawmill.Info("Crash recovery game rule started");

        // Load logins with unclaimed recovery data so periodic snapshots don't overwrite them
        LoadPendingRecoveryLogins();
    }

    private async void LoadPendingRecoveryLogins()
    {
        try
        {
            await PendingWrites();

            var logins = await _dbManager.GetAllCrashRecoveryLogins();
            foreach (var login in logins)
                _pendingRecoveryLogins.Add(login);

            if (logins.Count > 0)
                _sawmill.Info($"Loaded {logins.Count} pending crash recovery logins");
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to load pending crash recovery logins: {e}");
        }
    }

    private void OnRoundRestartCleanup(RoundRestartCleanupEvent ev)
    {
        var wasActive = _ruleActive;
        _ruleActive = false;
        _dirtyMinds.Clear();

        if (!_enabled || !wasActive || !GameTicker.IsGameRuleActive<CrashRecoveryRuleComponent>())
            return;

        _sawmill.Info("Clearing all crash recovery data (round restart)");
        TrackDbWrite(() => _dbManager.ClearAllCrashRecovery(), "ClearAllCrashRecovery(round-restart)");
        _pendingRecoveryLogins.Clear();
        _dirtyMinds.Clear();
        _timeSinceLastSave = 0;
    }

    protected override void ActiveTick(EntityUid uid, CrashRecoveryRuleComponent component,
        GameRuleComponent gameRule, float frameTime)
    {
        if (!_enabled)
            return;

        SnapshotDirty();

        if (_saveInterval <= 0)
            return;

        _timeSinceLastSave += frameTime;
        if (_timeSinceLastSave < _saveInterval)
            return;

        _timeSinceLastSave = 0;
        SnapshotAll("periodic");
    }

    #endregion

    #region Crash Recovery Check (BUI Open)

    /// <summary>
    /// Called from StalkerRepositorySystem.OnBeforeActivate when stash UI opens.
    /// Async check — state arrives on next tick, after RepositoryUpdateState.
    /// </summary>
    public async void CheckAndSendCrashRecoveryState(EntityUid repository, EntityUid actor)
    {
        if (!_enabled)
            return;

        if (!TryComp<ActorComponent>(actor, out var actorComp))
            return;

        // Don't show banner if a claim is already being processed
        if (_currentlyProcessingClaims.ContainsKey(actor))
            return;

        var login = actorComp.PlayerSession.Name;

        // Only show recovery for data that existed at round start (from a crash).
        // Data written during the current session by periodic/immediate snapshots is not recoverable.
        if (!_pendingRecoveryLogins.Contains(login))
            return;

        try
        {
            var json = await _dbManager.GetCrashRecovery(login);

            if (!Exists(repository) || !Exists(actor))
                return;

            if (string.IsNullOrEmpty(json))
            {
                _pendingRecoveryLogins.Remove(login);
                return;
            }

            var itemCount = 0;
            try
            {
                var data = JsonSerializer.Deserialize<CrashRecoveryData>(json);
                if (data != null && !string.IsNullOrEmpty(data.EquippedJson))
                {
                    var equipped = StalkerStorageSystem.InventoryFromJson(data.EquippedJson);
                    itemCount = equipped.AllItems.Count;
                }
            }
            catch
            {
                // Malformed data, still show banner with 0 count
            }

            _ui.SetUiState(repository, StalkerRepositoryUiKey.Key,
                new CrashRecoveryUpdateState(true, itemCount));
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to check crash recovery for {login}: {e}");
        }
    }

    #endregion

    #region Claim Handler — Spawn Items at Feet

    private async void OnCrashRecoveryClaim(EntityUid uid, StalkerRepositoryComponent component,
        CrashRecoveryClaimMessage msg)
    {
        if (msg.Actor == EntityUid.Invalid)
            return;

        if (!_currentlyProcessingClaims.TryAdd(msg.Actor, 0))
            return;

        try
        {
            if (!TryComp<ActorComponent>(msg.Actor, out var actorComp))
                return;

            var login = actorComp.PlayerSession.Name;

            if (!_pendingRecoveryLogins.Contains(login))
                return;

            var json = await _dbManager.GetCrashRecovery(login);
            if (string.IsNullOrEmpty(json))
                return;

            AllStorageInventory equipped;
            try
            {
                var data = JsonSerializer.Deserialize<CrashRecoveryData>(json);
                if (data == null || string.IsNullOrEmpty(data.EquippedJson))
                {
                    _sawmill.Error($"Empty crash recovery data for {login}, discarding");
                    await _dbManager.SetCrashRecovery(login, null);
                    _pendingRecoveryLogins.Remove(login);
                    return;
                }

                equipped = StalkerStorageSystem.InventoryFromJson(data.EquippedJson);
            }
            catch (Exception e)
            {
                _sawmill.Error($"Failed to deserialize crash recovery for {login}, keeping it for admins: {e}");
                return;
            }

            if (!Exists(uid) || !Exists(msg.Actor))
                return;

            await _dbManager.SetCrashRecovery(login, null);
            _pendingRecoveryLogins.Remove(login);

            if (!Exists(uid) || !Exists(msg.Actor))
            {
                await _dbManager.SetCrashRecovery(login, json);
                _pendingRecoveryLogins.Add(login);
                return;
            }

            var coords = Transform(msg.Actor).Coordinates;
            var spawnedCount = 0;

            foreach (var item in equipped.AllItems)
            {
                if (item is not IItemStalkerStorage storageItem)
                    continue;

                if (string.IsNullOrEmpty(storageItem.PrototypeName))
                    continue;

                try
                {
                    var entity = Spawn(storageItem.PrototypeName, coords);
                    _stalkerStorage.SpawnedItem(entity, storageItem);
                    spawnedCount++;
                }
                catch (Exception e)
                {
                    _sawmill.Warning($"Failed to spawn recovery item {storageItem.PrototypeName}: {e}");
                }
            }

            _sawmill.Info($"Crash recovery: spawned {spawnedCount} items for {login}");

            _popup.PopupEntity(Loc.GetString("crash-recovery-claimed"), msg.Actor, msg.Actor,
                PopupType.Medium);
        }
        catch (Exception e)
        {
            _sawmill.Error($"Failed to process crash recovery claim: {e}");
        }
        finally
        {
            _currentlyProcessingClaims.TryRemove(msg.Actor, out _);
        }
    }

    #endregion

    #region Periodic Snapshot

    /// <summary>
    /// Manually trigger an immediate snapshot for all online players.
    /// Called by the crash_recovery_snapshot admin command.
    /// </summary>
    public void ForceSnapshot()
    {
        _sawmill.Info("Force snapshot triggered by admin command");
        SnapshotAll("force");
    }

    private void SnapshotAll(string reason)
    {
        var batch = new Dictionary<string, string?>();
        var skipped = 0;
        var query = EntityQueryEnumerator<MindComponent>();

        while (query.MoveNext(out var mindId, out var mind))
        {
            _dirtyMinds.Remove(mindId);
            if (CaptureMind(mind, batch))
                skipped++;
        }

        WriteBatch(batch, reason);

        if (reason == "force")
            _sawmill.Info($"Force snapshot saved {batch.Count} players, skipped {skipped} with unclaimed recovery");
    }

    private void SnapshotDirty()
    {
        if (_dirtyMinds.Count == 0)
            return;

        var now = _timing.CurTime;
        var batch = new Dictionary<string, string?>();

        foreach (var (mindId, since) in _dirtyMinds)
        {
            if (now - since < DirtyDelay)
                continue;

            _dirtyMinds.Remove(mindId);
            if (TryComp<MindComponent>(mindId, out var mind))
                CaptureMind(mind, batch);
        }

        WriteBatch(batch, "changed");
    }

    private void WriteBatch(Dictionary<string, string?> batch, string reason)
    {
        if (batch.Count > 0)
            TrackDbWrite(() => _dbManager.SetCrashRecoveryBatch(batch), $"SetCrashRecoveryBatch({reason},{batch.Count})");
    }

    private bool CaptureMind(MindComponent mind, Dictionary<string, string?> batch)
    {
        if (mind.UserId is not { } userId || !_playerManager.TryGetPlayerData(userId, out var player))
            return false;

        var login = player.UserName;

        if (_pendingRecoveryLogins.Contains(login))
            return true;

        string? data = null;
        if (mind.OwnedEntity is { } body && Exists(body) && HasComp<InventoryComponent>(body))
        {
            try
            {
                data = CapturePlayerState(body);
            }
            catch (Exception e)
            {
                _sawmill.Error($"Failed to snapshot player {login}: {e}");
                return false;
            }
        }

        if (data != null || !batch.ContainsKey(login))
            batch[login] = data;

        return false;
    }

    private string? CapturePlayerState(EntityUid player)
    {
        var equippedInventory = new AllStorageInventory();

        if (!_inventory.TryGetContainerSlotEnumerator(player, out var enumerator))
            return null;

        while (enumerator.NextItem(out var item, out _))
        {
            CaptureItemRecursive(item, equippedInventory);
        }

        foreach (var held in _hands.EnumerateHeld(player))
        {
            CaptureItemRecursive(held, equippedInventory);
        }

        if (equippedInventory.AllItems.Count == 0)
            return null;

        var equippedJson = StalkerStorageSystem.InventoryToJson(equippedInventory);
        var recoveryData = new CrashRecoveryData
        {
            EquippedJson = equippedJson,
        };

        return JsonSerializer.Serialize(recoveryData);
    }

    private void CaptureItemRecursive(EntityUid item, AllStorageInventory inventory, int depth = 0)
    {
        if (depth > MaxCaptureDepth)
            return;

        // Blacklist (same as StalkerRepositorySystem.GetRecursiveContainerElements)
        if (HasComp<OrganComponent>(item) ||
            HasComp<InstantActionComponent>(item) ||
            HasComp<WorldTargetActionComponent>(item) ||
            HasComp<EntityTargetActionComponent>(item) ||
            HasComp<SubdermalImplantComponent>(item) ||
            HasComp<BodyPartComponent>(item) ||
            HasComp<VirtualItemComponent>(item) ||
            HasComp<MindContainerComponent>(item) ||
            HasComp<StatusEffectComponent>(item) ||
            HasComp<UnremoveableComponent>(item) ||
            HasComp<SelfUnremovableClothingComponent>(item))
            return;

        var converted = _stalkerStorage.ConvertToIItemStalkerStorage(item);
        foreach (var obj in converted)
        {
            if (obj is IItemStalkerStorage)
                inventory.AllItems.Add(obj);
        }

        if (!TryComp<ContainerManagerComponent>(item, out var containerMan))
            return;

        foreach (var container in containerMan.Containers)
        {
            if (container.Key is "toggleable-clothing" or "program-container")
                continue;

            foreach (var contained in container.Value.ContainedEntities)
            {
                if (_stalkerStorage.IsStoredInOwnerRecord(item, contained)) // ST:OW - Also covers revolver cylinders
                    continue;

                CaptureItemRecursive(contained, inventory, depth + 1);
            }
        }
    }

    /// <summary>
    /// Immediately snapshots a player's equipped state to DB.
    /// Called when stash contents change to keep crash recovery in sync.
    /// </summary>
    public void ImmediateSnapshot(EntityUid player)
    {
        if (!_enabled)
            return;

        if (!_mind.TryGetMind(player, out var mindId, out var mind))
            return;

        _dirtyMinds.Remove(mindId);
        var batch = new Dictionary<string, string?>();
        CaptureMind(mind, batch);
        WriteBatch(batch, "immediate");
    }

    #endregion
}
