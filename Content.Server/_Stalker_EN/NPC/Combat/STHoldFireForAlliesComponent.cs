namespace Content.Server._Stalker_EN.NPC.Combat;

[RegisterComponent]
public sealed partial class STHoldFireForAlliesComponent : Component
{
    [DataField]
    public float LineWidth = 1f;

    [DataField]
    public float TouchRange = 0.75f;
}
