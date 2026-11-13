using Content.Server.NPC.HTN;
using Content.Shared.ActionBlocker;
using Content.Shared.Actions;
using Content.Shared.Actions.Components;
using Content.Shared.Examine;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Stalker.NPCs;

public sealed class NPCUseActionSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    private static readonly TimeSpan FizzleRetry = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan ReachRetry = TimeSpan.FromSeconds(0.25);

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NPCUseActionComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<NPCUseActionComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.ActionEnt = _actions.AddAction(ent, ent.Comp.ActionId);
    }

    public bool TryUseAction(Entity<NPCUseActionComponent?> user, EntityUid target)
    {
        if (!Resolve(user, ref user.Comp, false))
            return false;

        if (_actions.GetAction(user.Comp.ActionEnt) is not { } actionEntityWorldTarget)
            return false;

        if (!_actions.ValidAction(actionEntityWorldTarget))
            return false;

        _actions.SetEventTarget(actionEntityWorldTarget, target);

        // NPC is serverside, no prediction :(
        _actions.PerformAction(user.Owner, actionEntityWorldTarget, predicted: false);

        return true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<NPCUseActionComponent, HTNComponent, ActiveNPCComponent>();
        while (query.MoveNext(out var uid, out var comp, out var htn, out _))
        {
            if (comp.WindUpEnd != null)
            {
                if (now < comp.WindUpEnd)
                    continue;

                var used = comp.WindUpTarget is { } windUpTarget &&
                           htn.Blackboard.TryGetValue<EntityUid>(comp.TargetKey, out var current, EntityManager) &&
                           current == windUpTarget &&
                           CanCast(uid, windUpTarget, comp) &&
                           TryUseAction((uid, comp), windUpTarget);

                comp.WindUpEnd = null;
                comp.WindUpTarget = null;
                comp.NextUse = now + (used ? comp.Interval + _random.Next(comp.Jitter) : FizzleRetry);
                continue;
            }

            var attempt = new NPCUseActionAttemptEvent();
            RaiseLocalEvent(uid, ref attempt);
            if (attempt.Cancelled)
            {
                if (comp.NextUse < now + comp.Interval)
                    comp.NextUse = now + comp.Interval + _random.Next(comp.Jitter);

                continue;
            }

            if (now < comp.NextUse)
                continue;

            if (!htn.Blackboard.TryGetValue<EntityUid>(comp.TargetKey, out var target, EntityManager) ||
                !CanCast(uid, target, comp))
            {
                comp.NextUse = now + ReachRetry;
                continue;
            }

            _audio.PlayPvs(comp.WindUpSound, uid);
            comp.WindUpEnd = now + comp.WindUp;
            comp.WindUpTarget = target;
        }
    }

    public bool CanReach(EntityUid uid, EntityUid target)
    {
        return TryComp<NPCUseActionComponent>(uid, out var comp) &&
               comp.ActionEnt is { } action &&
               !HasComp<InstantActionComponent>(action) &&
               CanCast(uid, target, comp);
    }

    private bool CanCast(EntityUid uid, EntityUid target, NPCUseActionComponent comp)
    {
        return !TerminatingOrDeleted(target) &&
               _mobState.IsAlive(target) &&
               _actionBlocker.CanInteract(uid, null) &&
               _transform.InRange(uid, target, comp.MaxRange) &&
               (!comp.RequireLineOfSight || _examine.InRangeUnOccluded(uid, target, comp.MaxRange));
    }
}

[ByRefEvent]
public record struct NPCUseActionAttemptEvent(bool Cancelled = false);
