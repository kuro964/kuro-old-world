using System.Numerics;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;

namespace Content.Client._Stalker_EN.NPC.Hive;

public sealed class STHiveSightOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IEyeManager _eyeManager = default!;

    private readonly SpriteSystem _sprite;
    private readonly SharedTransformSystem _transform;
    private readonly STHiveSightSystem _sight;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    public STHiveSightOverlay()
    {
        IoCManager.InjectDependencies(this);
        _sprite = _entMan.System<SpriteSystem>();
        _transform = _entMan.System<SharedTransformSystem>();
        _sight = _entMan.System<STHiveSightSystem>();
        ZIndex = 100;
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return _sight.Behind.Count > 0 && args.Viewport.Eye == _eyeManager.CurrentEye;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;
        var eyeRotation = args.Viewport.Eye?.Rotation ?? Angle.Zero;
        handle.Modulate = _sight.Tint;

        foreach (var uid in _sight.Behind)
        {
            if (!_entMan.TryGetComponent(uid, out SpriteComponent? sprite) ||
                !sprite.Visible ||
                !_entMan.TryGetComponent(uid, out TransformComponent? xform) ||
                xform.MapID != args.MapId)
            {
                continue;
            }

            var (position, rotation) = _transform.GetWorldPositionRotation(xform);
            if (!args.WorldBounds.Contains(position))
                continue;

            _sprite.RenderSprite((uid, sprite), handle, eyeRotation, rotation, position);
        }

        handle.Modulate = Color.White;
        handle.SetTransform(Matrix3x2.Identity);
    }
}
