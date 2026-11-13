using Robust.Client.Graphics;

namespace Content.Client._Stalker_EN.NPC.Awareness;

public sealed class STNoticeMeterSystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlay = default!;

    public override void Initialize()
    {
        base.Initialize();
        _overlay.AddOverlay(new STNoticeMeterOverlay());
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlay.RemoveOverlay<STNoticeMeterOverlay>();
    }
}
