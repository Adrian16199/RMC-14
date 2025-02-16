﻿using System.Runtime.InteropServices;
using Content.Server._RMC14.Dropship;
using Content.Server._RMC14.Marines;
using Content.Server.Administration.Components;
using Content.Server.Administration.Managers;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules;
using Content.Server.Mind;
using Content.Server.Players.PlayTimeTracking;
using Content.Server.Power.Components;
using Content.Server.Preferences.Managers;
using Content.Server.RoundEnd;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Spawners.Components;
using Content.Server.Spawners.EntitySystems;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._RMC14.CCVar;
using Content.Shared._RMC14.Dropship;
using Content.Shared._RMC14.Marines;
using Content.Shared._RMC14.Marines.HyperSleep;
using Content.Shared._RMC14.Marines.Squads;
using Content.Shared._RMC14.Spawners;
using Content.Shared._RMC14.Weapons.Ranged.IFF;
using Content.Shared._RMC14.Xenonids;
using Content.Shared._RMC14.Xenonids.Construction.Nest;
using Content.Shared._RMC14.Xenonids.Evolution;
using Content.Shared._RMC14.Xenonids.Parasite;
using Content.Shared.CCVar;
using Content.Shared.Coordinates;
using Content.Shared.GameTicking;
using Content.Shared.GameTicking.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Server.Audio;
using Robust.Server.Containers;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Audio;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Server._RMC14.Rules;

public sealed class CMDistressSignalRuleSystem : GameRuleSystem<CMDistressSignalRuleComponent>
{
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly IBanManager _bans = default!;
    [Dependency] private readonly IComponentFactory _compFactory = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly ContainerSystem _containers = default!;
    [Dependency] private readonly DropshipSystem _dropship = default!;
    [Dependency] private readonly GunIFFSystem _gunIFF = default!;
    [Dependency] private readonly HungerSystem _hunger = default!;
    [Dependency] private readonly MarineSystem _marines = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly MapSystem _mapSystem = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly PlayTimeTrackingSystem _playTime = default!;
    [Dependency] private readonly IServerPreferencesManager _prefsManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly RoundEndSystem _roundEnd = default!;
    [Dependency] private readonly StationSpawningSystem _stationSpawning = default!;
    [Dependency] private readonly SquadSystem _squad = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly XenoSystem _xeno = default!;
    [Dependency] private readonly XenoEvolutionSystem _xenoEvolution = default!;

    private readonly CVarDef<float>[] _ftlcVars =
    [
        CCVars.FTLStartupTime,
        CCVars.FTLTravelTime,
        CCVars.FTLArrivalTime,
        CCVars.FTLCooldown,
    ];

    private readonly List<MapId> _almayerMaps = [];
    private float _marinesPerXeno;

    private EntityQuery<XenoNestedComponent> _xenoNestedQuery;

    public override void Initialize()
    {
        base.Initialize();

        _xenoNestedQuery = GetEntityQuery<XenoNestedComponent>();

        SubscribeLocalEvent<RulePlayerSpawningEvent>(OnRulePlayerSpawning);
        SubscribeLocalEvent<PlayerSpawningEvent>(OnPlayerSpawning,
            before: [typeof(ArrivalsSystem), typeof(SpawnPointSystem)]);
        SubscribeLocalEvent<RoundEndMessageEvent>(OnRoundEndMessage);

        SubscribeLocalEvent<MarineComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<MarineComponent, ComponentRemove>(OnCompRemove);

        SubscribeLocalEvent<XenoComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<XenoComponent, ComponentRemove>(OnCompRemove);

        SubscribeLocalEvent<XenoEvolutionGranterComponent, MapInitEvent>(OnMapInit);

        SubscribeLocalEvent<AlmayerComponent, MapInitEvent>(OnAlmayerMapInit);

        Subs.CVar(_config, CMCVars.CMMarinesPerXeno, v => _marinesPerXeno = v, true);
    }

