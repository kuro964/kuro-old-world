using Robust.Shared.Map;

namespace Content.Server._Stalker_EN.NPC.Awareness;

[ByRefEvent]
public readonly record struct STExplosionHeardEvent(MapCoordinates Epicenter, float Intensity, EntityUid? Source);
