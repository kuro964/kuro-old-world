using Content.Server._Stalker_EN.NPC.Awareness;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Shared.CombatMode;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Robust.Shared.Collections;
using Robust.Shared.Timing;

namespace Content.Server.NPC.Systems;

/// <summary>
///     Handles NPC which become aggressive after being attacked.
/// </summary>
public sealed class NPCRetaliationSystem : EntitySystem
{
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly STAwarenessSystem _awareness = default!;

    /// <inheritdoc />
    public override void Initialize()
    {
        SubscribeLocalEvent<NPCRetaliationComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<NPCRetaliationComponent, DisarmedEvent>(OnDisarmed);
    }

    private void OnDamageChanged(Entity<NPCRetaliationComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased)
            return;

        if (args.Origin is not {} origin || IsStray(ent, origin))
            return;

        TryRetaliate(ent, origin);
    }

    public bool IsStray(EntityUid victim, EntityUid attacker)
    {
        if (!HasComp<ActiveNPCComponent>(attacker) || !TryComp<HTNComponent>(attacker, out var htn) || _awareness.IsHostile(attacker, victim))
            return false;

        var aims = 0;
        if (htn.Blackboard.TryGetValue<EntityUid>("Target", out var target, EntityManager) && Aimed(target, victim, ref aims) ||
            TryComp<NPCRangedCombatComponent>(attacker, out var ranged) && Aimed(ranged.Target, victim, ref aims) ||
            TryComp<NPCMeleeCombatComponent>(attacker, out var melee) && Aimed(melee.Target, victim, ref aims) ||
            TryComp<STAwarenessComponent>(attacker, out var awareness) && awareness.Target is { } remembered && Aimed(remembered, victim, ref aims))
        {
            return false;
        }

        return aims > 0;
    }

    private static bool Aimed(EntityUid aimed, EntityUid victim, ref int aims)
    {
        if (!aimed.IsValid())
            return false;

        aims++;
        return aimed == victim;
    }

    private void OnDisarmed(Entity<NPCRetaliationComponent> ent, ref DisarmedEvent args)
    {
        TryRetaliate(ent, args.Source);
    }

    public bool TryRetaliate(Entity<NPCRetaliationComponent> ent, EntityUid target)
    {
        // don't retaliate against inanimate objects.
        if (!HasComp<MobStateComponent>(target))
            return false;

        // don't retaliate against the same faction
        if (_npcFaction.IsEntityFriendly(ent.Owner, target))
            return false;

        _npcFaction.AggroEntity(ent.Owner, target);
        if (ent.Comp.AttackMemoryLength is {} memoryLength)
            ent.Comp.AttackMemories[target] = _timing.CurTime + memoryLength;

        return true;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<NPCRetaliationComponent, FactionExceptionComponent>();
        while (query.MoveNext(out var uid, out var retaliationComponent, out var factionException))
        {
            // TODO: can probably reuse this allocation and clear it
            foreach (var entity in new ValueList<EntityUid>(retaliationComponent.AttackMemories.Keys))
            {
                if (!TerminatingOrDeleted(entity) && _timing.CurTime < retaliationComponent.AttackMemories[entity])
                    continue;

                _npcFaction.DeAggroEntity((uid, factionException), entity);
                // TODO: should probably remove the AttackMemory, thats the whole point of the ValueList right??
            }
        }
    }
}
