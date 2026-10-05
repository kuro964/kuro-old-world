namespace Content.Shared.Speech;

public sealed class AccentGetEvent : EntityEventArgs
{
    /// <summary>
    ///     The entity to apply the accent to.
    /// </summary>
    public EntityUid Entity { get; }

    /// <summary>
    ///     The message to apply the accent transformation to.
    ///     Modify this to apply the accent.
    /// </summary>
    public string Message { get; set; }

    /// <summary>
    /// STOW
    /// Used to make certain accents (i.e. Russian) readable when lowercase & italicized
    /// </summary>
    public bool IsWhisper { get; }

    public AccentGetEvent(EntityUid entity, string message, bool isWhisper = false)
    {
        Entity = entity;
        Message = message;
        IsWhisper = isWhisper;
    }
}
