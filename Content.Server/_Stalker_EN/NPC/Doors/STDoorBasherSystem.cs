using Content.Server._Stalker_EN.NPC.Awareness;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Shared.CombatMode;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Examine;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Doors;

public sealed class STDoorBasherSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private readonly SharedCombatModeSystem _combat = default!;
    [Dependency] private readonly SharedDoorSystem _door = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;

    private const string VisionRadiusKey = "VisionRadius";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<STDoorBasherComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<STDoorBasherComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.Active = TryComp<HTNComponent>(ent, out var htn) &&
                          !(htn.Blackboard.TryGetValue<bool>(NPCBlackboard.NavInteract, out var opens, EntityManager) && opens);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<STDoorBasherComponent, STAwarenessComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var comp, out var awareness, out var htn))
        {
            if (!comp.Active)
                continue;

            var hunting = awareness.State != STAwarenessState.Unaware;
            if (!hunting)
            {
                if (now < comp.NextSightCheck)
                    continue;

                comp.NextSightCheck = now + comp.SightCheckInterval;
                hunting = SeesHostile(uid, htn.Blackboard);
            }

            if (hunting == comp.Hunting)
                continue;

            comp.Hunting = hunting;

            if (hunting)
                htn.Blackboard.SetValue(NPCBlackboard.NavInteract, true);
            else
                htn.Blackboard.Remove<bool>(NPCBlackboard.NavInteract);
        }
    }

    private bool SeesHostile(EntityUid uid, NPCBlackboard blackboard)
    {
        var vision = blackboard.GetValueOrDefault<float>(VisionRadiusKey, EntityManager);

        foreach (var hostile in _faction.GetNearbyHostiles(uid, vision))
        {
            if (_mobState.IsAlive(hostile) && _examine.InRangeUnOccluded(uid, hostile, vision))
                return true;
        }

        return false;
    }

    public bool TryBash(Entity<STDoorBasherComponent> ent, Entity<DoorComponent> door)
    {
        var now = _timing.CurTime;

        if (ent.Comp.Door != door.Owner || now - ent.Comp.LastBash > ent.Comp.GiveUpTime)
        {
            ent.Comp.Door = door;
            ent.Comp.BashStart = now;
        }

        ent.Comp.LastBash = now;

        if (now - ent.Comp.BashStart > ent.Comp.GiveUpTime ||
            !TryComp<STBashableDoorComponent>(door, out var bashable) ||
            !_melee.TryGetWeapon(ent, out var weaponUid, out var weapon))
            return false;

        if (TryComp<STAwarenessComponent>(ent, out var awareness))
            awareness.HoldUntil = now + ent.Comp.HoldTime;

        if (door.Comp.State != DoorState.Closed || weapon.NextAttack > now)
            return true;

        _combat.SetInCombatMode(ent, true);
        var hit = _melee.AttemptLightAttack(ent, weaponUid, weapon, door);
        _combat.SetInCombatMode(ent, false);

        if (hit)
            bashable.Damage += _melee.GetDamage(weaponUid, ent, weapon).GetTotal();

        if (bashable.Damage < bashable.Strength || !_door.TryOpen(door, door.Comp, ent))
            return true;

        bashable.Damage = 0;
        ent.Comp.Door = null;
        return true;
    }
}
