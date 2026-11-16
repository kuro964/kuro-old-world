using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Movement.Systems;
using Content.Shared.Standing;

namespace Content.Shared._Stalker_EN.Movement;

public sealed class BarefootSpeedModifierSystem : EntitySystem
{
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movement = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;

    private const string ShoesSlot = "shoes";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BarefootSpeedModifierComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<BarefootSpeedModifierComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<BarefootSpeedModifierComponent, DidEquipEvent>(OnEquipped);
        SubscribeLocalEvent<BarefootSpeedModifierComponent, DidUnequipEvent>(OnUnequipped);
        SubscribeLocalEvent<BarefootSpeedModifierComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshSpeed);
    }

    private void OnMapInit(Entity<BarefootSpeedModifierComponent> ent, ref MapInitEvent args)
    {
        _movement.RefreshMovementSpeedModifiers(ent);
    }

    private void OnShutdown(Entity<BarefootSpeedModifierComponent> ent, ref ComponentShutdown args)
    {
        _movement.RefreshMovementSpeedModifiers(ent);
    }

    private void OnEquipped(Entity<BarefootSpeedModifierComponent> ent, ref DidEquipEvent args)
    {
        if (args.Slot == ShoesSlot)
            _movement.RefreshMovementSpeedModifiers(ent);
    }

    private void OnUnequipped(Entity<BarefootSpeedModifierComponent> ent, ref DidUnequipEvent args)
    {
        if (args.Slot == ShoesSlot)
            _movement.RefreshMovementSpeedModifiers(ent);
    }

    private void OnRefreshSpeed(Entity<BarefootSpeedModifierComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (ent.Comp.LifeStage > ComponentLifeStage.Running ||
            _standing.IsDown(ent.Owner) ||
            !_inventory.HasSlot(ent, ShoesSlot) ||
            _inventory.TryGetSlotEntity(ent, ShoesSlot, out _))
        {
            return;
        }

        args.ModifySpeed(ent.Comp.SpeedModifier);
    }
}
