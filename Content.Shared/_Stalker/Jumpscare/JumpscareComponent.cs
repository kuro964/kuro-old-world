using System.Numerics;
using Content.Shared.Damage;
using Robust.Shared.GameStates;

namespace Content.Shared._Stalker.Jumpscare
{
    [RegisterComponent, NetworkedComponent, AutoGenerateComponentPause]
    public sealed partial class JumpscareComponent : Component
    {
        [DataField, ViewVariables(VVAccess.ReadWrite)]
        public float AttackRadius = 6f;

        [DataField, ViewVariables(VVAccess.ReadWrite)]
        public float AttackDistance = 1.5f;

        [DataField, ViewVariables(VVAccess.ReadWrite)]
        public float JumpDistance = 7f;

        [DataField, ViewVariables(VVAccess.ReadWrite)]
        public float JumpPower = 14f;

        [DataField, ViewVariables(VVAccess.ReadWrite)]
        public bool Away;

        [DataField(required: true), ViewVariables(VVAccess.ReadWrite)]
        public DamageSpecifier ChargeDamage = default!;

        [AutoPausedField]
        public TimeSpan? StartTime = TimeSpan.FromSeconds(0f);
        [AutoPausedField]
        public TimeSpan? EndTime = TimeSpan.FromSeconds(0f);

        [DataField, ViewVariables(VVAccess.ReadWrite)]
        public float ReloadTime = 6f;
        [DataField, ViewVariables(VVAccess.ReadWrite)]
        public float RandomiseReloadTime = 2f;

        [AutoPausedField]
        public TimeSpan? PreparingStartTime = TimeSpan.FromSeconds(0f);
        [AutoPausedField]
        public TimeSpan? PreparingEndTime = TimeSpan.FromSeconds(0f);
        [DataField, AutoPausedField]
        public TimeSpan? PreparingReloadTime = TimeSpan.FromSeconds(0.5f);

        [ViewVariables(VVAccess.ReadWrite)]
        public bool OnCoolDown = true;

        [AutoPausedField]
        public TimeSpan? NextTimeUpdate;

        [DataField]
        public float UpdateCooldown;
        
        [DataField]
        public float TargetSearchInterval = 0.2f; // ST:OW

        [AutoPausedField]
        public TimeSpan NextTargetSearchTime; // ST:OW

        [DataField, AutoPausedField]
        public TimeSpan SlowdownTime = TimeSpan.FromSeconds(0.1f);

        // steps params
        [ViewVariables(VVAccess.ReadWrite)]
        public Vector2 JumpTarget = new(0, 0);

        [ViewVariables(VVAccess.ReadWrite)]
        public int CurrentStep = 0;

        [DataField, ViewVariables(VVAccess.ReadWrite)]
        public int TotalSteps = 3;

        [DataField, ViewVariables(VVAccess.ReadWrite)]
        public float StepInterval = 0.025f;

        [AutoPausedField]
        public TimeSpan? NextStepTime;

        [ViewVariables(VVAccess.ReadWrite)]
        public bool MovingToJumpTarget = false;

        [DataField]
        public float HitRange;

        [DataField]
        public TimeSpan HitKnockdown;

        [DataField]
        public float HitThrow;

        [DataField]
        public TimeSpan RecoveryTime;

        [DataField]
        public Robust.Shared.Audio.SoundSpecifier? WindupSound;

        [ViewVariables]
        public EntityUid? LungeTarget;
    }
}
