using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Stalker_OW.SlotMachine;

[Prototype("slotMachinePrizePool")]
public sealed partial class SlotMachinePrizePoolPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public List<SlotMachinePrize> Prizes = new();
    
    public static bool IsUsableWeight(float weight)
    {
        return weight > 0f && float.IsFinite(weight);
    }

    public List<SlotMachinePrize> GetPrizes(IPrototypeManager prototypes, SlotMachineRarity rarity)
    {
        var prizes = new List<SlotMachinePrize>();

        foreach (var prize in Prizes)
        {
            if (prize.Rarity == rarity && IsUsableWeight(prize.Weight) && prototypes.HasIndex<EntityPrototype>(prize.Id))
                prizes.Add(prize);
        }

        return prizes;
    }

    public List<SlotMachinePrizeTier> GetTiers(IPrototypeManager prototypes, Dictionary<SlotMachineRarity, float> weights)
    {
        var tiers = new List<SlotMachinePrizeTier>();
        var totalShare = 0d;

        foreach (var rarity in Enum.GetValues<SlotMachineRarity>())
        {
            if (rarity == SlotMachineRarity.Mystery || !weights.TryGetValue(rarity, out var share) || !IsUsableWeight(share))
                continue;

            var prizes = GetPrizes(prototypes, rarity);
            if (prizes.Count == 0)
                continue;

            tiers.Add(new SlotMachinePrizeTier(rarity, share, prizes));
            totalShare += share;
        }

        foreach (var tier in tiers)
        {
            tier.Chance /= totalShare;
        }

        return tiers;
    }
}

[DataDefinition]
public sealed partial class SlotMachinePrize
{
    [DataField(required: true)]
    public EntProtoId Id;
    [DataField]
    public SlotMachineRarity Rarity;
    [DataField]
    public SlotMachinePrizeCategory Category = SlotMachinePrizeCategory.Other;
    [DataField]
    public float Weight = 1f;
}

public sealed class SlotMachinePrizeTier(SlotMachineRarity rarity, double chance, List<SlotMachinePrize> prizes)
{
    public readonly SlotMachineRarity Rarity = rarity;
    public double Chance = chance;
    public readonly List<SlotMachinePrize> Prizes = prizes;
}

[Serializable, NetSerializable]
public enum SlotMachineRarity : byte
{
    Common,
    Uncommon,
    Rare,
    Mystery,
}

public enum SlotMachinePrizeCategory : byte
{
    Headwear,
    Glasses,
    Masks,
    Scarves,
    Gloves,
    Cloaks,
    Other,
}