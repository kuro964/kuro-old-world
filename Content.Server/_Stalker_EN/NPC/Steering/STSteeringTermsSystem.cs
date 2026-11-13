using Content.Server._Stalker_EN.NPC.Combat;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Shared.NPC;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Steering;

public sealed class STSteeringTermsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(NPCSystem));
        UpdatesAfter.Add(typeof(NPCCombatSystem));
        UpdatesBefore.Add(typeof(NPCSteeringSystem));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<ActiveNPCComponent, NPCSteeringComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var steering, out var xform))
        {
            steering.ExtraInterest.Clear();
            steering.ExtraDanger.Clear();

            var ev = new STSteeringTermsEvent(steering, xform, now);
            RaiseLocalEvent(uid, ref ev);
        }
    }
}
