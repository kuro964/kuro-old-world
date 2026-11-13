using System.Linq;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Awareness;

public sealed class STAlertGroupSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly STAwarenessSystem _awareness = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NPCRetaliationSystem _retaliation = default!;

    private readonly HashSet<Entity<STAlertGroupComponent>> _members = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<STAlertGroupComponent, STTargetSpottedEvent>(OnSpotted);
        SubscribeLocalEvent<STAlertGroupComponent, DamageChangedEvent>(OnDamaged, before: new[] { typeof(STHearingSystem) });
    }

    private void OnSpotted(Entity<STAlertGroupComponent> ent, ref STTargetSpottedEvent args)
    {
        var now = _timing.CurTime;
        if (now < ent.Comp.NextAlert)
            return;

        var origin = _transform.GetMapCoordinates(ent);
        var where = args.Where;

        _members.Clear();
        _lookup.GetEntitiesInRange(origin, ent.Comp.Radius, _members);

        var nearest = _members
            .Where(m => m.Owner != ent.Owner && (m.Comp.Group == ent.Comp.Group || ent.Comp.AlsoAlerts.Contains(m.Comp.Group)) && _faction.IsEntityFriendly(ent.Owner, m.Owner))
            .OrderBy(m => (_transform.GetWorldPosition(m) - origin.Position).LengthSquared());

        var alerted = 0;
        foreach (var member in nearest)
        {
            if (!_awareness.TryAlert(member, args.Target, where))
                continue;

            member.Comp.NextAlert = now + member.Comp.ChainBlock;
            if (++alerted >= ent.Comp.MaxRecipients)
                break;
        }

        if (alerted == 0)
            return;

        ent.Comp.NextAlert = now + ent.Comp.Cooldown;
        _audio.PlayPvs(ent.Comp.Sound, ent);
    }

    private void OnDamaged(Entity<STAlertGroupComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased ||
            args.Origin is not { } attacker ||
            attacker == ent.Owner ||
            !HasComp<NPCRetaliationComponent>(ent) ||
            !_mobState.IsAlive(attacker) ||
            _retaliation.IsStray(ent, attacker) ||
            !Rallies(ent, attacker))
        {
            return;
        }

        _members.Clear();
        _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(ent), ent.Comp.Radius, _members);

        foreach (var member in _members)
        {
            if (member.Owner == ent.Owner ||
                member.Comp.Group != ent.Comp.Group ||
                !_faction.IsEntityFriendly(ent.Owner, member.Owner) ||
                !_faction.IsEntityFriendly(member.Owner, ent.Owner) ||
                !TryComp<NPCRetaliationComponent>(member, out var retaliation))
            {
                continue;
            }

            _retaliation.TryRetaliate((member, retaliation), attacker);
        }
    }

    private bool Rallies(EntityUid victim, EntityUid attacker)
    {
        if (!TryComp<NpcFactionMemberComponent>(victim, out var mine) ||
            _faction.IsEntityFriendly((victim, mine), attacker))
        {
            return false;
        }

        var ours = mine.HostileFactions;
        var theirs = CompOrNull<NpcFactionMemberComponent>(attacker);
        if (theirs != null && ours.Overlaps(theirs.Factions))
            return false;

        if (HasComp<ActorComponent>(attacker))
            return true;

        if (theirs == null)
            return false;

        var hostile = theirs.HostileFactions;
        return hostile.Overlaps(mine.Factions);
    }
}
