using System.Numerics;

namespace Content.Server._Stalker_EN.NPC.Steering;

[RegisterComponent]
public sealed partial class STStuckSmashComponent : Component
{
    [ViewVariables]
    public Vector2 LastPosition;

    [ViewVariables]
    public TimeSpan NextCheck;
}
