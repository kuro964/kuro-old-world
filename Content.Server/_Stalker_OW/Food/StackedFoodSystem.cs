using Content.Server.Construction;
using Content.Server.Construction.Components;
using Content.Shared._Stalker_OW.Stacks;
using Content.Shared.Atmos.Rotting;
using Content.Shared.Nutrition.Components;
using Content.Shared.Stacks;
using Robust.Shared.Spawners;

namespace Content.Server._Stalker_OW.Food;

public sealed class StackedFoodSystem : EntitySystem
{
    [Dependency] private readonly SharedRottingSystem _rotting = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;
    
    private readonly Dictionary<EntityUid, int> _pendingCounts = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ConstructionChangeEntityEvent>(OnConstructionChangeEntity);
        SubscribeLocalEvent<ConstructionComponent, AfterConstructionChangeEntityEvent>(OnAfterConstructionChangeEntity);

        SubscribeLocalEvent<PerishableComponent, StackMergedEvent>(OnPerishableMerged);
        SubscribeLocalEvent<PerishableComponent, StackSplitEvent>(OnPerishableSplit);

        SubscribeLocalEvent<STOWCuringStackComponent, StackCountChangedEvent>(OnCuringStackCountChanged);
        SubscribeLocalEvent<STOWCuringStackComponent, StackSplitEvent>(OnCuringStackSplit);
    }

    private void OnConstructionChangeEntity(ConstructionChangeEntityEvent args)
    {
        if (!HasComp<EdibleComponent>(args.Old) ||
            !TryComp<StackComponent>(args.Old, out var stack) ||
            stack.Unlimited)
        {
            return;
        }

        var count = _stack.GetCount((args.Old, stack));
        if (count > 1)
            _pendingCounts[args.New] = count;
    }

    private void OnAfterConstructionChangeEntity(Entity<ConstructionComponent> ent, 
        ref AfterConstructionChangeEntityEvent args)
    {
        if (_pendingCounts.Remove(ent.Owner, out var count))
            DistributeUnits(ent.Owner, count);
    }
    
    public void DistributeUnits(EntityUid first, int units)
    {
        if (units <= 0)
            return;

        var protoId = MetaData(first).EntityPrototype?.ID;

        var held = HoldUnits(first, units);
        if (held <= 0)
            return;

        var remaining = units - held;
        if (remaining <= 0)
            return;

        if (protoId == null)
        {
            Log.Error($"Can't spawn {remaining} overflow unit(s) of {ToPrettyString(first)}: it has no prototype.");
            return;
        }

        while (remaining > 0)
        {
            var extra = SpawnNextToOrDrop(protoId, first);
            var extraHeld = HoldUnits(extra, remaining);
            if (extraHeld <= 0)
            {
                Del(extra);
                return;
            }

            remaining -= extraHeld;
        }
    }
    
    private int HoldUnits(EntityUid uid, int units)
    {
        if (!TryComp<StackComponent>(uid, out var stack))
            return 1;

        var max = _stack.GetMaxCount(stack);
        if (max <= 0)
        {
            Log.Error($"{ToPrettyString(uid)} has a stack max count of {max}.");
            return 0;
        }

        var held = Math.Min(units, max);
        _stack.SetCount((uid, stack), held);
        return held;
    }

    private void OnPerishableMerged(Entity<PerishableComponent> ent, ref StackMergedEvent args)
    {
        // The merged stack is as old as its oldest item
        _rotting.InheritRotProgress(args.Donor, ent.Owner);
    }

    private void OnPerishableSplit(Entity<PerishableComponent> ent, ref StackSplitEvent args)
    {
        // Items split off from stack are as old as the stack
        _rotting.InheritRotProgress(ent.Owner, args.NewId);
    }

    private void OnCuringStackCountChanged(Entity<STOWCuringStackComponent> entity, ref StackCountChangedEvent args)
    {
        // Reset time when something is added to the stack
        if (args.NewCount <= args.OldCount || !TryComp<TimedDespawnComponent>(entity, out var timer))
            return;

        timer.Lifetime = entity.Comp.Duration;
    }

    private void OnCuringStackSplit(Entity<STOWCuringStackComponent> entity, ref StackSplitEvent args)
    {
        if (!TryComp<TimedDespawnComponent>(entity, out var sourceTimer) ||
            !TryComp<TimedDespawnComponent>(args.NewId, out var splitTimer))
        {
            return;
        }

        // If a stack of items that are curing is split,
        // Then both items will retain the original stack's timer
        splitTimer.Lifetime = sourceTimer.Lifetime;
    }
}
