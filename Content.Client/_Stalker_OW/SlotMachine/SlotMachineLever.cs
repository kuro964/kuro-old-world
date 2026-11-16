using System.Numerics;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client._Stalker_OW.SlotMachine;

public sealed class SlotMachineLever : ContainerButton
{
    private const float KnobSize = 26f;
    private const float Travel = 74f;
    private const float DownTime = 0.2f;
    private const float HoldTime = 0.06f;
    private const float UpTime = 0.3f;
    private const float TotalTime = DownTime + HoldTime + UpTime;

    private static readonly Color KnobColor = Color.FromHex("#C4352B");
    private static readonly Color KnobDisabledColor = Color.FromHex("#6E3530");

    private readonly StyleBoxFlat _knobStyle = new(KnobColor)
    {
        BorderColor = Color.FromHex("#2A0D0A"),
        BorderThickness = new Thickness(2),
    };

    private readonly PanelContainer _knob;
    private float _elapsed = TotalTime;

    public SlotMachineLever()
    {
        MinSize = new Vector2(KnobSize + 8f, KnobSize + Travel);
        VerticalAlignment = VAlignment.Center;

        AddChild(new PanelContainer
        {
            MinWidth = 8,
            MaxWidth = 8,
            HorizontalAlignment = HAlignment.Center,
            VerticalExpand = true,
            Margin = new Thickness(0, KnobSize / 2f),
            MouseFilter = MouseFilterMode.Ignore,
            PanelOverride = new StyleBoxFlat(SlotMachineColors.Panel)
            {
                BorderColor = SlotMachineColors.PanelEdge,
                BorderThickness = new Thickness(1),
            },
        });

        _knob = new PanelContainer
        {
            MinSize = new Vector2(KnobSize),
            MaxSize = new Vector2(KnobSize),
            HorizontalAlignment = HAlignment.Center,
            VerticalAlignment = VAlignment.Top,
            MouseFilter = MouseFilterMode.Ignore,
            PanelOverride = _knobStyle,
        };
        AddChild(_knob);
    }

    public void Pull()
    {
        _elapsed = 0f;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        _knobStyle.BackgroundColor = Disabled ? KnobDisabledColor : KnobColor;

        if (_elapsed >= TotalTime)
            return;

        _elapsed = MathF.Min(TotalTime, _elapsed + args.DeltaSeconds);
        _knob.Margin = new Thickness(0, GetPosition(_elapsed) * Travel, 0, 0);
    }

    private static float GetPosition(float time)
    {
        if (time < DownTime)
        {
            var down = 1f - time / DownTime;
            return 1f - down * down;
        }

        time -= DownTime;
        if (time < HoldTime)
            return 1f;

        var up = 1f - MathF.Min(1f, (time - HoldTime) / UpTime);
        return up * up * up;
    }
}
