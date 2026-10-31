using System.Linq;
using Content.Server.Popups;
using Content.Shared._Stalker_OW.Workshop;
using Content.Shared.ActionBlocker;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_OW.Workshop;

public sealed class WorkshopSystem : EntitySystem
{
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedEquipmentModificationSystem _modules = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private static readonly SoundSpecifier WorkSound =
        new SoundCollectionSpecifier("Screwdriver", AudioParams.Default.WithVolume(-6));

    public override void Initialize()
    {
        SubscribeLocalEvent<EquipmentModificationWorkbenchComponent, ComponentInit>(OnInit);
        SubscribeLocalEvent<EquipmentModificationWorkbenchComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<EquipmentModificationWorkbenchComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<EquipmentModificationWorkbenchComponent, WorkshopMessage>(OnMessage);
        SubscribeLocalEvent<EquipmentModificationWorkbenchComponent, WorkshopDoAfterEvent>(OnDoAfter);
        SubscribeLocalEvent<EquipmentModificationWorkbenchComponent, EntInsertedIntoContainerMessage>(OnInserted);
        SubscribeLocalEvent<EquipmentModificationWorkbenchComponent, EntRemovedFromContainerMessage>(OnRemoved);
        SubscribeLocalEvent<EquipmentModificationWorkbenchComponent, EntityTerminatingEvent>(OnTerminating);
        SubscribeLocalEvent<EquipmentModificationWorkbenchComponent, EntityUnpausedEvent>(OnUnpaused);
    }

    #region Events

    private void OnInit(Entity<EquipmentModificationWorkbenchComponent> ent, ref ComponentInit args)
    {
        ent.Comp.Pending = _containers.EnsureContainer<Container>(ent, EquipmentModificationWorkbenchComponent.PendingContainerId);
    }

    private void OnUnpaused(Entity<EquipmentModificationWorkbenchComponent> ent, ref EntityUnpausedEvent args)
    {
        if (ent.Comp.DoAfter == null)
            return;

        ent.Comp.Started += args.PausedTime;
        ent.Comp.Finishes += args.PausedTime;
        UpdateUi(ent);
    }

    private void OnUiOpened(Entity<EquipmentModificationWorkbenchComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUi(ent);
    }

    private void OnUiClosed(Entity<EquipmentModificationWorkbenchComponent> ent, ref BoundUIClosedEvent args)
    {
        // Closing the window returns pending modules
        if (!Equals(args.UiKey, WorkshopUiKey.Key) || ent.Comp.Worker != args.Actor || TerminatingOrDeleted(ent))
            return;

        ClearJob(ent);
        UpdateUi(ent);
    }

    private void OnInserted(Entity<EquipmentModificationWorkbenchComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID != EquipmentModificationWorkbenchComponent.TargetSlotId)
            return;

