using Content.Server.NPC.Pathfinding;
using Robust.Shared.Map;

namespace Content.Server._Stalker_EN.NPC.Pathfinding;

[ByRefEvent]
public record struct STPathCostEvent(EntityCoordinates? Destination)
{
    public Func<PathPoly, PathPoly, float>? Cost;

    public void Add(Func<PathPoly, PathPoly, float> cost)
    {
        var previous = Cost;
        Cost = previous == null
            ? cost
            : (from, to) =>
            {
                var first = previous(from, to);
                return float.IsPositiveInfinity(first) ? first : first + cost(from, to);
            };
    }
}
