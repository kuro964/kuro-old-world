using Content.Shared._Stalker_OW.SlotMachine;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._Stalker_OW.SlotMachine;

[UsedImplicitly]
public sealed class SlotMachineBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private SlotMachineWindow? _window;

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<SlotMachineWindow>();
        _window.SetMachine(Owner);
        _window.SpinRequested += tier => SendMessage(new SlotMachineSpinMessage(tier));
    }

    protected override void ReceiveMessage(BoundUserInterfaceMessage message)
    {
        base.ReceiveMessage(message);

        if (message is SlotMachineBalanceMessage balance)
            _window?.SetBalance(balance.Balance);
    }
}
