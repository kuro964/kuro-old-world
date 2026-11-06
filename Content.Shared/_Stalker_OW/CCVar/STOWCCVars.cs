using Robust.Shared.Configuration;

namespace Content.Shared._Stalker_OW.CCVar;

/// <summary>
/// CVars for ST:OW
/// </summary>
[CVarDefs]
public sealed partial class STOWCCVars
{
    /// <summary>
    ///     Per-player setting: if true, a period is added to the end of your in-character
    ///     messages when they end in a letter. Ported from RMC14's rmc.auto_punctuate.
    ///     Must be REPLICATED | CLIENT so the server can read it with GetClientCVar.
    /// </summary>
    public static readonly CVarDef<bool> AutoPunctuate =
        CVarDef.Create("stalkerow.chat.auto_punctuate", false, CVar.REPLICATED | CVar.CLIENT | CVar.ARCHIVE);
}