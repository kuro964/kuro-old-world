using System.Numerics;
using Content.Shared.Stacks;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;
using Robust.Shared.Utility;

namespace Content.Shared._Stalker_OW.SlotMachine;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause]
public sealed partial class SlotMachineComponent : Component
{
    public const int ReelCount = 3;

    /// <summary>
    /// Selectable spin prices and the chance of a win at each price
    /// </summary>
    [DataField(required: true)]
    public List<SlotMachineTier> Tiers = new();

    /// <summary>
    /// Symbols shown on the reels
    /// </summary>
    [DataField(required: true)]
    public List<SlotMachineSymbol> Symbols = new();

    [DataField(required: true)]
    public ProtoId<SlotMachinePrizePoolPrototype> PrizePool;

    /// <summary>
    /// Currency used for gambling
    /// </summary>
    [DataField]
    public ProtoId<StackPrototype> Currency = "Roubles";

    /// <summary>
    /// Seconds after the spin starts at which each reel stops (left to right)
    /// </summary>
    [DataField]
    public float[] ReelStopDelays = { 1.4f, 2.2f, 3.0f };

    /// <summary>
    /// Total duration of spin
    /// </summary>
    [DataField]
    public float SpinDuration = 3.4f;

    /// <summary>
    /// Where prizes appear in relation to the machine
    /// </summary>
    [DataField]
    public Vector2 DispenseOffset = new(0f, -0.75f);

    /// <summary>
    /// Played once when a spin starts
    /// </summary>
    [DataField]
    public SoundSpecifier? SpinSound;

    [DataField]
    public SoundSpecifier? WinSound;

    [DataField]
    public SoundSpecifier? LoseSound;

    [DataField]
    public SoundSpecifier? DispenseSound;

    /// <summary>
    /// Rare easter egg audio occasionally played
    /// </summary>
    [DataField]
    public SoundSpecifier? RareSpinSound;

    [DataField]
    public float RareSpinChance = 0.01f;

    /// <summary>
    /// Rare easter egg audio occasionally played
    /// </summary>
    [DataField]
    public SoundSpecifier? RareLoseSound;

    [DataField]
    public float RareLoseChance = 0.01f;

    /// <summary>
    /// On a loss, this is the chance that the first two reels are the same symbol
    /// </summary>
    [DataField]
    public float NearMissChance = 0.25f;

    /// <summary>
    /// Odds for the ???% prize
    /// </summary>
    [DataField]
    public float BonusChance = 0.00001f;

    [DataField, AutoNetworkedField]
    public bool Spinning;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan SpinStart;
    
    [DataField, AutoNetworkedField]
    public int[] Result = { 0, 1, 2 };

    [DataField, AutoNetworkedField]
    public SlotMachineOutcome LastOutcome;

    [DataField, AutoNetworkedField]
    public EntProtoId? LastPrize;
    
    [DataField, AutoNetworkedField]
    public EntProtoId? LastBonusPrize;
    
    [DataField, AutoNetworkedField]
    public SlotMachineRarity LastRarity;
    
    [ViewVariables]
    public EntProtoId? PendingPrize;
    
    [ViewVariables]
    public EntProtoId? PendingBonusPrize;

    [ViewVariables]
    public SlotMachineRarity PendingRarity;
    
    [ViewVariables]
    public int Stake;

    [ViewVariables]
    public EntityUid? Spinner;
}

[DataDefinition]
public sealed partial class SlotMachineTier
{
    [DataField(required: true)]
    public int Price;
    
    [DataField(required: true)]
    public float WinChance;
    
    [DataField(required: true)]
    public Dictionary<SlotMachineRarity, float> RarityWeights = new();
}

[DataDefinition]
public sealed partial class SlotMachineSymbol
{
    [DataField(required: true)]
    public SpriteSpecifier Icon = SpriteSpecifier.Invalid;
}

[Serializable, NetSerializable]
public enum SlotMachineOutcome : byte
{
    None,
    Win,
    Loss,
    Refunded,
}