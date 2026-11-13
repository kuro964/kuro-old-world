namespace Content.Server._Stalker_EN.NPC.Combat;

[RegisterComponent]
public sealed partial class STNpcBombConditionComponent : Component
{
    [DataField]
    public HashSet<string> Keys = new() { "trigger", "stageTwo" };

    [DataField]
    public float Radius = 4f;
}
