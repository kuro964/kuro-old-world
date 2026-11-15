using System.Linq;
using Content.Server._Stalker_OW.BrainScorcher.BrainScorcherComponents;
using Content.Server._Stalker_EN.MiracleMachine.MiracleMachineComponents;
using Content.Server.Chat.Systems;
using Content.Server.Radio.EntitySystems;
using Content.Shared.Destructible;
using Content.Shared.GameTicking;
using Content.Shared.Radio.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Physics;
using Robust.Shared.Timing;

namespace Content.Server._Stalker_OW.BrainScorcher;

/// <summary>
/// Handles various systems related to the Scorchmeister such as Generators, teleports, psy fields, etc.
/// </summary>
public sealed class BrainScorcherSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timingSystem = default!;
    [Dependency] private readonly ChatSystem _chatSystem = default!;
    [Dependency] private readonly MapSystem _mapSystem = default!;
    [Dependency] private readonly RadioSystem _radioSystem = default!;
    private TimeSpan _brainScorcherDisabledTime  = TimeSpan.Zero;

    private bool _brainScorcherAnnounced = false;

    private bool _brainScorcherDisabled = false;

    private SoundSpecifier _soundSpecifier = new SoundPathSpecifier("/Audio/Announcements/announce.ogg");
    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<BrainScorcherComponent, ComponentInit>(OnStartup);
        SubscribeLocalEvent<BrainScorcherGeneratorComponent, DestructionAttemptEvent>(OnGeneratorDestroyed);
    }

    public override void Update(float frameTime)
    {
        if (_brainScorcherAnnounced)
            return;

        if (_brainScorcherDisabledTime  == TimeSpan.Zero)
            return;

        if (_brainScorcherDisabledTime  <= _timingSystem.CurTime)
        {
            _brainScorcherAnnounced = true;
            _chatSystem.DispatchGlobalAnnouncement("Bratya! They have weakened His voice! The infidels can enter His lands once more.", "Eidolon", true, _soundSpecifier, Color.AntiqueWhite);
        }

    }

    private void OnStartup(EntityUid uid, BrainScorcherComponent comp, ComponentInit args)
    {
        var query = EntityQueryEnumerator<BrainScorcherGeneratorComponent>();
        while (query.MoveNext(out var generator, out var _))
        {
            if (!comp.Generators.Contains(generator))
                comp.Generators.Add(generator);
        }
    }

    private void OnGeneratorDestroyed(EntityUid uid, BrainScorcherGeneratorComponent comp, DestructionAttemptEvent args)
    {
        var query = EntityQueryEnumerator<BrainScorcherComponent>();
        while (query.MoveNext(out var _, out var scorcher))
        {
            if (scorcher.Generators.Contains(uid))
            {
                scorcher.Generators.Remove(uid);
                if (TryComp<IntrinsicRadioTransmitterComponent>(uid, out var radio))
                {
                    _radioSystem.SendRadioMessage(uid, "His Voice is Weakened!", radio.Channels.FirstOrDefault(), uid);
                }
            }


            if (scorcher.Generators.Count <= 0 && !_brainScorcherDisabled)
            {
                _brainScorcherDisabled = true;
                BrainScorcherDisabled(scorcher);
                break;
            }
        }
    }

    /// <summary>
    /// Called when the BrainScorcher is disabled. Deletes all Miracle Machine specific psy fields, psy sources, and spawns new teleports to different areas
    /// </summary>
    private void BrainScorcherDisabled(BrainScorcherComponent comp)
    {
        // Needed just in case maps are paused and stuff doesn't get deleted. Hopefully works
        foreach (var mapId in _mapSystem.GetAllMapIds())
        {
            _mapSystem.SetPaused(mapId, false);
        }
        var query = EntityQueryEnumerator<BrainScorcherSpawnerComponent>();
        while (query.MoveNext(out var uid, out var spawner))
        {
            if(!spawner.BrainScorcher)
                continue;

            spawner.Inside.Clear();
            QueueDel(uid);
        }

        var query2 = EntityQueryEnumerator<BrainScorcherTeleportsComponent>();
        while (query2.MoveNext(out var uid2, out var teleporters))
        {
            Spawn(teleporters.Teleport, Transform(uid2).Coordinates);
            QueueDel(uid2);
        }

        var query3 = EntityQueryEnumerator<BrainScorcherPsySourceComponent>();
        while (query3.MoveNext(out var uid3, out var psySource))
        {
            QueueDel(uid3);
        }
        _brainScorcherDisabledTime  = _timingSystem.CurTime + TimeSpan.FromMinutes(1);
    }
}
