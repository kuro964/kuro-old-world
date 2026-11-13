using Content.Server._Stalker_EN.NPC.Awareness;
using Content.Server.NPC.HTN;
using Content.Shared.Implants.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Trigger;

namespace Content.Server._Stalker_EN.NPC.Combat;

public sealed class STNpcBombConditionSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly STAwarenessSystem _awareness = default!;

    private readonly HashSet<Entity<NpcFactionMemberComponent>> _nearby = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<STNpcBombConditionComponent, AttemptTriggerEvent>(OnAttemptTrigger);
    }

    private void OnAttemptTrigger(Entity<STNpcBombConditionComponent> ent, ref AttemptTriggerEvent args)
    {
        if (args.Key == null ||
            !ent.Comp.Keys.Contains(args.Key) ||
            !TryComp<SubdermalImplantComponent>(ent, out var implant) ||
            implant.ImplantedEntity is not { } owner ||
            !HasComp<HTNComponent>(owner))
        {
            return;
        }

        args.Cancelled |= !WorthDetonating(owner, ent.Comp.Radius);
    }

    private bool WorthDetonating(EntityUid owner, float radius)
    {
        var enemy = false;

        _nearby.Clear();
        _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(owner), radius, _nearby);

        foreach (var other in _nearby)
        {
            if (other.Owner == owner || _mobState.IsDead(other))
                continue;

            if (_faction.IsEntityFriendly(owner, other.Owner))
                return false;

            enemy |= _awareness.IsHostile(owner, other.Owner);
        }

        return enemy;
    }
}
