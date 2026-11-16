using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client._Stalker_OW.SlotMachine;

public sealed class SlotMachineRainbowLabel : BoxContainer
{
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly List<Label> _letters = new();

    public SlotMachineRainbowLabel(string text)
    {
        IoCManager.InjectDependencies(this);

        Orientation = LayoutOrientation.Horizontal;
        SeparationOverride = 0;

        foreach (var letter in text)
        {
            var label = new Label { Text = letter.ToString() };
            _letters.Add(label);
            AddChild(label);
        }
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        var now = _timing.RealTime;

        for (var i = 0; i < _letters.Count; i++)
        {
            _letters[i].FontColorOverride = SlotMachineColors.Rainbow(now, i);
        }
    }
}
