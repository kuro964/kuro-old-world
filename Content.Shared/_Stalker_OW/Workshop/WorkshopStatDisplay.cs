namespace Content.Shared._Stalker_OW.Workshop;

public static class WorkshopStatDisplay
{
    // Rounds displayed stats to nearest integer
    // The actual damage coefficient is unchanged
    public static float ProtectionPercent(float coefficient) =>
        MathF.Round((1f - coefficient) * 100f, MidpointRounding.AwayFromZero);
}
