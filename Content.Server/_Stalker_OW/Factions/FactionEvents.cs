namespace Content.Server._Stalker_OW.Bands;

public sealed class STBandPointsChangedEvent(string bandId) : EntityEventArgs
{
    public string BandId { get; } = bandId;
}

/// <summary>
/// Raised after faction relations or relation proposals change
/// </summary>
public sealed class STFactionRelationsChangedEvent : EntityEventArgs;

/// <summary>
/// Raised after a war zone's attacker or owner changes
/// </summary>
public sealed class STWarZoneChangedEvent : EntityEventArgs;
