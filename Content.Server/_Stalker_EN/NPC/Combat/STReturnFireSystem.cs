using Content.Server._Stalker_EN.NPC.Awareness;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Combat;

public sealed class STReturnFireSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly STAwarenessSystem _awareness = default!;
    [Dependency] private readonly HTNSystem _htn = default!;

    private const string VisionKey = "VisionRadius";
    private const string AggroKey = "AggroVisionRadius";
    private static readonly TimeSpan KeepTime = TimeSpan.FromSeconds(2);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<STReturnFireComponent, DamageChangedEvent>(OnDamaged, after: new[] { typeof(NPCRetaliationSystem) });
    }

    private void OnDamaged(Entity<STReturnFireComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased ||
            args.Origin is not { } attacker ||
            attacker == ent.Owner ||
            !_mobState.IsAlive(ent) ||
            !_mobState.IsAlive(attacker) ||
            !_awareness.IsHostile(ent, attacker) ||
            !TryComp<HTNComponent>(ent, out var htn))
        {
            return;
        }

        var blackboard = htn.Blackboard;
        var vision = ent.Comp.BaseVision ?? blackboard.GetValueOrDefault<float>(VisionKey, EntityManager);
        var distance = (_transform.GetWorldPosition(attacker) - _transform.GetWorldPosition(ent)).Length();
        if (distance <= vision ||
            distance > ent.Comp.MaxRange ||
            _transform.GetMapId(attacker) != _transform.GetMapId(ent.Owner) ||
            !_examine.InRangeUnOccluded(ent.Owner, attacker, distance + 0.5f))
        {
            return;
        }

        ent.Comp.BaseVision ??= vision;
        ent.Comp.BaseAggro ??= blackboard.GetValueOrDefault<float>(AggroKey, EntityManager);
        var reach = distance + ent.Comp.Margin;
        blackboard.SetValue(VisionKey, MathF.Max(reach, ent.Comp.BaseVision.Value));
        blackboard.SetValue(AggroKey, MathF.Max(reach, ent.Comp.BaseAggro.Value));
        ent.Comp.Until = _timing.CurTime + ent.Comp.Duration;
        ent.Comp.Attacker = attacker;
        _awareness.ForceNotice(ent, attacker);
        _htn.Replan(htn);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<STReturnFireComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var comp, out var htn))
        {
            if (comp.BaseVision is not { } vision || now < comp.Until)
                continue;

            if (StillOnAttacker(uid, comp, htn))
            {
                comp.Until = now + KeepTime;
                continue;
            }

            htn.Blackboard.SetValue(VisionKey, vision);
            if (comp.BaseAggro is { } aggro)
                htn.Blackboard.SetValue(AggroKey, aggro);

            comp.BaseVision = null;
            comp.BaseAggro = null;
            comp.Attacker = null;
        }
    }

    private bool StillOnAttacker(EntityUid uid, STReturnFireComponent comp, HTNComponent htn)
    {
        if (comp.Attacker is not { } attacker ||
            TerminatingOrDeleted(attacker) ||
            !_mobState.IsAlive(uid) ||
            !_mobState.IsAlive(attacker) ||
            !htn.Blackboard.TryGetValue<EntityUid>("Target", out var target, EntityManager) ||
            target != attacker)
        {
            return false;
        }

        var reach = htn.Blackboard.GetValueOrDefault<float>(AggroKey, EntityManager);
        return _examine.InRangeUnOccluded(uid, attacker, reach);
    }
}
