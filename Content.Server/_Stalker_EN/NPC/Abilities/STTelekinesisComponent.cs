using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._Stalker_EN.NPC.Abilities;

[RegisterComponent]
public sealed partial class STTelekinesisComponent : Component
{
    [DataField]
    public TimeSpan ThrowCooldown = TimeSpan.FromSeconds(6);

    [DataField]
    public TimeSpan ThrowWindup = TimeSpan.FromSeconds(0.8);

    [DataField]
    public float PickRange = 9f;

    [DataField]
    public float ThrowRange = 12f;

    [DataField]
    public float ThrowSpeed = 14f;

    [DataField]
    public float DamagePerMass = 1.5f;

    [DataField]
    public float MinDamage = 8f;

    [DataField]
    public float MaxDamage = 40f;

    [DataField]
    public TimeSpan OrbitCooldown = TimeSpan.FromSeconds(25);

    [DataField]
    public TimeSpan OrbitWindup = TimeSpan.FromSeconds(1.2);

    [DataField]
    public TimeSpan OrbitTime = TimeSpan.FromSeconds(6);

    [DataField]
    public TimeSpan OrbitShotInterval = TimeSpan.FromSeconds(0.35);

    [DataField]
    public int OrbitGuns = 4;

    [DataField]
    public float OrbitRadius = 1.3f;

    [DataField]
    public float OrbitRange = 14f;

    [DataField]
    public float GunSpriteAngle = 45f;

    [DataField]
    public List<EntProtoId> OrbitGunPool = new() { "STWeaponRifleAKS74", "STWeaponRifleAKM", "STWeaponRifleASVAL", "STWeaponRifleFAL", "STWeaponRifleAKS74U" };

    [DataField]
    public EntProtoId OrbitBullet = "Bullet545PC";

    [DataField]
    public float OrbitBulletSpeed = 25f;

    [DataField]
    public SoundSpecifier? LiftSound;

    [DataField]
    public float DamagePerGun = 25f;

    [DataField]
    public bool Orbit = true;

    [DataField]
    public bool ShieldStance;

    [DataField]
    public bool Disarm = true;

    [DataField]
    public TimeSpan DisarmCooldown = TimeSpan.FromSeconds(15);

    [DataField]
    public TimeSpan DisarmWindup = TimeSpan.FromSeconds(0.8);

    [DataField]
    public float DisarmRange = 9f;

    [ViewVariables]
    public TimeSpan NextDisarm;

    [ViewVariables]
    public TimeSpan? DisarmAt;

    [ViewVariables]
    public float OrbitDamage;

    [ViewVariables]
    public TimeSpan NextThink;

    [ViewVariables]
    public TimeSpan NextThrow;

    [DataField]
    public int RecentKept = 4;

    [ViewVariables]
    public readonly List<EntityUid> Recent = new();

    [ViewVariables]
    public EntityUid? Held;

    [ViewVariables]
    public TimeSpan ThrowAt;

    [ViewVariables]
    public TimeSpan NextOrbit;

    [ViewVariables]
    public TimeSpan OrbitStart;

    [ViewVariables]
    public TimeSpan OrbitEnd;

    [ViewVariables]
    public TimeSpan NextOrbitShot;

    [ViewVariables]
    public int OrbitTurn;

    [ViewVariables]
    public readonly List<EntityUid> Guns = new();
}

[RegisterComponent]
public sealed partial class STHurledComponent : Component
{
    [ViewVariables]
    public float Damage;

    [ViewVariables]
    public EntityUid Thrower;
}
