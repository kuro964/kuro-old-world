using System.Numerics;
using Content.Shared._ES.Viewcone;
using Content.Shared._Stalker_EN.NPC.Awareness;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._Stalker_EN.NPC.Awareness;

public sealed class STNoticeMeterOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> UnshadedShader = "unshaded";
    private static readonly ResPath Rsi = new("/Textures/_Stalker_EN/Interface/notice_meter.rsi");

    [Dependency] private readonly IEntityManager _entity = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;

    private SpriteSystem? _sprite;
    private TransformSystem? _transform;
    private ShaderInstance? _unshaded;
    private readonly SpriteSpecifier.Rsi _frame = new(Rsi, "frame");
    private readonly SpriteSpecifier.Rsi _fill = new(Rsi, "fill");

    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowFOV;

    public STNoticeMeterOverlay()
    {
        IoCManager.InjectDependencies(this);
        ZIndex = 9;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_entity.TryGetComponent<STNoticeMeterComponent>(_player.LocalEntity, out var meters) || meters.Meters.Count == 0)
            return;

        _sprite ??= _entity.System<SpriteSystem>();
        _transform ??= _entity.System<TransformSystem>();
        _unshaded ??= _prototype.Index(UnshadedShader).Instance();

        var handle = args.WorldHandle;
        var rotation = Matrix3Helpers.CreateRotation(-(args.Viewport.Eye?.Rotation ?? default));
        var frame = _sprite.Frame0(_frame);
        var fill = _sprite.Frame0(_fill);
        var ppm = (float) EyeManager.PixelsPerMeter;
        var size = new Vector2(frame.Width, frame.Height) / ppm;
        var bounds = args.WorldAABB.Enlarged(2f);

        handle.UseShader(_unshaded);
        foreach (var (net, value) in meters.Meters)
        {
            if (!_entity.TryGetEntity(net, out var npc) ||
                !_entity.TryGetComponent<TransformComponent>(npc, out var xform) ||
                xform.MapID != args.MapId ||
                !_entity.TryGetComponent<SpriteComponent>(npc, out var sprite) ||
                !sprite.Visible ||
                _entity.TryGetComponent<ESViewconeOccludableComponent>(npc, out var occludable) && occludable.IsHidden)
            {
                continue;
            }

            var position = _transform.GetWorldPosition(xform);
            if (!bounds.Contains(position))
                continue;

            var top = (_sprite.GetLocalBounds((npc.Value, sprite)).Height + sprite.Offset.Y) / 2f;
            var origin = new Vector2(-size.X / 2f, top + 0.1f);
            handle.SetTransform(Matrix3x2.Multiply(rotation, Matrix3Helpers.CreateTranslation(position)));
            handle.DrawTexture(frame, origin);

            var noticed = value == STNoticeMeterComponent.Noticed;
            var progress = noticed ? 1f : Math.Clamp(value / (float) STNoticeMeterComponent.Full, 0f, 1f);
            var rows = MathF.Round(progress * fill.Height);
            if (rows <= 0f)
                continue;

            var color = noticed
                ? Color.FromHex("#E8402F")
                : progress < 0.5f
                    ? Color.InterpolateBetween(Color.White, Color.FromHex("#F4C63D"), progress * 2f)
                    : Color.InterpolateBetween(Color.FromHex("#F4C63D"), Color.FromHex("#F08A2C"), (progress - 0.5f) * 2f);
            var quad = Box2.FromDimensions(origin, new Vector2(size.X, rows / ppm));
            handle.DrawTextureRectRegion(fill, quad, color, new UIBox2(0f, fill.Height - rows, fill.Width, fill.Height));
        }

        handle.UseShader(null);
        handle.SetTransform(Matrix3x2.Identity);
    }
}
