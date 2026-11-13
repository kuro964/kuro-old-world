using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Shared.NPC;
using Robust.Shared.Map;

namespace Content.Server._Stalker_EN.NPC.Brain;

public sealed class STMoverSystem : EntitySystem
{
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private const float SameGoal = 0.75f;

    public void MoveTo(EntityUid uid, EntityCoordinates destination, float range)
    {
        if (TryComp<NPCSteeringComponent>(uid, out var steering) &&
            (steering.Coordinates.Equals(destination) ||
             steering.Coordinates.EntityId == destination.EntityId && _transform.InRange(steering.Coordinates, destination, SameGoal)))
        {
            steering.Range = range;
            return;
        }

        _steering.Unregister(uid);
        _steering.Register(uid, destination).Range = range;
    }

    public void Stop(EntityUid uid)
    {
        if (HasComp<NPCSteeringComponent>(uid))
            _steering.Unregister(uid);
    }

    public bool Moving(EntityUid uid)
    {
        return TryComp<NPCSteeringComponent>(uid, out var steering) && steering.Status == SteeringStatus.Moving;
    }

    public bool NoPathTo(EntityUid uid, EntityUid target)
    {
        return TryComp<NPCSteeringComponent>(uid, out var steering) &&
               steering.Status == SteeringStatus.NoPath &&
               steering.Coordinates.EntityId == target;
    }

    public bool NoPath(EntityUid uid)
    {
        return TryComp<NPCSteeringComponent>(uid, out var steering) && steering.Status == SteeringStatus.NoPath;
    }
}
