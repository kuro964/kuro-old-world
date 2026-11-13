using Content.Server.NPC.Components;

namespace Content.Server._Stalker_EN.NPC.Steering;

[ByRefEvent]
public readonly record struct STSteeringTermsEvent(NPCSteeringComponent Steering, TransformComponent Transform, TimeSpan CurTime);
