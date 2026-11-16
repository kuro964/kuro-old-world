using System.Numerics;
using Content.Shared._RMC14.Weapons.Ranged.Prediction;
using Content.Shared.Administration.Logs;
using Content.Shared.Camera;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Database;
using Content.Shared.Destructible;
using Content.Shared.DoAfter;
using Content.Shared.Effects;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Components;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;
using Content.Shared.FixedPoint; // ST:OW

namespace Content.Shared.Projectiles;

public abstract partial class SharedProjectileSystem : EntitySystem
{
    public const string ProjectileFixture = "projectile";

    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedDestructibleSystem _destructible = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly SharedColorFlashEffectSystem _color = default!;
    [Dependency] private readonly DamageableSystem _damageableSystem = default!;
    [Dependency] private readonly SharedGunSystem _guns = default!;
    [Dependency] private readonly SharedCameraRecoilSystem _sharedCameraRecoil = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ProjectileComponent, PreventCollideEvent>(PreventCollision);
        SubscribeLocalEvent<EmbeddableProjectileComponent, ProjectileHitEvent>(OnEmbedProjectileHit);
        SubscribeLocalEvent<EmbeddableProjectileComponent, ThrowDoHitEvent>(OnEmbedThrowDoHit);
        SubscribeLocalEvent<EmbeddableProjectileComponent, ActivateInWorldEvent>(OnEmbedActivate);
        SubscribeLocalEvent<EmbeddableProjectileComponent, RemoveEmbeddedProjectileEvent>(OnEmbedRemove);
        SubscribeLocalEvent<EmbeddableProjectileComponent, ComponentShutdown>(OnEmbeddableCompShutdown);

