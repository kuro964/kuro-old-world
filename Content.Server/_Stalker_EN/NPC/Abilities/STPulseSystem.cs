using Content.Server._Stalker_EN.NPC.Awareness;
using Content.Server._Stalker_EN.NPC.Brain;
using Content.Server.NPC.HTN;
using Content.Server.Stunnable;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.Popups;
using Content.Shared.Throwing;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Abilities;

[RegisterComponent]
public sealed partial class STPulseComponent : Component
{
    [DataField]
    public float Range = 4f;

    [DataField]
    public TimeSpan Windup = TimeSpan.FromSeconds(0.5);

    [DataField]
    public TimeSpan Cooldown = TimeSpan.FromSeconds(12);

    [DataField]
    public TimeSpan Stun;

    [DataField]
    public TimeSpan Knockdown;

    [DataField]
    public TimeSpan Root;

    [DataField]
    public float Knockback;

    [DataField]
    public float KnockbackSpeed = 10f;

    [DataField]
    public DamageSpecifier? Damage;

    [DataField]
    public bool HoldStill = true;

    [DataField]
    public SoundSpecifier? Sound;

    [DataField]
    public LocId? Popup;

    [DataField]
    public string Doing = "winding up a blast";

    [ViewVariables]
    public TimeSpan NextThink;

    [ViewVariables]
    public TimeSpan Next;

    [ViewVariables]
    public TimeSpan? FireAt;
}

public sealed class STPulseSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly STAwarenessSystem _awareness = default!;
    [Dependency] private readonly StunSystem _stun = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly Content.Shared.Movement.Systems.MovementModStatusSystem _movementMod = default!;

    private static readonly Robust.Shared.Prototypes.EntProtoId RootEffect = "STRootStatusEffect";

    private static readonly TimeSpan ThinkInterval = TimeSpan.FromSeconds(0.25);
    private readonly HashSet<Entity<MobStateComponent>> _victims = new();

    public override void Update(float frameTime)
    {
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<STPulseComponent, STBrainComponent, STAwarenessComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var comp, out var brain, out var awareness, out var htn))
        {
            if (comp.FireAt is { } fireAt)
            {
                if (now < fireAt)
                    continue;

                comp.FireAt = null;
                if (htn.Enabled && _mobState.IsAlive(uid))
                    Fire(uid, comp);

                continue;
            }

            if (now < comp.NextThink)
                continue;

            comp.NextThink = now + ThinkInterval;
            if (now < comp.Next ||
                !htn.Enabled ||
                !HasComp<ActiveNPCComponent>(uid) ||
                brain.Behaviour != STBehaviour.Fight ||
                now < brain.BusyUntil ||
                awareness.Target is not { } target ||
                !_mobState.IsAlive(target) ||
                !_examine.InRangeUnOccluded(uid, target, comp.Range))
            {
                continue;
            }

            comp.FireAt = now + comp.Windup;
            comp.Next = comp.FireAt.Value + comp.Cooldown;
            if (comp.HoldStill && comp.Windup > TimeSpan.Zero)
            {
                brain.BusyUntil = comp.FireAt.Value;
                brain.Doing = comp.Doing;
            }

            if (comp.Sound != null)
                _audio.PlayPvs(comp.Sound, uid);

            if (comp.Popup is { } popup)
                _popup.PopupEntity(Loc.GetString(popup), uid, PopupType.LargeCaution);
        }
    }

    private void Fire(EntityUid uid, STPulseComponent comp)
    {
        var self = _transform.GetMapCoordinates(uid);
        _victims.Clear();
        _lookup.GetEntitiesInRange(self, comp.Range, _victims);
        foreach (var victim in _victims)
        {
            if (victim.Owner == uid ||
                !_mobState.IsAlive(victim.Owner, victim.Comp) ||
                !_awareness.IsHostile(uid, victim.Owner) ||
                !_examine.InRangeUnOccluded(uid, victim.Owner, comp.Range))
            {
                continue;
            }

            if (comp.Damage != null)
                _damageable.TryChangeDamage(victim.Owner, comp.Damage, origin: uid);

            if (comp.Stun > TimeSpan.Zero)
                _stun.TryUpdateStunDuration(victim.Owner, comp.Stun);

            if (comp.Knockdown > TimeSpan.Zero)
                _stun.TryKnockdown(victim.Owner, comp.Knockdown, false);

            if (comp.Root > TimeSpan.Zero)
                _movementMod.TryUpdateMovementSpeedModDuration(victim.Owner, RootEffect, comp.Root, 0f);

            if (comp.Knockback <= 0f)
                continue;

            var away = _transform.GetWorldPosition(victim.Owner) - self.Position;
            if (away.LengthSquared() < 0.01f)
                continue;

            _throwing.TryThrow(victim.Owner, System.Numerics.Vector2.Normalize(away) * comp.Knockback, comp.KnockbackSpeed, uid, recoil: false);
        }
    }
}
