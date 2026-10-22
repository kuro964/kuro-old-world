using Content.Server.NPC;
using Content.Server.NPC.HTN.Preconditions;

namespace Content.Server._Stalker_EN.NPC.Awareness;

public sealed partial class STAwarenessStatePrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField(required: true)]
    public STAwarenessState State;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        return _entManager.TryGetComponent<STAwarenessComponent>(owner, out var comp) && comp.State == State;
    }
}
