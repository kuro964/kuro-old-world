using System.Numerics;

namespace Content.Server._Stalker_EN.NPC.Steering;

[RegisterComponent]
public sealed partial class STStuckComponent : Component
{
    [ViewVariables]
    public Vector2 LastPosition;

    [ViewVariables]
    public TimeSpan NextCheck;

    [ViewVariables]
    public TimeSpan? BlockedSince;
}
