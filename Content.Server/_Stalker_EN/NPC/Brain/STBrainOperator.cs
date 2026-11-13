using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;

namespace Content.Server._Stalker_EN.NPC.Brain;

public sealed partial class STBrainOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        return (_entManager.HasComponent<STBrainComponent>(blackboard.GetValue<EntityUid>(NPCBlackboard.Owner)), null);
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        return HTNOperatorStatus.Continuing;
    }

    public override void TaskShutdown(NPCBlackboard blackboard, HTNOperatorStatus status)
    {
        base.TaskShutdown(blackboard, status);

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (_entManager.TryGetComponent<HTNComponent>(owner, out var htn) && !htn.Enabled)
            _entManager.System<STBrainSystem>().Disable(owner);
    }
}