        SubscribeLocalEvent<EmbeddedContainerComponent, EntityTerminatingEvent>(OnEmbeddableTermination);
    }

    protected void OnStartCollide(EntityUid uid, ProjectileComponent component, ref StartCollideEvent args)
    {
        // This is so entities that shouldn't get a collision are ignored.
        if (args.OurFixtureId != ProjectileFixture || !args.OtherFixture.Hard
            || component.ProjectileSpent || component is { Weapon: null, OnlyCollideWhenShot: true })
            return;

        ProjectileCollide((uid, component, args.OurBody), args.OtherEntity);
    }

    public void ProjectileCollide(Entity<ProjectileComponent, PhysicsComponent> projectile, EntityUid target, bool predicted = false)
    {
        var (uid, component, ourBody) = projectile;
        if (projectile.Comp1.ProjectileSpent)
        {
            if (_net.IsServer && component.DeleteOnCollide)
                Del(uid);

            return;
        }

        // it's here so this check is only done once before possible hit
        var attemptEv = new ProjectileReflectAttemptEvent(uid, component, false);
        RaiseLocalEvent(target, ref attemptEv);
        if (attemptEv.Cancelled)
        {
            SetShooter(uid, component, target);
            return;
        }

        var ev = new ProjectileHitEvent(component.Damage * _damageableSystem.UniversalProjectileDamageModifier, target, component.Shooter);
        RaiseLocalEvent(uid, ref ev);
        if (ev.Handled)
            return;

        var coordinates = Transform(projectile).Coordinates;
        var otherName = ToPrettyString(target);

        // ST:OW begin - Penetration compares the damage dealt against the health the target had before the hit
        var damageRequired = FixedPoint2.Zero;
        var canPenetrate = component.PenetrationThreshold != 0 && TryGetPenetrationHealth(target, out damageRequired);
        // ST:OW end

        var modifiedDamage = _net.IsServer
            ? _damageableSystem.ChangeDamage(target,
                ev.Damage,
                component.IgnoreResistances,
                origin: Exists(component.Shooter) ? component.Shooter : null,
                damageTier: projectile.Comp1.ProjectileClass) // stalker-en-changes : damageTier
            : new DamageSpecifier(ev.Damage);
        var deleted = Deleted(target);

        // RMC14 this is already done on the server in TryChangeDamage.
        if (_net.IsClient)
        {
            var modifyEvent = new DamageModifyEvent(ev.Damage, component.Shooter, projectile.Comp1.ProjectileClass);
            RaiseLocalEvent(target, modifyEvent);
            modifiedDamage = modifyEvent.Damage;
        }

        //

        var filter = Filter.Pvs(coordinates, entityMan: EntityManager);
        if (_guns.GunPrediction && predicted) // ST:OW
        {
            // TODO RMC14 clean this up once gun prediction is using new lag compensation
            if (TryComp(projectile, out PredictedProjectileServerComponent? serverProjectile) &&
                serverProjectile.Shooter is { } shooter)
            {
                filter = filter.RemovePlayer(shooter);
            }

        }

        // ST:OW begin - Single hit implementation for normal and predicted collisions
        // This is the logic that used to live in Content.Server ProjectileSystem.OnStartCollide
        // On the server, the projectile is only spent when the hit was applied and the shooter still exists, and penetration is decided here
        // A predicting client cannot know either, so its local projectile always ends at the first hit
        var hitApplied = _net.IsClient || (!ev.Damage.Empty && Exists(component.Shooter));
        if (hitApplied)
        {
            if (!deleted)
                _color.RaiseEffect(Color.Red, new List<EntityUid> { target }, filter);

            if (Exists(component.Shooter))
            {
                _adminLogger.Add(LogType.BulletHit,
                    LogImpact.Medium,
                    $"Projectile {ToPrettyString(uid):projectile} shot by {ToPrettyString(component.Shooter.Value):user} hit {otherName:target} and dealt {modifiedDamage.GetTotal():damage} damage");
            }
            
            if (canPenetrate)
            {
                if (component.PenetrationDamageTypeRequirement != null)
                {
                    foreach (var requiredDamageType in component.PenetrationDamageTypeRequirement)
                    {
                        if (modifiedDamage.DamageDict.ContainsKey(requiredDamageType))
                            continue;

                        component.ProjectileSpent = true;
                        break;
                    }
                }
                if (modifiedDamage.GetTotal() < damageRequired)
                    component.ProjectileSpent = true;

                if (!component.ProjectileSpent)
                {
                    component.PenetrationAmount += damageRequired;
                    if (component.PenetrationAmount >= component.PenetrationThreshold)
                        component.ProjectileSpent = true;
                }
            }
            else
            {
                component.ProjectileSpent = true;
            }
        }

        if (!deleted)
        {
            _guns.PlayImpactSound(target, modifiedDamage, component.SoundHit, component.ForceSound);

            if (_net.IsServer && !ourBody.LinearVelocity.IsLengthZero())
            {
                var direction = ourBody.LinearVelocity.Normalized();
                if (!float.IsNaN(direction.X))
                    _sharedCameraRecoil.KickCamera(target, direction);
            }
        }

        Dirty(uid, component);
        // ST:OW end

        if ((_net.IsServer || IsClientSide(uid)) && component.ImpactEffect != null)
        {
            var impactEffectEv = new ImpactEffectEvent(component.ImpactEffect, GetNetCoordinates(coordinates));
            if (_net.IsServer)
                RaiseNetworkEvent(impactEffectEv, filter);
            else
                RaiseLocalEvent(impactEffectEv);
        }

        // ST:OW begin - A projectile that is not spent keeps flying
        if (!component.DeleteOnCollide || !component.ProjectileSpent)
            return;

        if (predicted)
        {
            // Server-side hit reported by the shooter's client
            // Removal is deferred until the projectile has travelled to the target, so other clients don't see it vanish earlier than expected
            if (!_net.IsServer)
                return;

            var predictedComp = EnsureComp<PredictedProjectileHitComponent>(uid);
            predictedComp.Origin = _transform.GetMoverCoordinates(coordinates);

            var targetCoords = _transform.GetMoverCoordinates(target);
            if (predictedComp.Origin.TryDistance(EntityManager, _transform, targetCoords, out var distance))
                predictedComp.Distance = distance;

            Dirty(uid, predictedComp);
            return;
        }

        if (_net.IsServer)
            QueueDel(uid);
        else if (IsClientSide(uid))
            Del(uid);
    }
    
    protected virtual bool TryGetPenetrationHealth(EntityUid target, out FixedPoint2 damageRequired)
    {
        damageRequired = FixedPoint2.Zero;
        return false;
    }
    // ST:OW end

    private void OnEmbedActivate(Entity<EmbeddableProjectileComponent> embeddable, ref ActivateInWorldEvent args)
    {
        // Unremovable embeddables moment
        if (embeddable.Comp.RemovalTime == null)
            return;

        if (args.Handled || !args.Complex || !TryComp<PhysicsComponent>(embeddable, out var physics) ||
            physics.BodyType != BodyType.Static)
            return;

        args.Handled = true;

        _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager,
            args.User,
            embeddable.Comp.RemovalTime.Value,
            new RemoveEmbeddedProjectileEvent(),
            eventTarget: embeddable,
            target: embeddable));
    }

    private void OnEmbedRemove(Entity<EmbeddableProjectileComponent> embeddable, ref RemoveEmbeddedProjectileEvent args)
    {
        if (args.Cancelled)
            return;

        EmbedDetach(embeddable, embeddable.Comp, args.User);

        // try place it in the user's hand
        _hands.TryPickupAnyHand(args.User, embeddable);
    }

    private void OnEmbeddableCompShutdown(Entity<EmbeddableProjectileComponent> embeddable, ref ComponentShutdown arg)
    {
        EmbedDetach(embeddable, embeddable.Comp);
    }

    private void OnEmbedThrowDoHit(Entity<EmbeddableProjectileComponent> embeddable, ref ThrowDoHitEvent args)
    {
        if (!embeddable.Comp.EmbedOnThrow)
            return;

        EmbedAttach(embeddable, args.Target, null, embeddable.Comp);
    }

    private void OnEmbedProjectileHit(Entity<EmbeddableProjectileComponent> embeddable, ref ProjectileHitEvent args)
    {
        EmbedAttach(embeddable, args.Target, args.Shooter, embeddable.Comp);

        // Raise a specific event for projectiles.
        if (TryComp(embeddable, out ProjectileComponent? projectile) &&
            projectile.Shooter is { } shooter &&
            projectile.Weapon is { } weapon)
        {
            var ev = new ProjectileEmbedEvent(shooter, weapon, args.Target);
            RaiseLocalEvent(embeddable, ref ev);
        }
    }

    private void EmbedAttach(EntityUid uid, EntityUid target, EntityUid? user, EmbeddableProjectileComponent component)
    {
        TryComp<PhysicsComponent>(uid, out var physics);
        _physics.SetLinearVelocity(uid, Vector2.Zero, body: physics);
        _physics.SetBodyType(uid, BodyType.Static, body: physics);
        var xform = Transform(uid);
        _transform.SetParent(uid, xform, target);

        if (component.Offset != Vector2.Zero)
        {
            var rotation = xform.LocalRotation;
            if (TryComp<ThrowingAngleComponent>(uid, out var throwingAngleComp))
                rotation += throwingAngleComp.Angle;
            _transform.SetLocalPosition(uid, xform.LocalPosition + rotation.RotateVec(component.Offset), xform);
        }

        _audio.PlayPredicted(component.Sound, uid, null);
        component.EmbeddedIntoUid = target;
        var ev = new EmbedEvent(user, target);
        RaiseLocalEvent(uid, ref ev);
        Dirty(uid, component);

        EnsureComp<EmbeddedContainerComponent>(target, out var embeddedContainer);

        //Assert that this entity not embed
        DebugTools.AssertEqual(embeddedContainer.EmbeddedObjects.Contains(uid), false);

        embeddedContainer.EmbeddedObjects.Add(uid);
    }

    public void EmbedDetach(EntityUid uid, EmbeddableProjectileComponent? component, EntityUid? user = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (component.EmbeddedIntoUid is not null)
        {
            if (TryComp<EmbeddedContainerComponent>(component.EmbeddedIntoUid.Value, out var embeddedContainer))
            {
                embeddedContainer.EmbeddedObjects.Remove(uid);
                Dirty(component.EmbeddedIntoUid.Value, embeddedContainer);
                if (embeddedContainer.EmbeddedObjects.Count == 0)
                    RemCompDeferred<EmbeddedContainerComponent>(component.EmbeddedIntoUid.Value);
            }
        }

        if (component.DeleteOnRemove && _net.IsServer)
        {
            QueueDel(uid);
            return;
        }

        var xform = Transform(uid);
        if (TerminatingOrDeleted(xform.GridUid) && TerminatingOrDeleted(xform.MapUid))
            return;
        TryComp<PhysicsComponent>(uid, out var physics);
        _physics.SetBodyType(uid, BodyType.Dynamic, body: physics, xform: xform);
        _transform.AttachToGridOrMap(uid, xform);
        component.EmbeddedIntoUid = null;
        Dirty(uid, component);

        // Reset whether the projectile has damaged anything if it successfully was removed
        if (TryComp<ProjectileComponent>(uid, out var projectile))
        {
            projectile.Shooter = null;
            projectile.Weapon = null;
            projectile.ProjectileSpent = false;

            Dirty(uid, projectile);
        }

        if (user != null)
        {
            // Land it just coz uhhh yeah
            var landEv = new LandEvent(user, true);
            RaiseLocalEvent(uid, ref landEv);
        }

        _physics.WakeBody(uid, body: physics);
    }

    private void OnEmbeddableTermination(Entity<EmbeddedContainerComponent> container, ref EntityTerminatingEvent args)
    {
        DetachAllEmbedded(container);
    }

    public void DetachAllEmbedded(Entity<EmbeddedContainerComponent> container)
    {
        foreach (var embedded in container.Comp.EmbeddedObjects)
        {
            if (!TryComp<EmbeddableProjectileComponent>(embedded, out var embeddedComp))
                continue;

            EmbedDetach(embedded, embeddedComp);
        }
    }

    private void PreventCollision(EntityUid uid, ProjectileComponent component, ref PreventCollideEvent args)
    {
        if (component.IgnoreShooter && (args.OtherEntity == component.Shooter || args.OtherEntity == component.Weapon))
        {
            args.Cancelled = true;
        }
    }

    public void SetShooter(EntityUid id, ProjectileComponent component, EntityUid? shooterId = null)
    {
        if (component.Shooter == shooterId || shooterId == null)
            return;

        component.Shooter = shooterId;
        Dirty(id, component);
    }

    [Serializable, NetSerializable]
    private sealed partial class RemoveEmbeddedProjectileEvent : DoAfterEvent
    {
        public override DoAfterEvent Clone() => this;
    }

    #region Stalker-EN-Changes: Thrown knives fixes
    /// <summary>
    /// Imp port : Unembeds all child entities on a given entity.
    /// </summary>
    public void RemoveEmbeddedChildren(EntityUid uid)
    {
        var enumerator = Transform(uid).ChildEnumerator;

        while (enumerator.MoveNext(out var child))
        {
            if (TryComp<EmbeddableProjectileComponent>(child, out var embed))
                EmbedDetach(child, embed);
        }
    }

    /// <summary>
    /// Imp port : Similar to OnEmbedRemove but without an user doing the action
    /// </summary>
    public void EmbedDetach(EntityUid uid, EmbeddableProjectileComponent? component)
    {
        if (!Resolve(uid, ref component))
            return;

        if (component.EmbeddedIntoUid != null &&
            TryComp<EmbeddedContainerComponent>(component.EmbeddedIntoUid.Value, out var embeddedContainer))
        {
            embeddedContainer.EmbeddedObjects.Remove(uid);
            Dirty(component.EmbeddedIntoUid.Value, embeddedContainer);
            if (embeddedContainer.EmbeddedObjects.Count == 0)
                RemCompDeferred<EmbeddedContainerComponent>(component.EmbeddedIntoUid.Value);
        }

        if (component.DeleteOnRemove)
        {
            PredictedQueueDel(uid);
            return;
        }

        var xform = Transform(uid);
        if (TerminatingOrDeleted(xform.GridUid) && TerminatingOrDeleted(xform.MapUid))
            return;

        TryComp<PhysicsComponent>(uid, out var physics);
        if (physics != null)
            _physics.SetBodyType(uid, BodyType.Dynamic, body: physics, xform: xform);
        _transform.AttachToGridOrMap(uid, xform);
        component.EmbeddedIntoUid = null;
        Dirty(uid, component);

        // Land it just coz uhhh yeah
        var landEv = new LandEvent(null, true);
        RaiseLocalEvent(uid, ref landEv);
        if (physics != null)
            _physics.WakeBody(uid, body: physics);
    }
    #endregion
}

[Serializable, NetSerializable]
public sealed class ImpactEffectEvent : EntityEventArgs
{
    public string Prototype;
    public NetCoordinates Coordinates;

    public ImpactEffectEvent(string prototype, NetCoordinates coordinates)
    {
        Prototype = prototype;
        Coordinates = coordinates;
    }
}

/// <summary>
/// Raised when an entity is just about to be hit with a projectile but can reflect it
/// </summary>
[ByRefEvent]
public record struct ProjectileReflectAttemptEvent(EntityUid ProjUid, ProjectileComponent Component, bool Cancelled) : IInventoryRelayEvent
{
    SlotFlags IInventoryRelayEvent.TargetSlots => SlotFlags.WITHOUT_POCKET;
}

/// <summary>
/// Raised when a projectile hits an entity
/// </summary>
[ByRefEvent]
public record struct ProjectileHitEvent(DamageSpecifier Damage, EntityUid Target, EntityUid? Shooter = null, bool Handled = false);