    private void OnRulePlayerSpawning(RulePlayerSpawningEvent ev)
    {
        var spawnedDropships = false;
        var query = QueryActiveRules();
        while (query.MoveNext(out var uid, out _, out var comp, out var gameRule))
        {
            if (!GameTicker.IsGameRuleAdded(uid, gameRule))
                continue;

            comp.Hive = Spawn(comp.HiveId);
            if (!SpawnXenoMap((uid, comp)))
            {
                Log.Error("Failed to load xeno map");
                continue;
            }

            var xenoSpawnPoints = new List<EntityUid>();
            var spawnQuery = AllEntityQuery<XenoSpawnPointComponent>();
            while (spawnQuery.MoveNext(out var spawnUid, out _))
            {
                xenoSpawnPoints.Add(spawnUid);
            }

            var xenoLeaderSpawnPoints = new List<EntityUid>();
            var leaderSpawnQuery = AllEntityQuery<XenoLeaderSpawnPointComponent>();
            while (leaderSpawnQuery.MoveNext(out var spawnUid, out _))
            {
                xenoLeaderSpawnPoints.Add(spawnUid);
            }

            bool IsAllowed(NetUserId id, ProtoId<JobPrototype> role)
            {
                if (!_player.TryGetSessionById(id, out var player))
                    return false;

                var jobBans = _bans.GetJobBans(player.UserId);
                if (jobBans == null || jobBans.Contains(role))
                    return false;

                if (!_playTime.IsAllowed(player, role))
                    return false;

                return true;
            }

            NetUserId? SpawnXeno(List<NetUserId> list, EntProtoId ent)
            {
                var playerId = _random.PickAndTake(list);
                if (!_player.TryGetSessionById(playerId, out var player))
                {
                    Log.Error($"Failed to find player with id {playerId} during xeno selection.");
                    return null;
                }

                ev.PlayerPool.Remove(player);
                GameTicker.PlayerJoinGame(player);

                var leader = _prototypes.TryIndex(ent, out var proto) &&
                             proto.TryGetComponent(out XenoComponent? xeno, _compFactory) &&
                             xeno.SpawnAtLeaderPoint;

                var point = _random.Pick(leader ? xenoLeaderSpawnPoints : xenoSpawnPoints);
                var xenoEnt = SpawnAtPosition(ent, point.ToCoordinates());

                _xeno.MakeXeno(xenoEnt);
                _xeno.SetHive(xenoEnt, comp.Hive);

                if (!_mind.TryGetMind(playerId, out var mind))
                    mind = _mind.CreateMind(playerId);

                _mind.TransferTo(mind.Value, xenoEnt);
                return playerId;
            }

            var totalXenos = Math.Max(1, ev.PlayerPool.Count / _marinesPerXeno);
            var xenoCandidates = new List<NetUserId>[Enum.GetValues<JobPriority>().Length];
            for (var i = 0; i < xenoCandidates.Length; i++)
            {
                xenoCandidates[i] = [];
            }

            foreach (var (id, profile) in ev.Profiles)
            {
                if (!IsAllowed(id, comp.QueenJob))
                    continue;

                if (profile.JobPriorities.TryGetValue(comp.QueenJob, out var priority) &&
                    priority > JobPriority.Never)
                {
                    xenoCandidates[(int) priority].Add(id);
                }
            }

            NetUserId? queenSelected = null;
            for (var i = xenoCandidates.Length - 1; i >= 0; i--)
            {
                var list = xenoCandidates[i];
                while (list.Count > 0)
                {
                    queenSelected = SpawnXeno(list, comp.QueenEnt);
                    if (queenSelected != null)
                        break;
                }

                if (queenSelected != null)
                {
                    totalXenos--;
                    break;
                }
            }

            foreach (var list in xenoCandidates)
            {
                list.Clear();
            }

            foreach (var (id, profile) in ev.Profiles)
            {
                if (id == queenSelected)
                    continue;

                if (!IsAllowed(id, comp.XenoSelectableJob))
                    continue;

                if (profile.JobPriorities.TryGetValue(comp.XenoSelectableJob, out var priority) &&
                    priority > JobPriority.Never)
                {
                    xenoCandidates[(int) priority].Add(id);
                }
            }

            var selected = 0;
            for (var i = xenoCandidates.Length - 1; i >= 0; i--)
            {
                var list = xenoCandidates[i];
                while (list.Count > 0 && selected < totalXenos)
                {
                    if (queenSelected == null)
                    {
                        queenSelected = SpawnXeno(list, comp.QueenEnt);
                        if (queenSelected != null)
                        {
                            totalXenos--;
                            selected++;
                        }
                    }
                    else if (SpawnXeno(list, comp.LarvaEnt) != null)
                    {
                        selected++;
                    }
                }
            }

            // Any unfilled xeno slots become larva
            for (var i = selected; i < totalXenos; i++)
            {
                // TODO RMC14 xeno spawn points
                var xenoEnt = SpawnAtPosition(comp.LarvaEnt, comp.XenoMap.ToCoordinates());
                _xeno.MakeXeno(xenoEnt);
                _xeno.SetHive(xenoEnt, comp.Hive);
            }

            if (spawnedDropships)
                return;

            foreach (var cvar in _ftlcVars)
            {
                comp.OriginalCVarValues[cvar] = _config.GetCVar(cvar);
                _config.SetCVar(cvar, 1);
            }

            // don't open shitcode inside
            spawnedDropships = true;
            var dropshipMap = _mapManager.CreateMap();
            var dropshipPoints = EntityQueryEnumerator<DropshipDestinationComponent, MetaDataComponent, TransformComponent>();
            var ships = new[] { "/Maps/_RMC14/alamo.yml", "/Maps/_RMC14/normandy.yml" };
            var shipIndex = 0;
            while (dropshipPoints.MoveNext(out var destinationId, out _, out var metaData, out var destTransform))
            {
                if (_mapSystem.TryGetMap(destTransform.MapID, out var destinationMapId) &&
                    comp.XenoMap == destinationMapId)
                {
                    continue;
                }

                _mapLoader.TryLoad(dropshipMap, ships[shipIndex], out var shipGrids);
                shipIndex++;

                if (shipIndex >= ships.Length)
                    shipIndex = 0;

                if (shipGrids == null)
                    continue;

                foreach (var shipGrid in shipGrids)
                {
                    var computers = EntityQueryEnumerator<DropshipNavigationComputerComponent, TransformComponent>();
                    var launched = false;
                    while (computers.MoveNext(out var computerId, out var computer, out var xform))
                    {
                        if (xform.GridUid != shipGrid)
                            continue;

                        if (!_dropship.FlyTo((computerId, computer), destinationId, null))
                            continue;

                        launched = true;
                        break;
                    }

                    if (launched)
                        break;
                }
            }
        }
    }

