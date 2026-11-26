using Content.Client.Gameplay;
using Content.Client.Outline;
using Content.Client.UserInterface.Systems.Actions;
using Content.Shared._Stalker_EN.NPC.Hive;
using Content.Shared.Mobs.Systems;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.State;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.Client._Stalker_EN.NPC.Hive;

public sealed class STHiveClaimOutlineSystem : EntitySystem
{
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IStateManager _state = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly TargetOutlineSystem _targetOutline = default!;

    private static readonly EntProtoId ClaimAction = "ActionSTHiveRecruit";
    private static readonly ProtoId<ShaderPrototype> FreeShader = "STHiveClaimFree";
    private static readonly ProtoId<ShaderPrototype> OwnedShader = "STHiveClaimOwned";

    private ShaderInstance _free = default!;
    private ShaderInstance _owned = default!;
    private Entity<SpriteComponent>? _lit;

    public override void Initialize()
    {
        base.Initialize();
        _free = _prototype.Index(FreeShader).InstanceUnique();
        _owned = _prototype.Index(OwnedShader).InstanceUnique();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        Unlight();
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_player.LocalEntity is not { } me || !Claiming())
        {
            Unlight();
            return;
        }

        _targetOutline.Disable();

        var hovered = Hovered();
        var shader = hovered is { } target ? ShaderFor(me, target) : null;
        if (hovered is not { } uid || shader == null || !TryComp<SpriteComponent>(uid, out var sprite))
        {
            Unlight();
            return;
        }

        if (_lit?.Owner != uid)
            Unlight();

        if (sprite.PostShader != null && sprite.PostShader != _free && sprite.PostShader != _owned)
            return;

        sprite.PostShader = shader;
        sprite.RenderOrder = EntityManager.CurrentTick.Value;
        _lit = (uid, sprite);
    }

    private bool Claiming()
    {
        var action = _ui.GetUIController<ActionUIController>().SelectingTargetFor;
        return action is { } id && MetaData(id).EntityPrototype is { } proto && proto.ID == ClaimAction.Id;
    }

    private EntityUid? Hovered()
    {
        if (_state.CurrentState is not GameplayStateBase screen)
            return null;

        return screen.GetClickedEntity(_eye.PixelToMap(_input.MouseScreenPosition));
    }

    private ShaderInstance? ShaderFor(EntityUid me, EntityUid target)
    {
        if (!TryComp<STHiveCallableComponent>(target, out var callable) || !_mobState.IsAlive(target))
            return null;

        if (callable.Leader == me)
            return _owned;

        return callable.Leader == null ? _free : null;
    }

    private void Unlight()
    {
        if (_lit is not { } lit)
            return;

        _lit = null;
        if (TerminatingOrDeleted(lit.Owner))
            return;

        if (lit.Comp.PostShader == _free || lit.Comp.PostShader == _owned)
        {
            lit.Comp.PostShader = null;
            lit.Comp.RenderOrder = 0;
        }
    }
}
