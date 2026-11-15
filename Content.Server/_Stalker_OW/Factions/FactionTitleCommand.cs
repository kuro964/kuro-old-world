using System.Linq;
using Content.Server.Administration;
using Content.Shared._Stalker.Bands;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Server._Stalker_OW.FactionTitles;

/// <summary>
/// Admin override for faction titles
/// Titles set by admins are locked so faction leaders cannot change them until an admin unlocks or clears it
/// </summary>
[AdminCommand(AdminFlags.Admin)]
public sealed class STFactionTitleCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPlayerLocator _locator = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    public string Command => "stow_factiontitle";

    public string Description => "View or override faction titles.";

    public string Help => "Usage:\n" +
                          "stow_factiontitle set <username> <faction> <title...>  - set and lock (leaders can't change it)\n" +
                          "stow_factiontitle clear <username> <faction>           - remove title and lock\n" +
                          "stow_factiontitle lock <username> <faction>            - lock the current title\n" +
                          "stow_factiontitle unlock <username> <faction>          - let leaders change it again\n" +
                          "stow_factiontitle list [faction]\n" +
                          "<faction> is an stBand prototype ID, e.g. STDolgBand.";

    public async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var titles = _entityManager.System<STFactionTitleSystem>();

        if (args.Length >= 1 && args[0] == "list")
        {
            var band = args.Length >= 2 ? args[1] : null;
            var any = false;
            foreach (var (userId, bandId, entry) in titles.GetTitles(band).OrderBy(t => t.BandId))
            {
                any = true;
                var name = _playerManager.TryGetSessionById(new NetUserId(userId), out var session)
                    ? session.Name
                    : userId.ToString();
                var title = entry.Title.Length == 0 ? "(none)" : $"'{entry.Title}'";
                shell.WriteLine($"{bandId}: {name} {title}{(entry.AdminLocked ? " [locked]" : string.Empty)}");
            }

            if (!any)
                shell.WriteLine("No faction titles set.");
            return;
        }

        if (args.Length < 3)
        {
            shell.WriteLine(Help);
            return;
        }

        var action = args[0];
        var bandArg = args[2];
        if (!_prototypeManager.HasIndex<STBandPrototype>(bandArg))
        {
            shell.WriteError($"Unknown faction '{bandArg}'.");
            return;
        }

        var located = await _locator.LookupIdByNameOrIdAsync(args[1]);
        if (located == null)
        {
            shell.WriteError($"Unable to find a player named '{args[1]}'.");
            return;
        }

        var target = located.UserId.UserId;
        var adminId = shell.Player?.UserId.UserId;
        var adminName = shell.Player?.Name ?? "console";

        if (!titles.IsReady)
        {
            shell.WriteError("Faction titles haven't finished loading from the database. Try again shortly.");
            return;
        }

        STFactionTitleResult result;
        switch (action)
        {
            case "set":
                if (args.Length < 4)
                {
                    shell.WriteLine(Help);
                    return;
                }

                result = await titles.SetTitleAsAdminAsync(target, bandArg, string.Join(' ', args.Skip(3)), true, adminId, adminName);
                break;
            case "clear":
                result = await titles.ClearAsync(target, bandArg, adminName);
                break;
            case "lock":
                result = await titles.SetLockedAsync(target, bandArg, true, adminId, adminName);
                break;
            case "unlock":
                result = await titles.SetLockedAsync(target, bandArg, false, adminId, adminName);
                break;
            default:
                shell.WriteLine(Help);
                return;
        }

        if (result is STFactionTitleResult.Failed or STFactionTitleResult.NotReady)
        {
            shell.WriteError(Loc.GetString(STFactionTitleSystem.GetResultLocId(result)));
            return;
        }

        var current = titles.TryGetTitle(target, bandArg, out var saved)
            ? $"'{saved.Title}'{(saved.AdminLocked ? " (locked)" : string.Empty)}"
            : "no title";
        shell.WriteLine($"{Loc.GetString(STFactionTitleSystem.GetResultLocId(result))} {located.Username} in {bandArg}: {current}.");
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromHintOptions(new[] { "set", "clear", "lock", "unlock", "list" }, "action"),
            2 when args[0] == "list" => CompletionResult.FromHintOptions(
                CompletionHelper.PrototypeIDs<STBandPrototype>(proto: _prototypeManager), "faction"),
            2 => CompletionResult.FromHintOptions(CompletionHelper.SessionNames(players: _playerManager), "username"),
            3 => CompletionResult.FromHintOptions(
                CompletionHelper.PrototypeIDs<STBandPrototype>(proto: _prototypeManager), "faction"),
            4 when args[0] == "set" => CompletionResult.FromHint("title"),
            _ => CompletionResult.Empty,
        };
    }
}