    private void OnPlayerSpawning(PlayerSpawningEvent ev)
    {
        if (ev.Job?.Prototype is not { } jobId ||
            !_prototypes.TryIndex(jobId, out var job) ||
            !job.IsCM)
        {
            return;
        }

        var query = QueryActiveRules();
        while (query.MoveNext(out _, out _, out var comp, out _))
        {
            if (GetSpawner(comp, job) is not { } spawnerInfo)
                return;

            var (spawner, squad) = spawnerInfo;
            if (TryComp(spawner, out HyperSleepChamberComponent? hyperSleep) &&
                _containers.TryGetContainer(spawner, hyperSleep.ContainerId, out var container))
            {
                ev.SpawnResult = _stationSpawning.SpawnPlayerMob(spawner.ToCoordinates(), ev.Job, ev.HumanoidCharacterProfile, ev.Station);
                _containers.Insert(ev.SpawnResult.Value, container);
            }
            else
            {
                var coordinates = _transform.GetMoverCoordinates(spawner);
                ev.SpawnResult = _stationSpawning.SpawnPlayerMob(coordinates, ev.Job, ev.HumanoidCharacterProfile, ev.Station);
            }

            // TODO RMC14 split this out with an event
            SpriteSpecifier? icon = null;
            if (job.HasIcon && _prototypes.TryIndex(job.Icon, out var jobIcon))
                icon = jobIcon.Icon;

            _marines.MakeMarine(ev.SpawnResult.Value, icon);

            if (squad != null)
            {
                _squad.AssignSquad(ev.SpawnResult.Value, squad.Value, ev.Job?.Prototype);

                // TODO RMC14 add this to the map file
                if (TryComp(spawner, out TransformComponent? xform) &&
                    xform.GridUid != null)
                {
                    EnsureComp<AlmayerComponent>(xform.GridUid.Value);
                }
            }

            if (TryComp(ev.SpawnResult, out HungerComponent? hunger))
                _hunger.SetHunger(ev.SpawnResult.Value, 50.0f, hunger);

            _gunIFF.SetUserFaction(ev.SpawnResult.Value, comp.MarineFaction);
            return;
        }
    }

