using Content.Shared.Roles;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Server.Players.JobWhitelist;

public sealed partial class JobWhitelistManager
{
    public void SetWhitelistCached(NetUserId player, ProtoId<JobPrototype> job, bool whitelisted)
    {
        if (!_whitelists.TryGetValue(player, out var whitelists))
            return;

        if (whitelisted)
            whitelists.Add(job);
        else
            whitelists.Remove(job);

        if (_player.TryGetSessionById(player, out var session))
            SendJobWhitelist(session);
    }
}
