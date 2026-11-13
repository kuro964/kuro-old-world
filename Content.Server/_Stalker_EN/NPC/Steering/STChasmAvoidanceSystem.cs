using System.Numerics;
using Content.Server.NPC.Components;
using Content.Shared.Chasm;
using Content.Shared.StepTrigger.Components;
using Content.Shared.Whitelist;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Stalker_EN.NPC.Steering;

public sealed class STChasmAvoidanceSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;

    private const float Range = 1.2f;
    private const float FullDanger = 0.6f;
    private const float LineStep = 0.25f;
    private const int Reach = 2;

    private readonly Dictionary<EntityUid, Dictionary<Vector2i, EntityUid>> _tiles = new();
    private readonly Dictionary<EntityUid, (EntityUid Grid, Vector2i Tile)> _indexed = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ChasmComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<ChasmComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NPCSteeringComponent, STSteeringTermsEvent>(OnSteeringTerms);
    }

    public bool IsChasm(EntityUid grid, Vector2i tile)
    {
        return _tiles.TryGetValue(grid, out var tiles) && IsOpen(grid, tiles, tile);
    }

    public bool IsChasm(EntityCoordinates coordinates)
    {
        if (_transform.GetGrid(coordinates) is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComp))
            return false;

        return IsChasm(grid, _map.TileIndicesFor(grid, gridComp, coordinates));
    }

    public bool Covered(EntityUid chasm, EntityUid grid, MapGridComponent gridComp, Vector2i tile)
    {
        if (!TryComp<StepTriggerComponent>(chasm, out var trigger) || trigger.Blacklist == null)
            return false;

        var anchored = _map.GetAnchoredEntitiesEnumerator(grid, gridComp, tile);
        while (anchored.MoveNext(out var ent))
        {
            if (ent != chasm && _whitelist.IsWhitelistPass(trigger.Blacklist, ent.Value))
                return true;
        }

        return false;
    }

    public bool LineCrosses(EntityUid uid, EntityCoordinates target, float radius)
    {
        var xform = Transform(uid);
        if (xform.GridUid is not { } grid || !_tiles.TryGetValue(grid, out var tiles))
            return false;

        var from = _transform.WithEntityId(xform.Coordinates, grid).Position;
        var line = _transform.WithEntityId(target, grid).Position - from;
        var length = line.Length();
        if (length < 0.01f)
            return false;

        var side = new Vector2(-line.Y, line.X) / length * radius;
        var steps = (int) MathF.Ceiling(length / LineStep);
        for (var i = 1; i <= steps; i++)
        {
            var point = from + line * (i / (float) steps);
            if (IsOpen(grid, tiles, point.Floored()) ||
                IsOpen(grid, tiles, (point + side).Floored()) ||
                IsOpen(grid, tiles, (point - side).Floored()))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsOpen(EntityUid grid, Dictionary<Vector2i, EntityUid> tiles, Vector2i tile)
    {
        return tiles.TryGetValue(tile, out var chasm) &&
               !(TryComp<MapGridComponent>(grid, out var gridComp) && Covered(chasm, grid, gridComp, tile));
    }

    private void OnMapInit(Entity<ChasmComponent> ent, ref MapInitEvent args)
    {
        var xform = Transform(ent);
        if (!xform.Anchored || xform.GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComp))
            return;

        var tile = _map.TileIndicesFor(grid, gridComp, xform.Coordinates);
        if (!_tiles.TryGetValue(grid, out var tiles))
            _tiles[grid] = tiles = new Dictionary<Vector2i, EntityUid>();

        tiles[tile] = ent;
        _indexed[ent] = (grid, tile);
    }

    private void OnShutdown(Entity<ChasmComponent> ent, ref ComponentShutdown args)
    {
        if (!_indexed.Remove(ent, out var entry) || !_tiles.TryGetValue(entry.Grid, out var tiles))
            return;

        if (tiles.TryGetValue(entry.Tile, out var owner) && owner == ent.Owner)
            tiles.Remove(entry.Tile);

        if (tiles.Count == 0)
            _tiles.Remove(entry.Grid);
    }

    private void OnSteeringTerms(Entity<NPCSteeringComponent> ent, ref STSteeringTermsEvent args)
    {
        if (args.Transform.GridUid is not { } grid || !_tiles.TryGetValue(grid, out var tiles))
            return;

        var position = _transform.WithEntityId(args.Transform.Coordinates, grid).Position;
        var rotation = _transform.GetWorldRotation(grid);
        var center = position.Floored();

        for (var x = -Reach; x <= Reach; x++)
        {
            for (var y = -Reach; y <= Reach; y++)
            {
                var tile = center + new Vector2i(x, y);
                if (!IsOpen(grid, tiles, tile))
                    continue;

                var closest = Vector2.Clamp(position, (Vector2) tile, (Vector2) tile + Vector2.One);
                var offset = closest - position;
                var distance = offset.Length();
                if (distance >= Range)
                    continue;

                var direction = distance > 0.01f ? offset : new Vector2(tile.X + 0.5f, tile.Y + 0.5f) - position;
                args.Steering.ExtraDanger.Add((rotation.RotateVec(direction), Math.Clamp((Range - distance) / (Range - FullDanger), 0f, 1f)));
            }
        }
    }
}
