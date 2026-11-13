using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Events;

namespace Content.Shared._Stalker_EN.Cover;

public sealed class STLowCoverSystem : EntitySystem
{
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<STLowCoverComponent, PreventCollideEvent>(OnPreventCollide);
    }

    private void OnPreventCollide(Entity<STLowCoverComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled || !TryComp<ProjectileComponent>(args.OtherEntity, out var projectile))
            return;

        var aimed = CompOrNull<TargetedProjectileComponent>(args.OtherEntity)?.Target;
        if (aimed == ent.Owner)
            return;

        if (aimed is { } target && !TerminatingOrDeleted(target) && IsBehind(ent, _transform.GetMapCoordinates(target), 0) ||
            projectile.Shooter is { } shooter && !TerminatingOrDeleted(shooter) && IsBehind(ent, _transform.GetMapCoordinates(shooter)))
        {
            args.Cancelled = true;
        }
    }

    public bool IsBehind(Entity<STLowCoverComponent> cover, MapCoordinates coordinates)
    {
        return IsBehind(cover, coordinates, cover.Comp.Range);
    }

    public bool IsBehind(Entity<STLowCoverComponent> cover, MapCoordinates coordinates, int range)
    {
        var xform = Transform(cover);
        if (xform.GridUid is not { } grid ||
            !TryComp<MapGridComponent>(grid, out var gridComp) ||
            xform.MapID != coordinates.MapId)
        {
            return false;
        }

        var offset = _map.TileIndicesFor(grid, gridComp, xform.Coordinates) - _map.TileIndicesFor(grid, gridComp, coordinates);
        return Math.Max(Math.Abs(offset.X), Math.Abs(offset.Y)) <= range;
    }
}
