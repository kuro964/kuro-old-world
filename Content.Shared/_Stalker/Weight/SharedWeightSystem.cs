using Content.Shared.Movement.Systems;
using Content.Shared.Chemistry.Components.SolutionManager; // ST:OW
using Content.Shared.Chemistry.EntitySystems; // ST:OW
using Content.Shared.FixedPoint; // ST:OW
using Content.Shared.Stacks; // ST:OW
using Content.Shared.Weapons.Ranged.Components; // ST:OW
using Content.Shared.Weapons.Ranged.Systems; // ST:OW
using Robust.Shared.Prototypes; // ST:OW

namespace Content.Shared._Stalker.Weight;

public sealed partial class STWeightSystem : EntitySystem
{
    [Dependency] private readonly MovementSpeedModifierSystem _movementSpeedModifier = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!; // ST:OW
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!; // ST:OW

    // ST:OW begin
    private EntityQuery<STWeightComponent> _weightQuery;
    private EntityQuery<StackComponent> _stackQuery;
    private EntityQuery<BallisticAmmoProviderComponent> _ballisticQuery;
    private EntityQuery<RevolverAmmoProviderComponent> _revolverQuery;
    private EntityQuery<SolutionContainerManagerComponent> _solutionManagerQuery;
    // ST:OW end