    private void OnRoundEndMessage(RoundEndMessageEvent ev)
    {
        var rules = QueryActiveRules();
        while (rules.MoveNext(out _, out var distress, out _))
        {
            if (distress.Result == DistressSignalRuleResult.None)
                continue;

            SoundSpecifier? audio = distress.Result switch
            {
                DistressSignalRuleResult.None => null,
                // TODO RMC14
                // DistressSignalRuleResult.MajorMarineVictory => distress.MajorMarineAudio,
                // DistressSignalRuleResult.MinorMarineVictory => distress.MinorMarineAudio,
                // DistressSignalRuleResult.MajorXenoVictory => distress.MajorXenoAudio,
                // DistressSignalRuleResult.MinorXenoVictory => distress.MinorXenoAudio,
                // DistressSignalRuleResult.AllDied => distress.AllDiedAudio,
                _ => null
            };

            if (audio != null)
                _audio.PlayGlobal(_audio.GetSound(audio), Filter.Broadcast(), true, AudioParams.Default.WithVolume(0));
        }
    }

    private void OnMobStateChanged<T>(Entity<T> ent, ref MobStateChangedEvent args) where T : IComponent?
    {
        if (args.NewMobState == MobState.Dead)
            CheckRoundShouldEnd();
    }

    private void OnCompRemove<T>(Entity<T> ent, ref ComponentRemove args) where T : IComponent?
    {
        CheckRoundShouldEnd();
    }

    private void OnMapInit(Entity<XenoEvolutionGranterComponent> ent, ref MapInitEvent args)
    {
        CheckRoundShouldEnd();
    }

    private void OnAlmayerMapInit(Entity<AlmayerComponent> almayer, ref MapInitEvent args)
    {
        GridInfinitePower(almayer);
    }

    protected override void OnStartAttempt(Entity<CMDistressSignalRuleComponent, GameRuleComponent> gameRule, RoundStartAttemptEvent ev)
    {
        if (ev.Forced || ev.Cancelled)
            return;

        var query = QueryAllRules();
        while (query.MoveNext(out _, out var distress, out _))
        {
            var xenoCandidate = false;
            foreach (var player in ev.Players)
            {
                if (_prefsManager.TryGetCachedPreferences(player.UserId, out var preferences))
                {
                    var profile = (HumanoidCharacterProfile) preferences.GetProfile(preferences.SelectedCharacterIndex);
                    if (profile.JobPriorities.TryGetValue(distress.XenoSelectableJob, out var xenoPriority) &&
                        xenoPriority > JobPriority.Never)
                    {
                        xenoCandidate = true;
                        break;
                    }

                    if (profile.JobPriorities.TryGetValue(distress.QueenJob, out var queenPriority) &&
                        queenPriority > JobPriority.Never)
                    {
                        xenoCandidate = true;
                        break;
                    }
                }
            }

            if (xenoCandidate)
                continue;

            ChatManager.SendAdminAnnouncement("Can't start distress signal. Requires at least 1 xeno player but we have 0.");
            ev.Cancel();
        }
    }

