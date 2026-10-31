using Content.Shared.DoAfter;
using Robust.Shared.Containers;

namespace Content.Server._Stalker_OW.Workshop;

// Holds the equipment and pending modules until the DoAfter finishes
[RegisterComponent]
public sealed partial class EquipmentModificationWorkbenchComponent : Component
{
    public const string TargetSlotId = "workshop-target";
    public const string PendingContainerId = "workshop-pending";
    
    [DataField]
    public float RemovalTime = 1f;

    [ViewVariables]
    public Container Pending = default!;

    // Player who owns the interaction
    [ViewVariables]
    public EntityUid? Worker;

    // Slot index to held module
    [ViewVariables]
    public Dictionary<int, EntityUid?> Changes = new();

    [ViewVariables]
    public DoAfterId? DoAfter;

    // Used to ignore completion events from an older job
    [ViewVariables]
    public int Token;

    [ViewVariables]
    public TimeSpan Started;

    [ViewVariables]
    public TimeSpan Finishes;
}