    public override void Initialize()
    {
        base.Initialize();

        // ST:OW begin
        _weightQuery = GetEntityQuery<STWeightComponent>();
        _stackQuery = GetEntityQuery<StackComponent>();
        _ballisticQuery = GetEntityQuery<BallisticAmmoProviderComponent>();
        _revolverQuery = GetEntityQuery<RevolverAmmoProviderComponent>();
        _solutionManagerQuery = GetEntityQuery<SolutionContainerManagerComponent>();
        
        SubscribeLocalEvent<STWeightComponent, EntParentChangedMessage>(OnParentChanged);
        SubscribeLocalEvent<STWeightComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshMovementSpeedModifiers);
        SubscribeLocalEvent<STWeightComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<STWeightComponent, StackCountChangedEvent>(OnStackCountChanged);
        SubscribeLocalEvent<STWeightComponent, SolutionContainerChangedEvent>(OnSolutionChanged);
        SubscribeLocalEvent<STWeightComponent, AmmoProviderChangedEvent>(OnAmmoProviderChanged);
        // ST:OW end
    }

    private void OnParentChanged(Entity<STWeightComponent> weight, ref EntParentChangedMessage args)
    {
        UpdateWeight(weight);
        TryUpdateWeight(args.OldParent);
    }

    // ST:OW begin
    private void OnMapInit(Entity<STWeightComponent> weight, ref MapInitEvent args)
    {
        UpdateWeight(weight);
    }

    private void OnStackCountChanged(Entity<STWeightComponent> weight, ref StackCountChangedEvent args)
    {
        UpdateWeightIfRunning(weight);
    }

    private void OnSolutionChanged(Entity<STWeightComponent> weight, ref SolutionContainerChangedEvent args)
    {
        if (weight.Comp.SolutionWeightPerUnit == 0f)
            return;

        UpdateWeightIfRunning(weight);
    }

    private void OnAmmoProviderChanged(Entity<STWeightComponent> weight, ref AmmoProviderChangedEvent args)
    {
        UpdateWeightIfRunning(weight);
    }
    
    private void UpdateWeightIfRunning(Entity<STWeightComponent> weight)
    {
        if (weight.Comp.LifeStage != ComponentLifeStage.Running)
            return;

        UpdateWeight(weight);
    }
    // ST:OW end

    private void OnRefreshMovementSpeedModifiers(Entity<STWeightComponent> weight, ref RefreshMovementSpeedModifiersEvent args)
    {
        args.ModifySpeed(weight.Comp.MovementSpeedModifier, weight.Comp.MovementSpeedModifier);
    }

    private void SetMovementSpeedModifiers(Entity<STWeightComponent> weight, float modifier)
    {
        // ST:OW begin
        if (MathHelper.CloseTo(weight.Comp.MovementSpeedModifier, modifier))
            return;

        weight.Comp.MovementSpeedModifier = modifier;
        Dirty(weight.Owner, weight.Comp);
        // ST:OW end

        _movementSpeedModifier.RefreshMovementSpeedModifiers(weight);
    }

    private void SetInsideWeight(Entity<STWeightComponent> weight, float inside)
    {
        var ev = new GetWeightModifiersEvent(inside, weight.Comp.OwnWeight); // ST:OW
        RaiseLocalEvent(weight, ev);

        weight.Comp.InsideWeight = ev.Inside;

        var speedModifier = Math.Clamp(
            1 - (weight.Comp.Total - weight.Comp.Overload) / (weight.Comp.TotalMaximum - weight.Comp.TotalOverload),
            0f,
            1f);

        SetMovementSpeedModifiers(weight, speedModifier);

        RaiseLocalEvent(weight, new STWeightChangedEvent(
            weight.Comp.Total,
            weight.Comp.TotalOverload,
            weight.Comp.TotalMaximum));
    }

    private void UpdateWeight(Entity<STWeightComponent> weight)
    {
        if (!TryComp<TransformComponent>(weight, out var transform))
            return;

        RefreshOwnWeight(weight); // ST:OW

        var newInside = 0f;
        var enumerator = transform.ChildEnumerator;
        while (enumerator.MoveNext(out var uid))
        {
            if (!_weightQuery.TryComp(uid, out var childWeight))  // ST:OW
                continue;

            if (EntityManager.IsQueuedForDeletion(uid))  // ST:OW
                continue;

            RefreshOwnWeight((uid, childWeight)); // ST:OW
            newInside += childWeight.Total; // ST:OW
        }

        SetInsideWeight(weight, newInside);

        // Call update the weight of the parent component if there is one
        TryUpdateParent(weight);
    }

    public void SetWeightLimits(Entity<STWeightComponent> weight, float newOverload, float newMaximum)
    {
        weight.Comp.Maximum = newMaximum;
        weight.Comp.Overload = newOverload;

        UpdateWeight(weight);
    }

    public bool TrySetWeightLimits(EntityUid uid, float newOverload, float newMaximum, STWeightComponent? weight = null)
    {
        if (!Resolve(uid, ref weight))
            return false;

        if (newMaximum == weight.Maximum && newOverload == weight.Overload)
            return false;

        SetWeightLimits((uid, weight), newOverload, newMaximum);
        return true;
    }

    public bool TryUpdateWeight(EntityUid? uid)
    {
        if (uid is not { } target)
            return false;

        if (!TryComp<STWeightComponent>(uid, out var comp))
            return false;

        UpdateWeight((target, comp));
        return true;
    }

    private bool TryUpdateParent(Entity<STWeightComponent> weight)
    {
        var parent = Transform(weight).ParentUid;
        if (!TryComp<STWeightComponent>(parent, out var comp))
            return false;

        UpdateWeight((parent, comp));
        return true;
    }

    // ST:OW begin
    public float GetOwnWeight(Entity<STWeightComponent?> ent)
    {
        if (!_weightQuery.Resolve(ent.Owner, ref ent.Comp, false))
            return 0f;

        RefreshOwnWeight((ent.Owner, ent.Comp));
        return ent.Comp.OwnWeight;
    }

    public float GetTotalWeight(Entity<STWeightComponent?> ent)
    {
        if (!_weightQuery.Resolve(ent.Owner, ref ent.Comp, false))
            return 0f;

        RefreshOwnWeight((ent.Owner, ent.Comp));
        var total = ent.Comp.OwnWeight;

        var enumerator = Transform(ent.Owner).ChildEnumerator;
        while (enumerator.MoveNext(out var child))
        {
            if (!EntityManager.IsQueuedForDeletion(child))
                total += GetTotalWeight(child);
        }

        return total;
    }

    public float GetPrototypeWeight(EntProtoId id)
    {
        if (!_prototype.TryIndex(id, out var proto) ||
            !proto.TryGetComponent<STWeightComponent>(out var weight, EntityManager.ComponentFactory))
            return 0f;

        return weight.Self;
    }

    private void RefreshOwnWeight(Entity<STWeightComponent> weight)
    {
        weight.Comp.Units = _stackQuery.TryComp(weight.Owner, out var stack) ? Math.Max(stack.Count, 0) : 1;
        weight.Comp.ContentsWeight = GetLiquidWeight(weight)
                                     + GetUnspawnedAmmoWeight(weight.Owner)
                                     + GetUnspawnedRevolverWeight(weight.Owner);
    }

    private float GetLiquidWeight(Entity<STWeightComponent> weight)
    {
        var perUnit = weight.Comp.SolutionWeightPerUnit;
        if (perUnit == 0f || !_solutionManagerQuery.TryComp(weight.Owner, out var manager))
            return 0f;

        var volume = FixedPoint2.Zero;

        if (manager.Solutions is { Count: > 0 })
        {
            foreach (var (_, solution) in _solutions.EnumerateSolutions(manager))
            {
                volume += solution.Volume;
            }
        }
        else
        {
            foreach (var name in manager.Containers)
            {
                if (_solutions.TryGetSolution(
                        (weight.Owner, manager), name, out _, out var solution))
                {
                    volume += solution.Volume;
                }
            }
        }

        return volume.Float() * perUnit;
    }

    private float GetUnspawnedAmmoWeight(EntityUid uid)
    {
        if (!_ballisticQuery.TryComp(uid, out var ammo) || ammo.UnspawnedCount <= 0)
            return 0f;
        
        var physical = ammo.Container?.ContainedEntities.Count ?? 0;
        var tracked = Math.Clamp(ammo.EntProtos.Count - physical, 0, ammo.UnspawnedCount);

        var weight = 0f;
        for (var i = 0; i < tracked; i++)
        {
            weight += GetPrototypeWeight(ammo.EntProtos[i]);
        }

        if (ammo.Proto is { } proto)
            weight += (ammo.UnspawnedCount - tracked) * GetPrototypeWeight(proto);

        return weight;
    }

    private float GetUnspawnedRevolverWeight(EntityUid uid)
    {
        if (!_revolverQuery.TryComp(uid, out var revolver) || revolver.FillPrototype == null)
            return 0f;

        var unspawned = 0;
        for (var i = 0; i < revolver.Chambers.Length; i++)
        {
            if (revolver.Chambers[i] != null && (i >= revolver.AmmoSlots.Count || revolver.AmmoSlots[i] == null))
                unspawned++;
        }

        return unspawned == 0 ? 0f : unspawned * GetPrototypeWeight(revolver.FillPrototype);
    }
    // ST:OW end
}