    private void CheckRoundShouldEnd()
    {
        var query = QueryActiveRules();
        while (query.MoveNext(out var uid, out _, out var distress, out var gameRule))
        {
            if (!GameTicker.IsGameRuleAdded(uid, gameRule))
                continue;

            distress.NextCheck ??= Timing.CurTime + distress.CheckEvery;

            var dropshipQuery = EntityQueryEnumerator<DropshipComponent>();
            while (dropshipQuery.MoveNext(out var dropship))
            {
                if (dropship.Crashed)
                    distress.Hijack = true;
            }

            var time = Timing.CurTime;
            if (distress.Hijack)
                distress.AbandonedAt ??= time + distress.AbandonedDelay;

            _almayerMaps.Clear();
            var almayerQuery = EntityQueryEnumerator<AlmayerComponent, TransformComponent>();
            while (almayerQuery.MoveNext(out _, out var xform))
            {
                _almayerMaps.Add(xform.MapID);
            }

            var xenosAlive = false;
            var xenos = EntityQueryEnumerator<ActorComponent, XenoComponent, MobStateComponent, TransformComponent>();
            while (xenos.MoveNext(out var xenoId, out _, out var xeno, out var mobState, out var xform))
            {
                if (!xeno.ContributesToVictory)
                    continue;

                if (_mobState.IsAlive(xenoId, mobState) &&
                    (distress.AbandonedAt == null ||
                     time < distress.AbandonedAt ||
                     !distress.Hijack ||
                     _almayerMaps.Contains(xform.MapID)))
                {
                    xenosAlive = true;
                }

                if (xenosAlive)
                    break;
            }

            var marines = EntityQueryEnumerator<ActorComponent, MarineComponent, MobStateComponent, TransformComponent>();
            var marinesAlive = false;
            while (marines.MoveNext(out var marineId, out _, out _, out var mobState, out var xform))
            {
                if (HasComp<VictimInfectedComponent>(marineId) ||
                    HasComp<VictimBurstComponent>(marineId) ||
                    _xenoNestedQuery.HasComp(marineId))
                {
                    continue;
                }

                if (_mobState.IsAlive(marineId, mobState) &&
                    (distress.AbandonedAt == null ||
                     time < distress.AbandonedAt ||
                     !distress.Hijack ||
                     _almayerMaps.Contains(xform.MapID)))
                {
                    marinesAlive = true;
                }

                if (marinesAlive)
                    break;
            }

            if (xenosAlive && !marinesAlive)
            {
                distress.Result = DistressSignalRuleResult.MajorXenoVictory;
                _roundEnd.EndRound();
                continue;
            }

            if (!xenosAlive && marinesAlive)
            {
                // TODO RMC14 this should be when the dropship crashes, not if xenos ever boarded
                if (distress.Hijack)
                {
                    distress.Result = DistressSignalRuleResult.MinorXenoVictory;
                    _roundEnd.EndRound();
                    continue;
                }
                else
                {
                    distress.Result = DistressSignalRuleResult.MajorMarineVictory;
                    _roundEnd.EndRound();
                    continue;
                }
            }

            if (!xenosAlive && !marinesAlive)
            {
                distress.Result = DistressSignalRuleResult.AllDied;
                _roundEnd.EndRound();
                continue;
            }

            if (_xenoEvolution.HasLiving<XenoEvolutionGranterComponent>(1))
            {
                distress.QueenDiedCheck = null;
                continue;
            }
            else
            {
                distress.QueenDiedCheck ??= Timing.CurTime + distress.QueenDiedDelay;
            }

            if (distress.QueenDiedCheck == null)
                continue;

            if (Timing.CurTime >= distress.QueenDiedCheck)
            {
                if (_xenoEvolution.HasLiving<XenoComponent>(4))
                {
                    distress.Result = DistressSignalRuleResult.MinorMarineVictory;
                    _roundEnd.EndRound();
                }
                else
                {
                    distress.Result = DistressSignalRuleResult.MajorMarineVictory;
                    _roundEnd.EndRound();
                }
            }
        }
    }

    private bool SpawnXenoMap(Entity<CMDistressSignalRuleComponent> rule)
    {
        // TODO RMC14 different planet-side maps
        var mapId = _mapManager.CreateMap();
        if (!_mapLoader.TryLoad(mapId, "/Maps/_RMC14/lv624.yml", out var grids) ||
            grids.Count == 0)
        {
            return false;
        }

        if (grids.Count > 1)
            Log.Error("Multiple planet-side grids found");

        rule.Comp.XenoMap = grids[0];

        _mapManager.SetMapPaused(mapId, false);
        return true;
    }

