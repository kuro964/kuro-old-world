using Content.Shared._Stalker_OW.PublicName;

namespace Content.Server.Database;

public sealed record StalkerBandMemberRow(Guid UserId, string UserName, string RoleId, string? CharacterName, string? Nickname, STNicknameDisplay DisplayMode = STNicknameDisplay.NicknameOnly);
