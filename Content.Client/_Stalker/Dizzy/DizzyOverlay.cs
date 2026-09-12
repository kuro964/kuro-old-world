using Content.Shared._Stalker.Dizzy;
using Content.Shared.StatusEffect;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Content.Shared.CCVar; // ST:OW
using Robust.Shared.Configuration; // ST:OW

namespace Content.Client._Stalker.Dizzy;

public sealed class DizzyOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IConfigurationManager _config = default!; // ST:OW

    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    public override bool RequestScreenTexture => true;
    private readonly ShaderInstance _dizzyShader;
    private readonly StatusEffectsSystem _statusEffects; // ST:OW

    private bool _isActive;
    // ST:OW begin
    private bool _reducedMotion;
    private float _effectStrength;
    private const float NormalPsiIntensity = 0.20f;
    private const float FadeInDuration = 0.50f;
    private const float FadeOutDuration = 0.75f;
    // ST:OW end
    
    // ST:OW begin
    public DizzyOverlay()
    {
        IoCManager.InjectDependencies(this);

        _statusEffects =
            _entityManager.System<StatusEffectsSystem>();

        _dizzyShader =
            _prototypeManager.Index<ShaderPrototype>("STDizzy")
                .InstanceUnique();

        _config.OnValueChanged(
            CCVars.ReducedMotion,
            value => _reducedMotion = value,
            invokeImmediately: true);
    }
    // ST:OW end

    public void Stop()
    {
        _isActive = false;
        _effectStrength = 0f; // ST:OW
    }

    // ST:OW begin
    protected override void FrameUpdate(FrameEventArgs args)
    {
        if (_playerManager.LocalEntity is not { } player)
        {
            _isActive = false;
            _effectStrength = 0f;
            return;
        }

        if (!_entityManager.TryGetComponent<StatusEffectsComponent>(player, out var status) ||
            !_entityManager.HasComponent<DizzyComponent>(player))
        {
            _isActive = false;
            _effectStrength = 0f;
            return;
        }

        _isActive = true;

        if (!_statusEffects.TryGetTime(player, SharedDizzySystem.DizzyKey, out var time, status))
        {
            _effectStrength = 1f;
            return;
        }

        var timeVal = time.Value;
        var now = _timing.CurTime;

        var timeSinceStart = (float)(now - timeVal.Item1).TotalSeconds;
        var timeUntilEnd = (float)(timeVal.Item2 - now).TotalSeconds;

        var fadeIn = Math.Clamp(timeSinceStart / FadeInDuration, 0f, 1f);
        var fadeOut = Math.Clamp(timeUntilEnd / FadeOutDuration, 0f, 1f);

        _effectStrength = Math.Min(fadeIn, fadeOut);
    }
    // ST:OW end

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        if (!_entityManager.TryGetComponent(_playerManager.LocalEntity, out EyeComponent? eyeComp))
            return false;

        if (args.Viewport.Eye != eyeComp.Eye)
            return false;

        return _isActive && _effectStrength > 0f;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        // ST:OW begin
        if (_effectStrength <= 0f)
            return;

        var handle = args.WorldHandle;
        
        var bounds = args.WorldBounds;

        if (_reducedMotion)
        {
            handle.DrawRect(
                bounds,
                new Color(
                    0.055f,
                    0.015f,
                    0.075f,
                    0.42f * _effectStrength));

            return;
        }

        if (ScreenTexture == null)
            return;

        _dizzyShader.SetParameter("SCREEN_TEXTURE", ScreenTexture);
        _dizzyShader.SetParameter("boozePower", NormalPsiIntensity * _effectStrength);

        handle.UseShader(_dizzyShader);
        handle.DrawRect(bounds, Color.White);
        handle.UseShader(null);

        handle.DrawRect(
            bounds,
            new Color(
                0.055f,
                0.015f,
                0.075f,
                0.36f * _effectStrength));
    }
    // ST:OW end
}
