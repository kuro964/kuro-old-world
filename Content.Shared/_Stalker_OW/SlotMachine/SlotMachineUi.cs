using Robust.Shared.Serialization;

namespace Content.Shared._Stalker_OW.SlotMachine;

[Serializable, NetSerializable]
public enum SlotMachineUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class SlotMachineSpinMessage(int tier) : BoundUserInterfaceMessage
{
    public readonly int Tier = tier;
}

[Serializable, NetSerializable]
public sealed class SlotMachineBalanceMessage(int balance) : BoundUserInterfaceMessage
{
    public readonly int Balance = balance;
}
