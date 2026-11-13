using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server._Stalker_EN.NPC.Anomaly;

[RegisterComponent]
public sealed partial class STAnomalyAvoidanceComponent : Component
{
    [DataField]
    public float Weight = 0.5f;

    [DataField]
    public float Range = 3f;

    [DataField]
    public bool IgnoreWhileChasing = true;

    [ViewVariables]
    public readonly List<(Vector2 Center, float Radius)> Nearby = new();

    [ViewVariables]
    public MapId NearbyMap;

    [ViewVariables]
    public TimeSpan NextRefresh;

    [ViewVariables]
    public EntityCoordinates? Escape;
}
