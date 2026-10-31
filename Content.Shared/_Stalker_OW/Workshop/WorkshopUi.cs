using Content.Shared.DoAfter;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Stalker_OW.Workshop;

[Serializable, NetSerializable]
public enum WorkshopUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum WorkshopAction : byte
{
    Insert = 1,
    Eject,
    SelectSlot,
    Apply,
    Cancel,
}

[Serializable, NetSerializable]
public sealed class WorkshopMessage(WorkshopAction action, int slot = -1) : BoundUserInterfaceMessage
{
    public readonly WorkshopAction Action = action;
    public readonly int Slot = slot;
}

[Serializable, NetSerializable]
public sealed class WorkshopBoundUserInterfaceState : BoundUserInterfaceState
{
    public NetEntity? Target;
    public NetEntity? Worker;
    public bool Working;
    public TimeSpan Started;
    public TimeSpan Finishes;

    public Dictionary<int, EntProtoId?> Changes = new();
}

public sealed class WorkshopStatState
{
    public string Name = string.Empty;
    public float Current;
    public float Proposed;
    public bool Flat;
}

[Serializable, NetSerializable]
public sealed partial class WorkshopDoAfterEvent : DoAfterEvent
{
    [DataField]
    public int Token;

    public override DoAfterEvent Clone() => (WorkshopDoAfterEvent) MemberwiseClone();
}
