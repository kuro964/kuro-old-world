using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server._Stalker_EN.NPC.Hive;

[RegisterComponent]
public sealed partial class STHiveCallableComponent : Component
{
}

[RegisterComponent]
public sealed partial class STHiveLeaderComponent : Component
{
    [ViewVariables]
    public readonly HashSet<EntityUid> Followers = new();

    [DataField]
    public EntityUid? CallAction;

    [DataField]
    public EntityUid? HoldAction;

    [DataField]
    public EntityUid? ReleaseAction;

    [ViewVariables]
    public TimeSpan NextShare;

    [ViewVariables]
    public readonly Dictionary<EntityUid, ICommonSession> Watching = new();
}

[RegisterComponent]
public sealed partial class STHiveScatterComponent : Component
{
    [ViewVariables]
    public int Tries;

    [ViewVariables]
    public TimeSpan NextCheck;
}

[RegisterComponent]
public sealed partial class STHiveFollowerComponent : Component
{
    [ViewVariables]
    public EntityUid Leader;

    [ViewVariables]
    public int Slot;

    [ViewVariables]
    public bool Hold;

    [ViewVariables]
    public EntityCoordinates? HoldAt;

    [ViewVariables]
    public EntityCoordinates? DownAt;

    [ViewVariables]
    public MapCoordinates? LastLeader;

    [ViewVariables]
    public TimeSpan NextThink;

    [ViewVariables]
    public TimeSpan DirectUntil;
}
