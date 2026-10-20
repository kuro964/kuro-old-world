namespace Content.Server._Stalker_EN.NPC.Steering;

[RegisterComponent]
public sealed partial class STNPCAvoidTilesComponent : Component
{
    [DataField]
    public TimeSpan AvoidTime = TimeSpan.FromSeconds(8);

    public readonly Dictionary<(EntityUid Graph, Vector2i Chunk, byte Tile), TimeSpan> Tiles = new();
}
