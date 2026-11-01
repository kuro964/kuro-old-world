using System.Linq; // ST:OW
using Content.Server.Damage.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;
using Content.Shared.Inventory;
using Content.Shared.Tag;
using Content.Shared._Stalker.PersonalDamage; // ST:OW
using Content.Shared._Stalker_OW.Workshop; // ST:OW
using Content.Server._Stalker_OW.Workshop; // ST:OW
using Robust.Shared.Prototypes; // ST:OW

namespace Content.Server._Stalker.PersonalDamage;

public sealed class PersonalDamageSystem : EntitySystem
{
    [Dependency] private readonly DamageableSystem _damageableSystem = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly StaminaSystem _stamina = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    // ST:OW begin
    [Dependency] private readonly IPrototypeManager _modulePrototypes = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<EquipmentModificationComponent, EquipmentModulesChangedEvent>(OnModulesChanged);
        SubscribeLocalEvent<EquipmentModificationComponent, ComponentShutdown>(OnModuleShutdown);
        _modulePrototypes.PrototypesReloaded += OnModulePrototypesReloaded;
    }

    public override void Shutdown()
    {
        _modulePrototypes.PrototypesReloaded -= OnModulePrototypesReloaded;
        base.Shutdown();
    }

    private void OnModuleShutdown(Entity<EquipmentModificationComponent> ent, ref ComponentShutdown args)
    {
        RemComp<ModulePersonalDamageComponent>(ent.Owner);
    }

    private void OnModulesChanged(Entity<EquipmentModificationComponent> ent, ref EquipmentModulesChangedEvent args)
    {
        RefreshModuleDamage(ent);
    }

    private void OnModulePrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<EntityPrototype>())
            return;

        var query = AllEntityQuery<EquipmentModificationComponent>();
        while (query.MoveNext(out var uid, out var equipment))
            RefreshModuleDamage((uid, equipment));
    }

    private void RefreshModuleDamage(Entity<EquipmentModificationComponent> ent)
    {
        if (TerminatingOrDeleted(ent.Owner))
            return;

        TryComp<ModulePersonalDamageComponent>(ent.Owner, out var timers);
        var hasEffects = false;
        for (var i = 0; i < EquipmentModificationComponent.MaxSlots; i++)
        {
            var id = i < ent.Comp.Installed.Count ? ent.Comp.Installed[i] : null;
            PersonalDamageComponent? effect = null;
            if (id is { } module && _modulePrototypes.TryIndex(module, out var prototype) &&
                prototype.TryGetComponent<PersonalDamageComponent>(out var candidate, EntityManager.ComponentFactory) &&
                float.IsFinite(candidate.Interval) && candidate.Interval > 0 &&
                (candidate.Damage.DamageDict.Values.Any(amount => amount != 0) || candidate.StaminaDamage != 0))
                effect = candidate;

            if (effect != null)
            {
                hasEffects = true;
                timers ??= EnsureComp<ModulePersonalDamageComponent>(ent.Owner);
            }

            if (timers == null)
                continue;

            if (timers.Prototypes[i] != id || !ReferenceEquals(timers.Effects[i], effect))
                timers.Elapsed[i] = 0;
            timers.Prototypes[i] = id;
            timers.Effects[i] = effect;
        }

        if (!hasEffects && timers != null && !TerminatingOrDeleted(ent.Owner))
            RemComp<ModulePersonalDamageComponent>(ent.Owner);
    }

    private void UpdateModuleDamage(float frameTime)
    {
        var query = EntityQueryEnumerator<ModulePersonalDamageComponent, TransformComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out var timers, out var transform, out var meta))
        {
            if (meta.EntityPaused || TerminatingOrDeleted(uid))
                continue;

            var wearer = transform.ParentUid;
            var worn = HasComp<InventoryComponent>(wearer) &&
                       _inventory.TryGetContainingSlot((uid, transform, meta), out var slot) &&
                       slot != null && (slot.SlotFlags & (SlotFlags.POCKET | SlotFlags.SUITSTORAGE)) == 0;
            if (!worn)
            {
                Array.Clear(timers.Elapsed);
                continue;
            }

            for (var i = 0; i < timers.Effects.Length; i++)
            {
                if (timers.Effects[i] is not { } effect)
                    continue;

                timers.Elapsed[i] += frameTime;
                if (timers.Elapsed[i] < effect.Interval)
                    continue;
                
                timers.Elapsed[i] = 0;
                if (TerminatingOrDeleted(wearer) || HasComp<PersonalDamageBlockComponent>(uid) ||
                    HasComp<PersonalDamageBlockComponent>(wearer))
                    continue;

                _damageableSystem.TryChangeDamage(wearer, effect.Damage,
                    effect.IgnoreResistances, effect.InterruptsDoAfters);
                _stamina.TakeStaminaDamage(wearer, effect.StaminaDamage);
            }
        }
    }
    // ST:OW end

    public override void Update(float frameTime)
    {
        UpdateModuleDamage(frameTime); // ST:OW
        var query = EntityQueryEnumerator<PersonalDamageComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            // ST:OW - Loose modules are inert; installed effects are handled on their wearer
            if (HasComp<EquipmentModuleComponent>(uid))
                continue;

            if (component.NextDamage > _timing.CurTime)
                continue;


            if (!IsArtifactAllowed(uid))
                continue;

            var parent = uid;
            while (!HasComp<MapComponent>(parent))
            {
                if (TerminatingOrDeleted(parent))
                    break;

                if (HasComp<PersonalDamageBlockComponent>(parent))
                    break;

                _damageableSystem.TryChangeDamage(parent, component.Damage, component.IgnoreResistances, component.InterruptsDoAfters);
                _stamina.TakeStaminaDamage(parent, component.StaminaDamage);
                parent = Transform(parent).ParentUid;
            }

            component.NextDamage = _timing.CurTime + TimeSpan.FromSeconds(component.Interval);
        }
    }

    private bool IsArtifactAllowed(EntityUid uid)
    {

        if (!TryComp<TagComponent>(uid, out var tagComp) || !_tag.HasTag(tagComp, "STArtifact"))
            return true;


        if (!TryComp<TransformComponent>(uid, out var xform) || !TryComp<MetaDataComponent>(uid, out var meta))
            return false;

        if (!_inventory.TryGetContainingSlot((uid, xform, meta), out var slotDef) || slotDef == null)
            return false;

        var name = slotDef.Name;
        return name == "artifact1" || name == "artifact2" || name == "artifact3" || name == "artifact4" || name == "artifact5";
    }
}