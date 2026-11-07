using Content.Shared.Stacks;
using Robust.Shared.Spawners;

namespace Content.Server._Stalker_OW.Food;

public sealed class STOWCuringStackSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<STOWCuringStackComponent, StackCountChangedEvent>(OnStackCountChanged);
        SubscribeLocalEvent<STOWCuringStackComponent, StackSplitEvent>(OnStackSplit);
    }

    private void OnStackCountChanged(Entity<STOWCuringStackComponent> entity, ref StackCountChangedEvent args)
    {
        // Reset time when something is added to the stack
        if (args.NewCount <= args.OldCount || !TryComp<TimedDespawnComponent>(entity, out var timer))
            return;

        timer.Lifetime = entity.Comp.Duration;
    }

    private void OnStackSplit(Entity<STOWCuringStackComponent> entity, ref StackSplitEvent args)
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