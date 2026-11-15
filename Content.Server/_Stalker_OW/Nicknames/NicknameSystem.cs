using Content.Shared._Stalker_OW.PublicName;
using Content.Shared.CCVar;
using Content.Shared.GameTicking;
using Content.Shared.IdentityManagement;
using Content.Shared.Preferences;
using Robust.Shared.Configuration;

namespace Content.Server._Stalker_OW.PublicName;

/// <summary>
/// Applies the player's nickname when they spawn and refreshes identity
/// </summary>
public sealed class STNicknameSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IdentitySystem _identity = default!;
    [Dependency] private readonly STPublicNameSystem _publicName = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        _publicName.SetNickname(args.Mob, GetValidatedNickname(args.Profile), args.Profile.STNicknameDisplayMode);
        _identity.QueueIdentityUpdate(args.Mob);
    }

    // Returns nicknames re-validated on server or empty string for none
    // Nicknames identical to player's actual name count as none
    public string GetValidatedNickname(HumanoidCharacterProfile profile)
    {
        var nickname = STNicknameRules.Sanitize(profile.STNickname, _cfg.GetCVar(CCVars.RestrictedNames));
        return nickname == profile.Name ? string.Empty : nickname;
    }
}