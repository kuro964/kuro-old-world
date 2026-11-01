using System.Linq;
using Content.Shared._CD.Engraving;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Labels.Components;
using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Shared._Stalker_OW.Workshop;

[ByRefEvent]
public readonly record struct EquipmentModulesChangedEvent;

public sealed class SharedEquipmentModificationSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;

    private static readonly DamageModifierSet Empty = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<EquipmentModuleComponent, BeingEquippedAttemptEvent>(OnModuleEquipAttempt);
        SubscribeLocalEvent<EquipmentModificationComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<EquipmentModificationComponent, AfterAutoHandleStateEvent>(OnHandleState);
        SubscribeLocalEvent<EquipmentModificationComponent, ExaminedEvent>(OnExamined);
    }

    private void OnModuleEquipAttempt(EntityUid uid, EquipmentModuleComponent comp, BeingEquippedAttemptEvent args)
    {
        // Modules can be carried but cannot be equipped w/o using the workbench
        if ((args.SlotFlags & SlotFlags.POCKET) == 0)
            args.Cancel();
    }

    private void OnStartup(Entity<EquipmentModificationComponent> ent, ref ComponentStartup args)
    {
        Normalize(ent.Comp);
        Rebuild(ent);
    }

    private void OnHandleState(Entity<EquipmentModificationComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        Rebuild(ent);
    }

    private void OnExamined(Entity<EquipmentModificationComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.Stored.Count > 0)
            args.PushMarkup(Loc.GetString("workshop-examine-stored", ("count", ent.Comp.Stored.Count)));
    }

    // Keep the installed list at the slot count and store any extra modules
    private static void Normalize(EquipmentModificationComponent comp)
    {
        var count = comp.SlotCount;
        for (var i = count; i < comp.Installed.Count; i++)
        {
            if (comp.Installed[i] is { } extra)
                comp.Stored.Add(extra);
        }

        if (comp.Installed.Count > count)
            comp.Installed.RemoveRange(count, comp.Installed.Count - count);
        while (comp.Installed.Count < count)
            comp.Installed.Add(null);
    }

    private void Rebuild(Entity<EquipmentModificationComponent> ent)
    {
        ent.Comp.Cached = Calculate(ent.Comp.Installed);
        var ev = new EquipmentModulesChangedEvent();
        RaiseLocalEvent(ent, ref ev);
    }

    #region Queries

    public EntProtoId? GetInstalled(EquipmentModificationComponent comp, int slot)
    {
        return slot >= 0 && slot < comp.Installed.Count ? comp.Installed[slot] : null;
    }

    public EquipmentModuleComponent? GetModule(EntProtoId? id)
    {
        return id is { } proto && _proto.TryIndex(proto, out var prototype) &&
               prototype.TryGetComponent<EquipmentModuleComponent>(out var module, EntityManager.ComponentFactory)
            ? module
            : null;
    }

    public ArmorModuleEffectComponent? GetArmorEffect(EntProtoId? id)
    {
        return id is { } proto && _proto.TryIndex(proto, out var prototype) &&
               prototype.TryGetComponent<ArmorModuleEffectComponent>(out var effect, EntityManager.ComponentFactory)
            ? effect
            : null;
    }

    // Check for item data that would be lost when the module is consumed (i.e. engravings)
    public bool HasInstanceData(EntityUid uid)
    {
        if (TryComp<LabelComponent>(uid, out var label) && !string.IsNullOrEmpty(label.CurrentLabel))
            return true;

        if (TryComp<EngraveableComponent>(uid, out var engraving) && !string.IsNullOrEmpty(engraving.EngravedMessage))
            return true;

        return TryComp<ContainerManagerComponent>(uid, out var manager) &&
               manager.Containers.Values.Any(c => c.ContainedEntities.Count != 0);
    }

    public bool TryGetModulePrototype(EntityUid uid, out EntProtoId proto)
    {
        proto = default;
        if (TerminatingOrDeleted(uid) ||
            !HasComp<EquipmentModuleComponent>(uid) ||
            HasComp<StackComponent>(uid) ||
            HasComp<EquipmentModificationComponent>(uid) ||
            HasInstanceData(uid) ||
            MetaData(uid).EntityPrototype is not { } prototype ||
            !IsValidModulePrototype(prototype.ID))
            return false;

        proto = prototype.ID;
        return true;
    }

    private bool IsValidModulePrototype(EntProtoId id)
    {
        if (GetModule(id) is not { } module || !float.IsFinite(module.InstallTime) || module.InstallTime <= 0)
            return false;

        return GetArmorEffect(id) is not { } effect ||
               (effect.Modifiers.Coefficients.Count > 0 || effect.Modifiers.FlatReduction.Count > 0) &&
               HasValidModifiers(effect.Modifiers);
    }

    public List<EntProtoId?> WithChanges(EquipmentModificationComponent comp, IReadOnlyDictionary<int, EntProtoId?>? changes)
    {
        var result = new List<EntProtoId?>(comp.Installed);
        if (changes == null)
            return result;

        foreach (var (slot, proto) in changes)
        {
            if (slot >= 0 && slot < result.Count)
                result[slot] = proto;
        }

        return result;
    }

    public bool IsValidConfiguration(EquipmentModificationComponent comp, IReadOnlyList<EntProtoId?> config)
    {
        if (config.Count != comp.SlotCount)
            return false;

        var seen = new HashSet<EntProtoId>();
        foreach (var entry in config)
        {
            if (entry is not { } proto)
                continue;

            if (!IsValidModulePrototype(proto))
                return false;

            if (!seen.Add(proto) && GetModule(proto)!.Exclusive)
                return false;
        }

        return HasValidModifiers(Calculate(config));
    }

    private static bool HasValidModifiers(DamageModifierSet set) =>
        set.Coefficients.Values.All(c => float.IsFinite(c) && c > 0) &&
        set.FlatReduction.Values.All(flat => float.IsFinite(flat) && flat >= 0);

    // Checks that the selected slots change and the resulting configuration is valid
    public bool CanApply(EquipmentModificationComponent comp, IReadOnlyDictionary<int, EntProtoId?> changes)
    {
        if (changes.Count == 0)
            return false;

        foreach (var (slot, proto) in changes)
        {
            if (slot < 0 || slot >= comp.Installed.Count || comp.Installed[slot] == proto)
                return false;
        }

        return IsValidConfiguration(comp, WithChanges(comp, changes));
    }

    public DamageModifierSet Calculate(IEnumerable<EntProtoId?> config)
    {
        var result = new DamageModifierSet();
        foreach (var entry in config)
        {
            if (GetArmorEffect(entry) is not { } effect)
                continue;

            foreach (var (type, coefficient) in effect.Modifiers.Coefficients)
            {
                if (!float.IsFinite(coefficient) || coefficient <= 0)
                    continue;

                result.Coefficients[type] = result.Coefficients.GetValueOrDefault(type, 1f) * coefficient;
            }

            foreach (var (type, flat) in effect.Modifiers.FlatReduction)
            {
                if (!float.IsFinite(flat) || flat < 0)
                    continue;

                result.FlatReduction[type] = result.FlatReduction.GetValueOrDefault(type) + flat;
            }
        }

        return result;
    }

    // Gets installed modifiers or calculates them with pending changes
    public DamageModifierSet GetArmorModifiers(EntityUid uid, IReadOnlyDictionary<int, EntProtoId?>? changes = null)
    {
        if (!TryComp<EquipmentModificationComponent>(uid, out var comp))
            return Empty;

        return changes == null || changes.Count == 0 ? comp.Cached : Calculate(WithChanges(comp, changes));
    }

    // Save installed slots first, followed by modules that no longer fit
    public List<string?> GetSaveData(EquipmentModificationComponent comp)
    {
        var result = comp.Installed.Select(m => m?.Id).ToList();
        result.AddRange(comp.Stored.Select(m => (string?) m.Id));
        return result;
    }

    #endregion

    #region Writes

    // Updates the installed list & caller handles consuming and returning items
    public bool SetInstalled(Entity<EquipmentModificationComponent> ent, IReadOnlyDictionary<int, EntProtoId?> changes)
    {
        if (!CanApply(ent.Comp, changes))
            return false;

        foreach (var (slot, proto) in changes)
            ent.Comp.Installed[slot] = proto;

        Dirty(ent);
        Rebuild(ent);
        return true;
    }
    
    public void Restore(Entity<EquipmentModificationComponent?> ent, IReadOnlyList<EntProtoId?> saved)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return;

        var comp = ent.Comp;
        comp.Stored.Clear();
        comp.Installed.Clear();
        for (var i = 0; i < comp.SlotCount; i++)
            comp.Installed.Add(null);

        var exclusive = new HashSet<EntProtoId>();
        for (var i = 0; i < saved.Count; i++)
        {
            if (saved[i] is not { } proto)
                continue;

            if (!_proto.TryIndex(proto, out _))
            {
                Log.Warning($"Dropping saved module {proto} on {ToPrettyString(ent.Owner)}: prototype no longer exists");
                continue;
            }

            var fits = i < comp.SlotCount &&
                       IsValidModulePrototype(proto) &&
                       !(GetModule(proto)!.Exclusive && !exclusive.Add(proto));
            if (fits)
                comp.Installed[i] = proto;
            else
                comp.Stored.Add(proto);
        }

        for (var i = comp.Installed.Count - 1; i >= 0 && !HasValidModifiers(Calculate(comp.Installed)); i--)
        {
            if (comp.Installed[i] is not { } proto)
                continue;

            comp.Installed[i] = null;
            comp.Stored.Add(proto);
        }

        if (comp.Stored.Count > 0)
            Log.Info($"Stored {comp.Stored.Count} module(s) that no longer fit on {ToPrettyString(ent.Owner)}");

        Dirty(ent.Owner, comp);
        Rebuild((ent.Owner, comp));
    }

    public List<EntProtoId> TakeStored(Entity<EquipmentModificationComponent> ent)
    {
        if (ent.Comp.Stored.Count == 0)
            return new List<EntProtoId>();

        var stored = new List<EntProtoId>(ent.Comp.Stored);
        ent.Comp.Stored.Clear();
        Dirty(ent);
        Rebuild(ent);
        return stored;
    }

    #endregion
}
