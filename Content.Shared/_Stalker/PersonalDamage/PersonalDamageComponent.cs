using Content.Shared.Damage;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._Stalker.PersonalDamage;

[RegisterComponent, AutoGenerateComponentState]
public sealed partial class PersonalDamageComponent : Component
{
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public DamageSpecifier Damage = new();

    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public float StaminaDamage = 0f;

    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public float Interval = 1f;

    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public bool IgnoreResistances = true;

    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public bool InterruptsDoAfters = false;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public TimeSpan NextDamage = TimeSpan.Zero;
}
