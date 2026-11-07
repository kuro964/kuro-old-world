namespace Content.Shared._Stalker_OW.Stacks;

// Raised when item(s) are moved from one stack to another
[ByRefEvent]
public readonly record struct StackMergedEvent(EntityUid Donor, int Transferred);
