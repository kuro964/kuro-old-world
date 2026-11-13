using Content.Server.NPC;
using Content.Server.NPC.HTN.Preconditions;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;

namespace Content.Server._Stalker_EN.NPC.Combat;

public sealed partial class STGunCanFirePrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!_entManager.System<GunSystem>().TryGetGun(owner, out var gunUid, out _))
            return false;

        if (_entManager.HasComponent<RechargeBasicEntityAmmoComponent>(gunUid))
            return true;

        var ammo = new GetAmmoCountEvent();
        _entManager.EventBus.RaiseLocalEvent(gunUid, ref ammo);
        return ammo.Count > 0;
    }
}