    private Spawners GetSpawners()
    {
        var spawners = new Spawners();
        var squadQuery = EntityQueryEnumerator<SquadSpawnerComponent>();
        while (squadQuery.MoveNext(out var uid, out var spawner))
        {
            if (TryComp(uid, out HyperSleepChamberComponent? hyperSleep) &&
                _containers.TryGetContainer(uid, hyperSleep.ContainerId, out var container) &&
                container.Count > 0)
            {
                if (spawner.Role == null)
                    spawners.SquadAnyFull.GetOrNew(spawner.Squad).Add(uid);
                else
                    spawners.SquadFull.GetOrNew(spawner.Squad).GetOrNew(spawner.Role.Value).Add(uid);
            }
            else
            {
                if (spawner.Role == null)
                    spawners.SquadAny.GetOrNew(spawner.Squad).Add(uid);
                else
                    spawners.Squad.GetOrNew(spawner.Squad).GetOrNew(spawner.Role.Value).Add(uid);
            }
        }

        var nonSquadQuery = EntityQueryEnumerator<SpawnPointComponent>();
        while (nonSquadQuery.MoveNext(out var uid, out var spawner))
        {
            if (spawner.Job == null)
                continue;

            if (TryComp(uid, out HyperSleepChamberComponent? hyperSleep) &&
                _containers.TryGetContainer(uid, hyperSleep.ContainerId, out var container) &&
                container.Count > 0)
            {
                spawners.NonSquadFull.GetOrNew(spawner.Job.Value).Add(uid);
            }
            else
            {
                spawners.NonSquad.GetOrNew(spawner.Job.Value).Add(uid);
            }
        }

        return spawners;
    }

    private (EntProtoId Id, EntityUid Ent) NextSquad(ProtoId<JobPrototype> job, CMDistressSignalRuleComponent rule)
    {
        // TODO RMC14 this biases people towards alpha as that's the first one, maybe not a problem once people can pick a preferred squad?
        if (!rule.NextSquad.TryGetValue(job, out var next) ||
            next >= rule.SquadIds.Count)
        {
            rule.NextSquad[job] = 0;
            next = 0;
        }

        var id = rule.SquadIds[next++];
        rule.NextSquad[job] = next;

        ref var squad = ref CollectionsMarshal.GetValueRefOrAddDefault(rule.Squads, id, out var exists);
        if (!exists)
            squad = Spawn(id);

        return (id, squad);
    }

    private (EntityUid Spawner, EntityUid? Squad)? GetSpawner(CMDistressSignalRuleComponent rule, JobPrototype job)
    {
        var allSpawners = GetSpawners();
        EntityUid? squad = null;

        if (job.HasSquad)
        {
            var (squadId, squadEnt) = NextSquad(job, rule);
            squad = squadEnt;

            if (allSpawners.Squad.TryGetValue(squadId, out var jobSpawners) &&
                jobSpawners.TryGetValue(job.ID, out var spawners))
            {
                return (_random.Pick(spawners), squadEnt);
            }

            if (allSpawners.SquadAny.TryGetValue(squadId, out var anySpawners))
                return (_random.Pick(anySpawners), squadEnt);

            if (allSpawners.SquadFull.TryGetValue(squadId, out jobSpawners) &&
                jobSpawners.TryGetValue(job.ID, out spawners))
            {
                return (_random.Pick(spawners), squadEnt);
            }

            if (allSpawners.SquadAnyFull.TryGetValue(squadId, out anySpawners))
                return (_random.Pick(anySpawners), squadEnt);

            Log.Error($"No valid spawn found for player. Squad: {squadId}, job: {job.ID}");

            if (allSpawners.NonSquad.TryGetValue(job.ID, out spawners))
                return (_random.Pick(spawners), squadEnt);

            if (allSpawners.NonSquadFull.TryGetValue(job.ID, out spawners))
                return (_random.Pick(spawners), squadEnt);

            Log.Error($"No valid spawn found for player. Job: {job.ID}");
        }
        else
        {
            if (allSpawners.NonSquad.TryGetValue(job.ID, out var spawners))
                return (_random.Pick(spawners), null);

            if (allSpawners.NonSquadFull.TryGetValue(job.ID, out spawners))
                return (_random.Pick(spawners), null);

            Log.Error($"No valid spawn found for player. Job: {job.ID}");
        }

        var pointsQuery = EntityQueryEnumerator<SpawnPointComponent>();
        var jobPoints = new List<EntityUid>();
        var anyJobPoints = new List<EntityUid>();
        var latePoints = new List<EntityUid>();

        while (pointsQuery.MoveNext(out var uid, out var point))
        {
            if (point.SpawnType == SpawnPointType.Job)
            {
                if (point.Job?.Id == job.ID)
                    jobPoints.Add(uid);
                else
                    anyJobPoints.Add(uid);
            }

            if (point.SpawnType == SpawnPointType.LateJoin)
                latePoints.Add(uid);
        }

        if (jobPoints.Count > 0)
            return (_random.Pick(jobPoints), squad);

        if (anyJobPoints.Count > 0)
            return (_random.Pick(anyJobPoints), squad);

        if (latePoints.Count > 0)
            return (_random.Pick(latePoints), squad);

        return null;
    }