        ReturnStoredModules(ent, args.Entity);
        UpdateUi(ent);
    }

    private void OnRemoved(Entity<EquipmentModificationWorkbenchComponent> ent, ref EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID != EquipmentModificationWorkbenchComponent.TargetSlotId || TerminatingOrDeleted(ent))
            return;

        // Clear pending changes if the equipment is removed or deleted
        ClearJob(ent);
        UpdateUi(ent);
    }

    // Returns saved modules that no longer fit as items
    private void ReturnStoredModules(EntityUid bench, EntityUid equipment)
    {
        if (!TryComp<EquipmentModificationComponent>(equipment, out var comp) || comp.Stored.Count == 0)
            return;

        var coordinates = Transform(bench).Coordinates;
        foreach (var proto in _modules.TakeStored((equipment, comp)))
            _transform.DropNextTo(Spawn(proto, coordinates), bench);

        _popup.PopupEntity(Loc.GetString("workshop-stored-returned"), bench);
    }

    private void OnTerminating(Entity<EquipmentModificationWorkbenchComponent> ent, ref EntityTerminatingEvent args)
    {
        // Skip returning items during map deletion or round cleanup
        if (TerminatingOrDeleted(Transform(ent).MapUid))
            return;

        CancelDoAfter(ent.Comp);
        foreach (var container in new BaseContainer?[] { ent.Comp.Pending, GetTargetContainer(ent) })
        {
            if (container == null)
                continue;

            foreach (var item in container.ContainedEntities.ToArray())
                _transform.DropNextTo(item, ent.Owner);
        }

        ent.Comp.Changes.Clear();
        ent.Comp.Worker = null;
    }

    // Actions update the UI through their handlers
    private void OnMessage(Entity<EquipmentModificationWorkbenchComponent> ent, ref WorkshopMessage msg)
    {
        var user = msg.Actor;
        switch (msg.Action)
        {
            case WorkshopAction.Cancel:
                Cancel(ent.Owner, user);
                break;
            case WorkshopAction.Insert:
                if (CanAct(ent, user) && ent.Comp.DoAfter == null && ent.Comp.Changes.Count == 0 &&
                    GetSlot(ent) is { } insertSlot && !_itemSlots.TryInsertFromHand(ent, insertSlot, user))
                {
                    Popup(ent, user, "workshop-hold-equipment");
                }
                break;
            case WorkshopAction.Eject:
                if (!CanAct(ent, user) || ent.Comp.DoAfter != null)
                    break;
                var hadJob = ent.Comp.Worker != null;
                ClearJob(ent);
                if ((GetSlot(ent) is not { } ejectSlot || !_itemSlots.TryEjectToHands(ent, ejectSlot, user)) && hadJob)
                    UpdateUi(ent);
                break;
            case WorkshopAction.SelectSlot:
                TrySelectSlot(ent.Owner, user, msg.Slot);
                break;
            case WorkshopAction.Apply:
                TryApply(ent.Owner, user);
                break;
        }
    }

    private void OnDoAfter(Entity<EquipmentModificationWorkbenchComponent> ent, ref WorkshopDoAfterEvent args)
    {
        if (args.Handled || args.Token != ent.Comp.Token || ent.Comp.DoAfter == null)
            return;

        args.Handled = true;
        ent.Comp.DoAfter = null;

        // Keep interrupted changes pending so the player can retry
        if (args.Cancelled)
        {
            UpdateUi(ent);
            return;
        }

        if (ent.Comp.Worker is not { } user || !CanUse(user, ent) ||
            !TryGetJob(ent, out var target, out var changes))
        {
            Fail(ent);
            UpdateUi(ent);
            return;
        }

        var replaced = changes.Keys
            .Select(slot => _modules.GetInstalled(target.Comp, slot))
            .ToList();

        if (!_modules.SetInstalled(target, changes))
        {
            Fail(ent);
            UpdateUi(ent);
            return;
        }
        
        var consumed = ent.Comp.Changes.Values.ToArray();
        ent.Comp.Changes.Clear();
        foreach (var staged in consumed)
        {
            if (staged is { } item)
                Del(item);
        }

        var coordinates = Transform(ent).Coordinates;
        foreach (var old in replaced)
        {
            if (old is { } proto)
                _hands.PickupOrDrop(user, Spawn(proto, coordinates));
        }

        ClearJob(ent);
        UpdateUi(ent);
        Popup(ent, user, "workshop-done");
    }

    #endregion

    #region Staging
    
    public bool TrySelectSlot(Entity<EquipmentModificationWorkbenchComponent?> bench, EntityUid user, int slot)
    {
        if (!Resolve(bench, ref bench.Comp, false))
            return false;

        Entity<EquipmentModificationWorkbenchComponent> ent = (bench.Owner, bench.Comp);
        if (!CanAct(ent, user) || ent.Comp.DoAfter != null)
            return false;

        var result = StageSlot(ent, user, slot);
        UpdateUi(ent);
        return result;
    }

    // Starts one DoAfter for all pending changes
    public bool TryApply(Entity<EquipmentModificationWorkbenchComponent?> bench, EntityUid user)
    {
        if (!Resolve(bench, ref bench.Comp, false))
            return false;

        Entity<EquipmentModificationWorkbenchComponent> ent = (bench.Owner, bench.Comp);
        if (!CanAct(ent, user) || ent.Comp.DoAfter != null)
            return false;

        var result = TryStart(ent, user);
        UpdateUi(ent);
        return result;
    }

    // Only the player who owns the pending changes can cancel them
    public void Cancel(Entity<EquipmentModificationWorkbenchComponent?> bench, EntityUid user)
    {
        if (!Resolve(bench, ref bench.Comp, false) || bench.Comp.Worker != user)
            return;

        ClearJob((bench.Owner, bench.Comp));
        UpdateUi((bench.Owner, bench.Comp));
    }

    // Gets pending module prototypes by slot
    public Dictionary<int, EntProtoId?> GetStagedChanges(Entity<EquipmentModificationWorkbenchComponent?> bench)
    {
        return Resolve(bench, ref bench.Comp, false) && TryGetProtoChanges(bench.Comp, out var changes)
            ? changes
            : new Dictionary<int, EntProtoId?>();
    }

    // Checks interaction range and whether another player owns the pending changes
    private bool CanAct(Entity<EquipmentModificationWorkbenchComponent> ent, EntityUid user)
    {
        if (!CanUse(user, ent))
            return false;

        if (ent.Comp.Worker is not { } worker || worker == user)
            return true;

        // Release the bench if the previous player can no longer use it
        if (CanUse(worker, ent))
        {
            Popup(ent, user, "workshop-busy");
            return false;
        }

        ClearJob(ent);
        UpdateUi(ent);
        return true;
    }

    private bool StageSlot(Entity<EquipmentModificationWorkbenchComponent> ent, EntityUid user, int slot)
    {
        if (GetTarget(ent) is not { } target ||
            !TryComp<EquipmentModificationComponent>(target, out var equipment) ||
            slot < 0 || slot >= equipment.Installed.Count)
            return false;

        if (!TryGetProtoChanges(ent.Comp, out var proposed))
        {
            Fail(ent);
            return false;
        }

        if (_hands.TryGetActiveItem(user, out var held))
        {
            if (!_modules.TryGetModulePrototype(held.Value, out var proto))
            {
                Popup(ent, user, HasComp<EquipmentModuleComponent>(held) && _modules.HasInstanceData(held.Value)
                    ? "workshop-module-has-data"
                    : "workshop-incompatible");
                return false;
            }

            if (equipment.Installed[slot] == proto)
            {
                Popup(ent, user, "workshop-same-module");
                return false;
            }

            proposed[slot] = proto;
            if (!_modules.CanApply(equipment, proposed))
            {
                Popup(ent, user, "workshop-incompatible");
                return false;
            }

            if (!_hands.TryDropIntoContainer(user, held.Value, ent.Comp.Pending))
            {
                Popup(ent, user, "workshop-failed");
                return false;
            }

            if (ent.Comp.Changes.TryGetValue(slot, out var previous) && previous is { } previousItem)
                ReturnItem(ent, user, previousItem);

            ent.Comp.Changes[slot] = held.Value;
            BeginJob(ent, user);
            return true;
        }

        // An empty hand undoes the pending change for this slot
        if (ent.Comp.Changes.TryGetValue(slot, out var staged))
        {
            proposed.Remove(slot);
            // Undoing a removal must not create an exclusive duplicate in another slot
            if (proposed.Count > 0 && !_modules.CanApply(equipment, proposed))
            {
                Popup(ent, user, "workshop-incompatible");
                return false;
            }

            ent.Comp.Changes.Remove(slot);
            if (staged is { } stagedItem)
                ReturnItem(ent, user, stagedItem);

            if (ent.Comp.Changes.Count == 0)
                ClearJob(ent);
            return true;
        }

        // An empty hand stages removal of the installed module
        if (equipment.Installed[slot] == null)
        {
            Popup(ent, user, "workshop-hold-module");
            return false;
        }

        proposed[slot] = null;
        if (!_modules.CanApply(equipment, proposed))
        {
            Popup(ent, user, "workshop-incompatible");
            return false;
        }

        ent.Comp.Changes[slot] = null;
        BeginJob(ent, user);
        return true;
    }

    private bool TryStart(Entity<EquipmentModificationWorkbenchComponent> ent, EntityUid user)
    {
        if (ent.Comp.Worker != user || ent.Comp.Changes.Count == 0)
            return false;

        if (!TryGetJob(ent, out _, out var changes))
        {
            Fail(ent);
            return false;
        }

        // Use the longest single change for the batch duration
        var duration = changes.Values.Max(proto => proto is { } module
            ? _modules.GetModule(module)?.InstallTime ?? ent.Comp.RemovalTime
            : ent.Comp.RemovalTime);

        var token = ++ent.Comp.Token;
        var args = new DoAfterArgs(EntityManager, user, duration, new WorkshopDoAfterEvent { Token = token }, ent.Owner, ent.Owner)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
            BreakOnHandChange = false,
            BreakOnDropItem = false,
            DistanceThreshold = 1.5f,
        };

        ent.Comp.Started = _timing.CurTime;
        ent.Comp.Finishes = ent.Comp.Started + TimeSpan.FromSeconds(duration);
        // Instant DoAfters can finish before TryStartDoAfter returns
        if (!_doAfter.TryStartDoAfter(args, out ent.Comp.DoAfter))
        {
            ent.Comp.DoAfter = null;
            Popup(ent, user, "workshop-failed");
            return false;
        }

        _audio.PlayPvs(WorkSound, ent);
        return true;
    }

    // Recheck the equipment and pending modules before applying changes
    private bool TryGetJob(Entity<EquipmentModificationWorkbenchComponent> ent,
        out Entity<EquipmentModificationComponent> target,
        out Dictionary<int, EntProtoId?> changes)
    {
        target = default;
        changes = new Dictionary<int, EntProtoId?>();

        if (GetTarget(ent) is not { } uid || !TryComp<EquipmentModificationComponent>(uid, out var equipment))
            return false;

        target = (uid, equipment);
        return TryGetProtoChanges(ent.Comp, out changes) && _modules.CanApply(equipment, changes);
    }

    private void BeginJob(Entity<EquipmentModificationWorkbenchComponent> ent, EntityUid user)
    {
        ent.Comp.Worker = user;
        _itemSlots.SetLock(ent, EquipmentModificationWorkbenchComponent.TargetSlotId, true);
    }

    // Cancel work and return pending modules to the player or drop them
    // The caller updates the UI afterward
    private void ClearJob(Entity<EquipmentModificationWorkbenchComponent> ent)
    {
        CancelDoAfter(ent.Comp);
        var worker = ent.Comp.Worker;
        foreach (var item in ent.Comp.Pending.ContainedEntities.ToArray())
            ReturnItem(ent, worker, item);

        ent.Comp.Changes.Clear();
        ent.Comp.Worker = null;
        _itemSlots.SetLock(ent, EquipmentModificationWorkbenchComponent.TargetSlotId, false);
    }

    private void Fail(Entity<EquipmentModificationWorkbenchComponent> ent)
    {
        var worker = ent.Comp.Worker;
        ClearJob(ent);
        if (worker is { } user && !TerminatingOrDeleted(user))
            Popup(ent, user, "workshop-failed");
    }

    private void CancelDoAfter(EquipmentModificationWorkbenchComponent comp)
    {
        var id = comp.DoAfter;
        comp.DoAfter = null;
        // Cancel runs the callback immediately, so clear the ID before calling it
        if (id != null)
            _doAfter.Cancel(id);
    }

    private void ReturnItem(EntityUid bench, EntityUid? user, EntityUid item)
    {
        if (TerminatingOrDeleted(item))
            return;

        if (user is { } u && CanUse(u, bench) && _hands.TryPickupAnyHand(u, item))
            return;

        _transform.DropNextTo(item, bench);
    }

    #endregion

    #region Helpers

    private bool CanUse(EntityUid user, EntityUid bench) =>
        !TerminatingOrDeleted(user) &&
        !TerminatingOrDeleted(bench) &&
        _blocker.CanInteract(user, bench) &&
        _interaction.InRangeUnobstructed(user, bench) &&
        _containers.IsInSameOrParentContainer(user, bench);

    private ItemSlot? GetSlot(EntityUid bench) =>
        _itemSlots.TryGetSlot(bench, EquipmentModificationWorkbenchComponent.TargetSlotId, out var slot) ? slot : null;

    private EntityUid? GetTarget(EntityUid bench) => GetSlot(bench)?.Item;

    private BaseContainer? GetTargetContainer(EntityUid bench) => GetSlot(bench)?.ContainerSlot;

    private bool TryGetProtoChanges(EquipmentModificationWorkbenchComponent comp,
        out Dictionary<int, EntProtoId?> changes)
    {
        changes = new Dictionary<int, EntProtoId?>();
        var staged = 0;
        foreach (var (slot, item) in comp.Changes)
        {
            if (item is not { } module)
            {
                changes[slot] = null;
                continue;
            }

            if (!comp.Pending.Contains(module) || !_modules.TryGetModulePrototype(module, out var proto))
                return false;

            changes[slot] = proto;
            staged++;
        }

        return staged == comp.Pending.ContainedEntities.Count;
    }

    private void Popup(EntityUid bench, EntityUid user, string key)
    {
        _popup.PopupEntity(Loc.GetString(key), bench, user);
    }

    private void UpdateUi(Entity<EquipmentModificationWorkbenchComponent> ent)
    {
        if (!TryGetProtoChanges(ent.Comp, out var changes))
        {
            Fail(ent);
            changes.Clear();
        }

        var state = new WorkshopBoundUserInterfaceState
        {
            Target = GetNetEntity(GetTarget(ent)),
            Worker = GetNetEntity(ent.Comp.Worker),
            Working = ent.Comp.DoAfter != null,
            Started = ent.Comp.Started,
            Finishes = ent.Comp.Finishes,
            Changes = changes,
        };
        _ui.SetUiState(ent.Owner, WorkshopUiKey.Key, state);
    }

    #endregion
}
