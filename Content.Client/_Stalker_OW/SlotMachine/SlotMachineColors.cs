using System.Numerics;
using Content.Shared._Stalker_OW.SlotMachine;

namespace Content.Client._Stalker_OW.SlotMachine;

public static class SlotMachineColors
{
    public static readonly Color Subdued = Color.FromHex("#A3A59F");
    public static readonly Color Panel = Color.FromHex("#14160F");
    public static readonly Color PanelEdge = Color.FromHex("#51574D");
    private static readonly Color Uncommon = Color.FromHex("#5FBF5A");
    private static readonly Color Rare = Color.FromHex("#E0463C");
    private static readonly Color CommonBorder = Color.FromHex("#C9CDC5");
    
    public static Color? Text(SlotMachineRarity rarity)
    {
        return rarity switch
        {
            SlotMachineRarity.Uncommon => Uncommon,
            SlotMachineRarity.Rare => Rare,
            _ => (Color?) null,
        };
    }

    public static Color Border(SlotMachineRarity rarity)
    {
        return Text(rarity) ?? CommonBorder;
    }
    
    public static Color Rainbow(TimeSpan time, int index)
    {
        var hue = (time.TotalSeconds * 0.5 + index * 0.12) % 1d;
        return Color.FromHsv(new Vector4((float) hue, 0.7f, 1f, 1f));
    }
}