    protected override void AppendRoundEndText(EntityUid uid,
        CMDistressSignalRuleComponent component,
        GameRuleComponent gameRule,
        ref RoundEndTextAppendEvent args)
    {
        base.AppendRoundEndText(uid, component, gameRule, ref args);
        args.AddLine($"{Loc.GetString($"cm-distress-signal-{component.Result.ToString().ToLower()}")}");
    }

    protected override void ActiveTick(EntityUid uid, CMDistressSignalRuleComponent component, GameRuleComponent gameRule, float frameTime)
    {
        base.ActiveTick(uid, component, gameRule, frameTime);

        if (!component.ResetCVars)
        {
            var anyDropships = false;
            var dropships = EntityQueryEnumerator<DropshipComponent, FTLComponent>();
            while (dropships.MoveNext(out _, out _))
            {
                anyDropships = true;
            }

            if (!anyDropships)
            {
                foreach (var (cvar, value) in component.OriginalCVarValues)
                {
                    _config.SetCVar(cvar, value);
                }

                component.ResetCVars = true;
            }
        }

        if (Timing.CurTime >= component.NextCheck)
        {
            component.NextCheck = Timing.CurTime + component.CheckEvery;
            CheckRoundShouldEnd();
        }

        if (_xenoEvolution.HasLiving<XenoEvolutionGranterComponent>(1))
            component.QueenDiedCheck = null;

        if (component.QueenDiedCheck == null)
            return;

        if (Timing.CurTime >= component.QueenDiedCheck)
        {
            if (_xenoEvolution.HasLiving<XenoComponent>(4))
            {
                component.Result = DistressSignalRuleResult.MinorMarineVictory;
                _roundEnd.EndRound();
            }
            else
            {
                component.Result = DistressSignalRuleResult.MajorMarineVictory;
                _roundEnd.EndRound();
            }
        }
    }

    public void GridInfinitePower(EntityUid grid)
    {
        foreach (var ent in GetChildren(grid))
        {
            if (TryComp(ent, out ApcPowerReceiverComponent? receiver))
                receiver.NeedsPower = false;

            if (!HasComp<StationInfiniteBatteryTargetComponent>(ent))
                continue;

            var recharger = EnsureComp<BatterySelfRechargerComponent>(ent);
            var battery = EnsureComp<BatteryComponent>(ent);

            recharger.AutoRecharge = true;
            recharger.AutoRechargeRate = battery.MaxCharge; // Instant refill.
        }
    }

    private IEnumerable<EntityUid> GetChildren(EntityUid almayer)
    {
        if (TryComp<StationDataComponent>(almayer, out var station))
        {
            foreach (var grid in station.Grids)
            {
                var enumerator = Transform(grid).ChildEnumerator;
                while (enumerator.MoveNext(out var ent))
                {
                    yield return ent;
                }
            }
        }
        else if (HasComp<MapComponent>(almayer))
        {
            var enumerator = Transform(almayer).ChildEnumerator;
            while (enumerator.MoveNext(out var possibleGrid))
            {
                var enumerator2 = Transform(possibleGrid).ChildEnumerator;
                while (enumerator2.MoveNext(out var ent))
                {
                    yield return ent;
                }
            }
        }
        else
        {
            var enumerator = Transform(almayer).ChildEnumerator;
            while (enumerator.MoveNext(out var ent))
            {
                yield return ent;
            }
        }
    }
}

public sealed class Spawners
{
    public readonly Dictionary<EntProtoId, Dictionary<ProtoId<JobPrototype>, List<EntityUid>>> Squad = new();
    public readonly Dictionary<EntProtoId, List<EntityUid>> SquadAny = new();
    public readonly Dictionary<EntProtoId, Dictionary<ProtoId<JobPrototype>, List<EntityUid>>> SquadFull = new();
    public readonly Dictionary<EntProtoId, List<EntityUid>> SquadAnyFull = new();
    public readonly Dictionary<ProtoId<JobPrototype>, List<EntityUid>> NonSquad = new();
    public readonly Dictionary<ProtoId<JobPrototype>, List<EntityUid>> NonSquadFull = new();
}
