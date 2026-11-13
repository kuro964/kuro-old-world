using Robust.Shared.Audio;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._Stalker_EN.NPC.Combat;

[RegisterComponent, AutoGenerateComponentPause]
public sealed partial class STNpcMagazineComponent : Component
{
    [DataField]
    public int Capacity = 30;

    [DataField]
    public TimeSpan ReloadTime = TimeSpan.FromSeconds(2.5);

    [DataField]
    public TimeSpan ReloadJitter = TimeSpan.FromSeconds(0.8);

    [DataField]
    public float EarlyReloadChance = 0.35f;

    [DataField]
    public float EarlyReloadBelow = 0.6f;

    [DataField]
    public TimeSpan LullTime = TimeSpan.FromSeconds(1.5);

    [DataField]
    public int BurstMin = 3;

    [DataField]
    public int BurstMax = 8;

    [DataField]
    public TimeSpan BurstPauseMin = TimeSpan.FromSeconds(0.3);

    [DataField]
    public TimeSpan BurstPauseMax = TimeSpan.FromSeconds(1);

    [DataField]
    public SoundSpecifier? ReloadSound = new SoundPathSpecifier("/Audio/Weapons/Guns/MagIn/ltrifle_magin.ogg");

    [ViewVariables]
    public int Rounds;

    [ViewVariables]
    public int ReloadAt;

    [ViewVariables]
    public int BurstLeft;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan PauseUntil;

    [ViewVariables]
    public bool LullChecked = true;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan ReloadEnd;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan LastShot;
}
