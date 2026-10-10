using Content.Server.Spawners.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Spawners;
// ST:OW begin
using Content.Shared.Stacks;
using Content.Server._Stalker_OW.Food;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Containers;
// ST:OW end

namespace Content.Server.Spawners.EntitySystems;

public sealed class SpawnOnDespawnSystem : EntitySystem
{
    // ST:OW begin
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedStackSystem _stacks = default!;
    // ST:OW end
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SpawnOnDespawnComponent, TimedDespawnEvent>(OnDespawn);
    }

    // ST:OW begin
    private void OnDespawn(EntityUid uid, SpawnOnDespawnComponent comp, ref TimedDespawnEvent args)
    {
        if (!TryComp(uid, out TransformComponent? xform))
            return;

        var hadContainer = _container.TryGetContainingContainer(
            (uid, xform, null),
            out var containingContainer);

        var mapCoords = _transform.GetMapCoordinates(uid, xform);
        var replacement = Spawn(comp.Prototype, mapCoords);

        // Stackable meats that are cured will produce the same amount of jerky pieces
        // This does not affect other SpawnOnDespawn transformations
        if (HasComp<STOWCuringStackComponent>(uid) &&
            TryComp<StackComponent>(uid, out var sourceStack) &&
            TryComp<StackComponent>(replacement, out var resultStack))
        {
            _stacks.SetCount((replacement, resultStack), _stacks.GetCount((uid, sourceStack)));
        }

        Transform(replacement).LocalRotation = xform.LocalRotation;

        if (!hadContainer || containingContainer == null)
            return;

        var hadStorageLocation = _storage.TryGetStorageLocation(
            uid,
            out var storageContainer,
            out var storage,
            out var storageLocation);

        _container.Remove(uid, containingContainer, force: true);

        if (hadStorageLocation &&
            storageContainer != null &&
            storage != null &&
            _storage.InsertAt(
                (storageContainer.Owner, storage),
                replacement,
                storageLocation,
                out _,
                playSound: false,
                stackAutomatically: false))
        {
            return;
        }

        _container.Insert(replacement, containingContainer);
    }
    // ST:OW end

    public void SetPrototype(Entity<SpawnOnDespawnComponent> entity, EntProtoId prototype)
    {
        entity.Comp.Prototype = prototype;
    }
}
