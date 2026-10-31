using Content.Shared._Stalker_OW.Workshop;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._Stalker_OW.Workshop;

[UsedImplicitly]
public sealed class WorkshopBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private WorkshopWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<WorkshopWindow>();
        _window.ActionRequested += SendMessage;
        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is WorkshopBoundUserInterfaceState workshop)
            _window?.SetState(workshop);
    }

    public void Refresh() => _window?.Refresh();
}
