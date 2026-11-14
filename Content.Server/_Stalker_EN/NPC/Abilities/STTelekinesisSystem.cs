using System.Numerics;
using Content.Server._Stalker_EN.NPC.Awareness;
using Content.Server._Stalker_EN.NPC.Brain;
using Content.Server.NPC.HTN;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Item;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_EN.NPC.Abilities;

public sealed class STTelekinesisSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly Robust.Shared.Random.IRobustRandom _random = default!;
    [Dependency] private readonly Content.Shared.Hands.EntitySystems.SharedHandsSystem _hands = default!;

    private const string TargetKey = STAwarenessSystem.TargetKey;
    private const float OrbitSpin = 2.5f;
    private static readonly TimeSpan ThinkInterval = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan SeenSlack = TimeSpan.FromSeconds(1.5);
    private static readonly ProtoId<DamageTypePrototype> Blunt = "Blunt";

    private readonly HashSet<EntityUid> _nearby = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<STTelekinesisComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<STTelekinesisComponent, MobStateChangedEvent>(OnMobState);
        SubscribeLocalEvent<STTelekinesisComponent, DamageChangedEvent>(OnDamaged);
        SubscribeLocalEvent<STHurledComponent, ThrowDoHitEvent>(OnHurledHit);
        SubscribeLocalEvent<STHurledComponent, StopThrowEvent>(OnHurledStop);
    }

    private void OnShutdown(Entity<STTelekinesisComponent> ent, ref ComponentShutdown args)
    {
        DropGuns(ent.Comp);
    }

    private void OnMobState(Entity<STTelekinesisComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Alive)
            return;

        DropGuns(ent.Comp);
        ent.Comp.Held = null;
    }

    private void OnDamaged(Entity<STTelekinesisComponent> ent, ref DamageChangedEvent args)
    {
        if (ent.Comp.Guns.Count == 0 || !args.DamageIncreased || args.DamageDelta == null)
            return;

        ent.Comp.OrbitDamage += args.DamageDelta.GetTotal().Float();
        while (ent.Comp.OrbitDamage >= ent.Comp.DamagePerGun && ent.Comp.Guns.Count > 0)
        {
            ent.Comp.OrbitDamage -= ent.Comp.DamagePerGun;
            var gun = ent.Comp.Guns[^1];
            ent.Comp.Guns.RemoveAt(ent.Comp.Guns.Count - 1);
            if (!TerminatingOrDeleted(gun))
                QueueDel(gun);
        }
    }

    private void OnHurledHit(Entity<STHurledComponent> ent, ref ThrowDoHitEvent args)
    {
        if (args.Target == ent.Comp.Thrower || !HasComp<MobStateComponent>(args.Target))
            return;

        _damageable.TryChangeDamage(args.Target, new DamageSpecifier(_proto.Index(Blunt), FixedPoint2.New(ent.Comp.Damage)), origin: ent.Comp.Thrower);
    }

    private void OnHurledStop(Entity<STHurledComponent> ent, ref StopThrowEvent args)
    {
        RemCompDeferred<STHurledComponent>(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<STTelekinesisComponent, STBrainComponent, STAwarenessComponent, HTNComponent>();
        while (query.MoveNext(out var uid, out var comp, out var brain, out var awareness, out var htn))
        {
            if (!HasComp<ActiveNPCComponent>(uid))
                continue;

            var hasTarget = htn.Blackboard.TryGetValue<EntityUid>(TargetKey, out var target, EntityManager) &&
                            !TerminatingOrDeleted(target) &&
                            _mobState.IsAlive(target) &&
                            awareness.Target == target &&
                            now - awareness.LastSeen <= SeenSlack;

            if (comp.Guns.Count > 0)
                Orbit(uid, comp, hasTarget ? target : null, now);

            if (comp.Held is { } held)
            {
                if (TerminatingOrDeleted(held) || !hasTarget)
                {
                    comp.Held = null;
                }
                else if (now >= comp.ThrowAt)
                {
                    comp.Held = null;
                    Hurl(uid, comp, held, target);
                }

                continue;
            }

            if (comp.DisarmAt is { } disarmAt)
            {
                if (now < disarmAt)
                    continue;

                comp.DisarmAt = null;
                if (hasTarget)
                {
                    var drop = new Content.Shared.Standing.DropHandItemsEvent();
                    RaiseLocalEvent(target, ref drop);
                }

                continue;
            }

            if (now < comp.NextThink || !hasTarget || comp.Guns.Count > 0)
                continue;

            if (comp.ShieldStance && TryComp<Content.Shared._Stalker.Psyonics.Actions.Shield.PsyonicsActionShieldComponent>(uid, out var shield) && shield.IsActive)
            {
                comp.NextThink = now + ThinkInterval;
                continue;
            }

            comp.NextThink = now + ThinkInterval;
            var self = _transform.GetMapCoordinates(uid);
            var distance = (_transform.GetWorldPosition(target) - self.Position).Length();

            if (comp.Orbit && now >= comp.NextOrbit && distance <= comp.OrbitRange)
            {
                StartOrbit(uid, comp, brain, now);
                continue;
            }

            if (comp.Disarm && now >= comp.NextDisarm && distance <= comp.DisarmRange && _hands.TryGetActiveItem(target, out _))
            {
                comp.DisarmAt = now + comp.DisarmWindup;
                comp.NextDisarm = comp.DisarmAt.Value + comp.DisarmCooldown;
                brain.BusyUntil = comp.DisarmAt.Value;
                brain.Doing = "wrenching the weapon away";
                _popup.PopupEntity(Loc.GetString("st-burer-disarm"), target, PopupType.LargeCaution);
                continue;
            }

            if (now >= comp.NextThrow && distance <= comp.ThrowRange && (PickObject(uid, comp, self) ?? PickObject(uid, comp, _transform.GetMapCoordinates(target))) is { } pick)
            {
                comp.Held = pick;
                comp.ThrowAt = now + comp.ThrowWindup;
                comp.NextThrow = comp.ThrowAt + comp.ThrowCooldown;
                brain.BusyUntil = comp.ThrowAt;
                brain.Doing = "lifting something to throw";
                _popup.PopupEntity(Loc.GetString("st-burer-lifts", ("item", pick)), pick, PopupType.MediumCaution);
                if (comp.LiftSound != null)
                    _audio.PlayPvs(comp.LiftSound, pick);
            }
        }
    }

    private readonly List<EntityUid> _choices = new();
    private readonly List<EntityUid> _stale = new();

    private EntityUid? PickObject(EntityUid uid, STTelekinesisComponent comp, MapCoordinates self)
    {
        _nearby.Clear();
        _lookup.GetEntitiesInRange(self.MapId, self.Position, comp.PickRange, _nearby, LookupFlags.Dynamic | LookupFlags.Sundries);

        _choices.Clear();
        _stale.Clear();
        foreach (var ent in _nearby)
        {
            if (ent == uid ||
                HasComp<MobStateComponent>(ent) ||
                HasComp<ProjectileComponent>(ent) ||
                HasComp<STHurledComponent>(ent) ||
                !TryComp<PhysicsComponent>(ent, out var body) ||
                body.BodyType == BodyType.Static ||
                HasComp<STOrbitGunComponent>(ent) ||
                Transform(ent).Anchored ||
                _container.IsEntityInContainer(ent))
            {
                continue;
            }

            if (comp.Recent.Contains(ent))
                _stale.Add(ent);
            else
                _choices.Add(ent);
        }

        var pool = _choices.Count > 0 ? _choices : _stale;
        if (pool.Count == 0)
            return null;

        var pick = pool[_random.Next(pool.Count)];
        comp.Recent.Add(pick);
        if (comp.Recent.Count > comp.RecentKept)
            comp.Recent.RemoveAt(0);

        return pick;
    }

    private void Hurl(EntityUid uid, STTelekinesisComponent comp, EntityUid held, EntityUid target)
    {
        if (Transform(held).Anchored || _container.IsEntityInContainer(held))
            return;

        var from = _transform.GetWorldPosition(held);
        var offset = _transform.GetWorldPosition(target) - from;
        var length = offset.Length();
        if (length < 0.1f)
            return;

        var mass = TryComp<PhysicsComponent>(held, out var body) ? body.FixturesMass : 1f;
        if (body != null && !body.CanCollide)
            _physics.SetCanCollide(held, true, body: body);

        var hurled = EnsureComp<STHurledComponent>(held);
        hurled.Thrower = uid;
        hurled.Damage = Math.Clamp(mass * comp.DamagePerMass, comp.MinDamage, comp.MaxDamage);
        _throwing.TryThrow(held, offset / length * (length + 2f), comp.ThrowSpeed, uid, recoil: false);
    }

    private void StartOrbit(EntityUid uid, STTelekinesisComponent comp, STBrainComponent brain, TimeSpan now)
    {
        comp.OrbitStart = now;
        comp.OrbitEnd = now + comp.OrbitWindup + comp.OrbitTime;
        comp.NextOrbitShot = now + comp.OrbitWindup;
        comp.NextOrbit = comp.OrbitEnd + comp.OrbitCooldown;
        comp.OrbitTurn = 0;
        comp.OrbitDamage = 0f;
        brain.BusyUntil = now + comp.OrbitWindup;
        brain.Doing = "raising guns into orbit";

        for (var i = 0; i < comp.OrbitGuns; i++)
        {
            var gun = Spawn(comp.OrbitGunPool[_random.Next(comp.OrbitGunPool.Count)], Transform(uid).Coordinates);
            RemComp<ItemComponent>(gun);
            if (TryComp<PhysicsComponent>(gun, out var body))
                _physics.SetCanCollide(gun, false, body: body);

            AddComp<STOrbitGunComponent>(gun);

            comp.Guns.Add(gun);
        }

        _popup.PopupEntity(Loc.GetString("st-burer-orbit"), uid, PopupType.LargeCaution);
    }

    private void Orbit(EntityUid uid, STTelekinesisComponent comp, EntityUid? target, TimeSpan now)
    {
        if (now >= comp.OrbitEnd)
        {
            DropGuns(comp);
            return;
        }

        var spin = (float) (now - comp.OrbitStart).TotalSeconds * OrbitSpin;
        for (var i = 0; i < comp.Guns.Count; i++)
        {
            var gun = comp.Guns[i];
            if (TerminatingOrDeleted(gun))
                continue;

            var angle = new Angle(spin + i * MathF.Tau / comp.Guns.Count);
            _transform.SetCoordinates(gun, Transform(uid).Coordinates.Offset(angle.ToVec() * comp.OrbitRadius));
            var aim = target is { } aimed ? _transform.GetWorldPosition(aimed) - _transform.GetWorldPosition(gun) : angle.ToVec();
            if (aim.LengthSquared() > 0.01f)
                _transform.SetWorldRotation(gun, aim.ToAngle() - Angle.FromDegrees(comp.GunSpriteAngle));
        }

        if (target is not { } victim || now < comp.NextOrbitShot)
            return;

        comp.NextOrbitShot = now + comp.OrbitShotInterval;
        var shooter = comp.Guns[comp.OrbitTurn++ % comp.Guns.Count];
        if (TerminatingOrDeleted(shooter))
            return;

        var from = _transform.GetMapCoordinates(shooter);
        var direction = _transform.GetWorldPosition(victim) - from.Position;
        if (direction.LengthSquared() < 0.01f)
            return;

        var bullet = Spawn(comp.OrbitBullet, from);
        _gun.ShootProjectile(bullet, direction, Vector2.Zero, shooter, uid, comp.OrbitBulletSpeed);
        if (TryComp<GunComponent>(shooter, out var gunComp))
            _audio.PlayPvs(gunComp.SoundGunshotModified, shooter);
    }

    private void DropGuns(STTelekinesisComponent comp)
    {
        foreach (var gun in comp.Guns)
        {
            if (!TerminatingOrDeleted(gun))
                QueueDel(gun);
        }

        comp.Guns.Clear();
    }
}
