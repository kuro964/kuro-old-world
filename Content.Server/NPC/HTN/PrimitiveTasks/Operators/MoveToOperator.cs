using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Server.NPC.HTN.PrimitiveTasks.Operators;

/// <summary>
/// Moves an NPC to the specified target key. Hands the actual steering off to NPCSystem.Steering
/// </summary>
public sealed partial class MoveToOperator : HTNOperator, IHtnConditionalShutdown
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    private NPCSteeringSystem _steering = default!;
    private PathfindingSystem _pathfind = default!;
    private SharedTransformSystem _transform = default!;

    private const float StalePathRange = 1.5f;

    /// <summary>
    /// When to shut the task down.
    /// </summary>
    [DataField("shutdownState")]
    public HTNPlanState ShutdownState { get; private set; } = HTNPlanState.TaskFinished;

    /// <summary>
    /// Should we assume the MovementTarget is reachable during planning or should we pathfind to it?
    /// </summary>
    [DataField("pathfindInPlanning")]
    public bool PathfindInPlanning = true;

    /// <summary>
    /// When we're finished moving to the target should we remove its key?
    /// </summary>
    [DataField("removeKeyOnFinish")]
    public bool RemoveKeyOnFinish = true;

    /// <summary>
    /// Target Coordinates to move to. This gets removed after execution.
    /// </summary>
    [DataField("targetKey")]
    public string TargetKey = "TargetCoordinates";

    /// <summary>
    /// Where the pathfinding result will be stored (if applicable). This gets removed after execution.
    /// </summary>
    [DataField("pathfindKey")]
    public string PathfindKey = NPCBlackboard.PathfindKey;

    /// <summary>
    /// How close we need to get before considering movement finished.
    /// </summary>
    [DataField("rangeKey")]
    public string RangeKey = "MovementRange";

    /// <summary>
    /// Do we only need to move into line of sight.
    /// </summary>
    [DataField("stopOnLineOfSight")]
    public bool StopOnLineOfSight;

    [DataField]
    public bool AllowPartial;

    private const string MovementCancelToken = "MovementCancelToken";

    private const string PartialTargetKey = "MovementPartialTarget";

    private const string PartialHoldKey = "MovementPartialHold";

    private static readonly TimeSpan PartialHold = TimeSpan.FromSeconds(1.5);

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _pathfind = sysManager.GetEntitySystem<PathfindingSystem>();
        _steering = sysManager.GetEntitySystem<NPCSteeringSystem>();
        _transform = sysManager.GetEntitySystem<SharedTransformSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        if (!blackboard.TryGetValue<EntityCoordinates>(TargetKey, out var targetCoordinates, _entManager))
        {
            return (false, null);
        }

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.TryGetComponent<TransformComponent>(owner, out var xform) ||
            !_entManager.TryGetComponent<PhysicsComponent>(owner, out var body))
            return (false, null);

        if (!_entManager.TryGetComponent<MapGridComponent>(xform.GridUid, out var ownerGrid) ||
            !_entManager.TryGetComponent<MapGridComponent>(_transform.GetGrid(targetCoordinates), out var targetGrid))
        {
            return (false, null);
        }

        var range = blackboard.GetValueOrDefault<float>(RangeKey, _entManager);

        if (xform.Coordinates.TryDistance(_entManager, targetCoordinates, out var distance) && distance <= range)
        {
            // In range
            return (true, new Dictionary<string, object>()
            {
                {NPCBlackboard.OwnerCoordinates, blackboard.GetValueOrDefault<EntityCoordinates>(NPCBlackboard.OwnerCoordinates, _entManager)}
            });
        }

        if (!PathfindInPlanning)
        {
            return (true, new Dictionary<string, object>()
            {
                {NPCBlackboard.OwnerCoordinates, targetCoordinates}
            });
        }

        var path = await _pathfind.GetPath(
            blackboard.GetValue<EntityUid>(NPCBlackboard.Owner),
            xform.Coordinates,
                targetCoordinates,
            range,
            cancelToken,
            _pathfind.GetFlags(blackboard));

        if (AllowPartial && path.Result == PathResult.NoPath)
            path = await _pathfind.GetPartialPath(owner, xform.Coordinates, targetCoordinates, range, cancelToken, _pathfind.GetFlags(blackboard));

        if (path.Result == PathResult.PartialPath && path.Path.Count > 0)
        {
            var closest = path.Path[^1];
            return (true, new Dictionary<string, object>()
            {
                {NPCBlackboard.OwnerCoordinates, targetCoordinates},
                {PathfindKey, path},
                {PartialTargetKey, new EntityCoordinates(closest.GraphUid, closest.Box.Center)}
            });
        }

        if (path.Result != PathResult.Path)
        {
            return (false, null);
        }

        return (true, new Dictionary<string, object>()
        {
            {NPCBlackboard.OwnerCoordinates, targetCoordinates},
            {PathfindKey, path}
        });

    }

    // Given steering is complicated we'll hand it off to a dedicated system rather than this singleton operator.

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);

        // Need to remove the planning value for execution.
        blackboard.Remove<EntityCoordinates>(NPCBlackboard.OwnerCoordinates);
        var partial = blackboard.TryGetValue<EntityCoordinates>(PartialTargetKey, out var partialTarget, _entManager);
        var targetCoordinates = partial ? partialTarget : blackboard.GetValue<EntityCoordinates>(TargetKey);
        blackboard.Remove<EntityCoordinates>(PartialTargetKey);
        blackboard.Remove<TimeSpan>(PartialHoldKey);
        var uid = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (partial)
        {
            var settled = _entManager.TryGetComponent<NPCSteeringComponent>(uid, out var current) &&
                          current.Status == SteeringStatus.InRange &&
                          current.Coordinates.Equals(targetCoordinates);
            blackboard.SetValue(PartialHoldKey, settled ? TimeSpan.Zero : TimeSpan.MaxValue);
        }

        // Re-use the path we may have if applicable.
        var comp = _steering.Register(uid, targetCoordinates);
        comp.ArriveOnLineOfSight = StopOnLineOfSight;

        if (blackboard.TryGetValue<float>(RangeKey, out var range, _entManager))
        {
            comp.Range = range;
        }

        if (blackboard.TryGetValue<PathResultEvent>(PathfindKey, out var result, _entManager) &&
            result.Path.Count > 0 &&
            _entManager.EntityExists(targetCoordinates.EntityId) &&
            _entManager.EntityExists(result.Path[0].GraphUid))
        {
            var mapCoords = _transform.GetMapCoordinates(uid);
            _steering.PrunePath(uid, mapCoords, _transform.ToMapCoordinates(targetCoordinates).Position - mapCoords.Position, result.Path);

            if (result.Path.Count > 0 &&
                result.Path[0] is var next &&
                next.IsValid() &&
                _transform.GetWorldMatrix(next.GraphUid).TransformBox(next.Box.Enlarged(StalePathRange)).Contains(mapCoords.Position))
            {
                comp.CurrentPath = new Queue<PathPoly>(result.Path);
            }
        }
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.TryGetComponent<NPCSteeringComponent>(owner, out var steering))
            return HTNOperatorStatus.Failed;

        if (blackboard.TryGetValue<TimeSpan>(PartialHoldKey, out var holdUntil, _entManager))
        {
            switch (steering.Status)
            {
                case SteeringStatus.NoPath:
                    return HTNOperatorStatus.Failed;
                case SteeringStatus.Moving:
                    return HTNOperatorStatus.Continuing;
            }

            if (holdUntil == TimeSpan.MaxValue)
            {
                blackboard.SetValue(PartialHoldKey, _timing.CurTime + PartialHold);
                return HTNOperatorStatus.Continuing;
            }

            return _timing.CurTime < holdUntil ? HTNOperatorStatus.Continuing : HTNOperatorStatus.Finished;
        }

        // Just keep moving in the background and let the other tasks handle it.
        if (ShutdownState == HTNPlanState.PlanFinished && steering.Status == SteeringStatus.Moving)
        {
            return HTNOperatorStatus.Finished;
        }

        return steering.Status switch
        {
            SteeringStatus.InRange => HTNOperatorStatus.Finished,
            SteeringStatus.NoPath => HTNOperatorStatus.Failed,
            SteeringStatus.Moving => HTNOperatorStatus.Continuing,
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    public void ConditionalShutdown(NPCBlackboard blackboard)
    {
        // Cleanup the blackboard and remove steering.
        if (blackboard.TryGetValue<CancellationTokenSource>(MovementCancelToken, out var cancelToken, _entManager))
        {
            cancelToken.Cancel();
            blackboard.Remove<CancellationTokenSource>(MovementCancelToken);
        }

        // OwnerCoordinates is only used in planning so dump it.
        blackboard.Remove<PathResultEvent>(PathfindKey);
        blackboard.Remove<TimeSpan>(PartialHoldKey);

        if (RemoveKeyOnFinish)
        {
            blackboard.Remove<EntityCoordinates>(TargetKey);
        }

        _steering.Unregister(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner));
    }
}
