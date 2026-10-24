using Content.Server.NPC;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Server.NPC.HTN.PrimitiveTasks.Operators;
using Content.Shared.Examine;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Awareness;

public sealed class STAwarenessSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly HTNSystem _htn = default!;

    public const string TargetKey = "Target";
    public const string LastKnownKey = "STLastKnownCoordinates";
    public const string ArrivedKey = "STReachedLastKnown";
    private const string SightRangeKey = "AggroVisionRadius";

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<STAwarenessComponent, HTNComponent, ActiveNPCComponent>();

        while (query.MoveNext(out var uid, out var comp, out var htn, out _))
        {
            if (now < comp.NextUpdate)
                continue;

            comp.NextUpdate = now + comp.UpdateInterval;

            if (!comp.ManagedRoots.Contains(htn.RootTask.Task))
                continue;

            UpdateAwareness(uid, comp, htn, now);
        }
    }

    private void UpdateAwareness(EntityUid uid, STAwarenessComponent comp, HTNComponent htn, TimeSpan now)
    {
        var blackboard = htn.Blackboard;

        if (blackboard.TryGetValue<EntityUid>(TargetKey, out var target, EntityManager) &&
            target.IsValid() &&
            !TerminatingOrDeleted(target))
        {
            if (!_mobState.IsAlive(target))
            {
                comp.Target = null;
                SetState(comp, STAwarenessState.Unaware, now);
                return;
            }

            comp.TargetMissingSince = null;

            if (comp.Target != target)
            {
                comp.Target = target;
                comp.LastSeen = now;
                blackboard.SetValue(LastKnownKey, Transform(target).Coordinates);
            }

            SetState(comp, STAwarenessState.Engaged, now);

            if (CanSee(uid, target, blackboard))
            {
                comp.LastSeen = now;
                blackboard.SetValue(LastKnownKey, Transform(target).Coordinates);
                return;
            }

            if (now - comp.LastSeen > comp.LoseSightGrace)
            {
                blackboard.Remove<EntityUid>(TargetKey);
                EnterLost(comp, blackboard, now);
                Interrupt(htn);
            }

            return;
        }

        if ((comp.State is STAwarenessState.Lost or STAwarenessState.Searching) && !RememberedAlive(comp))
        {
            GiveUp(comp, htn, now);
            return;
        }

        switch (comp.State)
        {
            case STAwarenessState.Engaged:
                if (RememberedAlive(comp) && CanSee(uid, comp.Target!.Value, blackboard))
                {
                    comp.LastSeen = now;
                    blackboard.SetValue(LastKnownKey, Transform(comp.Target.Value).Coordinates);
                    comp.TargetMissingSince ??= now;

                    if (now - comp.TargetMissingSince > comp.ReacquireTime)
                    {
                        comp.TargetMissingSince = null;
                        EnterLost(comp, blackboard, now);
                    }

                    break;
                }

                comp.TargetMissingSince = null;

                if (RememberedAlive(comp) &&
                    now - comp.LastSeen <= comp.MemoryTime &&
                    blackboard.ContainsKey(LastKnownKey))
                {
                    EnterLost(comp, blackboard, now);
                }
                else
                {
                    comp.Target = null;
                    SetState(comp, STAwarenessState.Unaware, now);
                }

                break;
            case STAwarenessState.Lost:
                if (htn.Plan?.CurrentOperator is MoveToOperator { TargetKey: LastKnownKey })
                    comp.LastPursuing = now;

                if (now < comp.HoldUntil || (TryComp<NPCSteeringComponent>(uid, out var steering) && steering.DoAfterId != null))
                    comp.StateStart += comp.UpdateInterval;

                var arrived = blackboard.TryGetValue<bool>(ArrivedKey, out var reached, EntityManager) && reached;
                var stalled = now - comp.LastPursuing > comp.PursueStallTime;

                if (arrived || stalled || now - comp.StateStart > comp.PursueTime)
                {
                    blackboard.SetValue(ArrivedKey, false);
                    SetState(comp, STAwarenessState.Searching, now);
                    Interrupt(htn);
                }

                break;
            case STAwarenessState.Searching:
                if (now - comp.StateStart > comp.SearchTime)
                    GiveUp(comp, htn, now);
                else if (htn.Plan == null)
                    _htn.Replan(htn);

                break;
        }
    }

    private bool CanSee(EntityUid uid, EntityUid target, NPCBlackboard blackboard)
    {
        var range = blackboard.GetValueOrDefault<float>(SightRangeKey, EntityManager) + 0.5f;
        return _examine.InRangeUnOccluded(uid, target, range);
    }

    private bool RememberedAlive(STAwarenessComponent comp)
    {
        return comp.Target is { } remembered && !TerminatingOrDeleted(remembered) && _mobState.IsAlive(remembered);
    }

    private void GiveUp(STAwarenessComponent comp, HTNComponent htn, TimeSpan now)
    {
        comp.Target = null;
        SetState(comp, STAwarenessState.Unaware, now);
        Interrupt(htn);
    }

    private void EnterLost(STAwarenessComponent comp, NPCBlackboard blackboard, TimeSpan now)
    {
        if (comp.Target is { } target && now - comp.LastSeen <= comp.HunchTime)
            blackboard.SetValue(LastKnownKey, Transform(target).Coordinates);

        blackboard.SetValue(ArrivedKey, false);
        comp.LastPursuing = now;
        SetState(comp, STAwarenessState.Lost, now);
    }

    private static void SetState(STAwarenessComponent comp, STAwarenessState state, TimeSpan now)
    {
        if (comp.State == state)
            return;

        comp.State = state;
        comp.StateStart = now;
    }

    private void Interrupt(HTNComponent htn)
    {
        htn.PlanningToken?.Cancel();
        htn.PlanningToken = null;
        htn.PlanningJob = null;

        if (htn.Plan != null)
        {
            _htn.ShutdownTask(htn.Plan.CurrentOperator, htn.Blackboard, HTNOperatorStatus.Failed);
            _htn.ShutdownPlan(htn);
        }

        _htn.Replan(htn);
    }
}
