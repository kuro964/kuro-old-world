using System.Numerics;
using Content.Client.UserInterface.Controls;

namespace Content.Client._Stalker_OW.Workshop;

public sealed class ModuleSlotButton : SlotControl
{
    public ModuleSlotButton()
    {
        MinSize = MaxSize = new Vector2(DefaultButtonSize);
        ProtoView.HorizontalAlignment = HAlignment.Center;
        ProtoView.VerticalAlignment = VAlignment.Center;
        ProtoView.SpriteOffset = false;
        ProtoView.MouseFilter = MouseFilterMode.Ignore;
        HoverSpriteView.MouseFilter = MouseFilterMode.Ignore;
        ButtonRect.HorizontalAlignment = HAlignment.Center;
        ButtonRect.VerticalAlignment = VAlignment.Center;
    }
}