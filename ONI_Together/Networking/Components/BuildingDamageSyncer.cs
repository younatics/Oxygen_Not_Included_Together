using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
    /// <summary>
    /// The single owner of building damage replication.
    ///
    /// Damage used to be a passenger on StructureStatePacket, which covered only
    /// buildings that have a StructureSyncerBase and only when that syncer's
    /// other state changed. A Tile has no syncer, so tile damage was never
    /// replicated at all; the storage, battery and reactor syncers skip their
    /// optional values when deciding whether anything changed, so their hit
    /// points moved without ever triggering a send.
    ///
    /// This runs on the host for every BuildingHP in the world, one sweep, and
    /// sends only what actually moved. It is intentionally not culled by
    /// viewport: a wall that cracks while nobody is looking is still cracked,
    /// and unlike a position there is nothing periodic to correct it later.
    /// </summary>
    public class BuildingDamageSyncer : MonoBehaviour
    {
        public static BuildingDamageSyncer Instance { get; private set; }

        /// <summary>
        /// Whole-world sweep interval. Damage is rare and the packet is eight
        /// bytes, so this is cheap; the cost that matters is the FindObjectsByType,
        /// which is why it is one sweep for every building rather than a component
        /// on each.
        /// </summary>
        private const float SweepInterval = 2f;

        /// <summary>
        /// Ceiling on packets per sweep.
        ///
        /// This was 400, chosen so a post-join re-assert of every building
        /// finished quickly, and the run that used it had the client drop and
        /// rejoin mid-session - the host logged the join twice and the client
        /// never got far enough to run its own checks. Four hundred reliable
        /// packets in a frame is a lot to ask of a UDP transport, and no
        /// correctness argument justifies it, because most of those packets say
        /// nothing: both peers loaded the same save, so a building at full
        /// health already agrees.
        ///
        /// Fifty spreads a full pass over about two and a half minutes, and the
        /// buildings that actually disagree are sent first, so the visible cases
        /// are fixed in the first sweep or two either way.
        /// </summary>
        private const int MaxSendsPerSweep = 50;

        private float _nextSweep;

        /// <summary>Last hit points we told clients about, by NetId.</summary>
        private readonly Dictionary<int, int> _lastSent = new Dictionary<int, int>();

        /// <summary>Buildings whose damage differed from what clients were told, this sweep.</summary>
        public int LastSweepChanged { get; private set; }
        public int LastSweepScanned { get; private set; }

        /// <summary>Buildings this sweep wanted to send but deferred to the next one.</summary>
        public int LastSweepDeferred { get; private set; }

        private void OnEnable() => Instance = this;

        private void OnDisable()
        {
            if (Instance == this) Instance = null;
        }

        public void Reset()
        {
            _lastSent.Clear();
            LastSweepChanged = 0;
            LastSweepScanned = 0;
            LastSweepDeferred = 0;
            _lastClientCount = 0;
            _lastReadyCount = 0;
            _reassertDamaged = false;
            // So a client that reconnects asks again: it has a fresh world, and
            // the host has had time to repair things while it was away.
            _asked = false;
        }

        /// <summary>Has this client already asked the host about its damaged buildings?</summary>
        private bool _asked;

        public bool HasAsked => _asked;

        private void Update()
        {
            using var _ = Profiler.Scope();

            if (!MultiplayerSession.InSession)
                return;

            if (MultiplayerSession.IsClient)
            {
                AskAboutOwnDamageOnce();
                return;
            }

            if (!MultiplayerSession.IsHost)
                return;

            if (Time.unscaledTime < _nextSweep)
                return;
            _nextSweep = Time.unscaledTime + SweepInterval;

            Sweep();
        }

        /// <summary>
        /// One query, naming the buildings this peer believes are broken.
        ///
        /// The host cannot volunteer this: a building it repaired before the
        /// client joined is at full health, so nothing on its side changed and
        /// nothing gets sent, while the client keeps the damage its save came
        /// with. That was the last measured disagreement - one tile at 68 of 100
        /// on the client, untouched on the host.
        ///
        /// Waits for a registry before asking. An earlier version fired as soon
        /// as a grid existed, which is enough to compose the question - the
        /// buildings are there with the ids their save came with - and not enough
        /// to match the answers, because the registry is a separate table and was
        /// still empty. A question you cannot hear the answer to is worse than no
        /// question, because it looks like it worked.
        /// </summary>
        private void AskAboutOwnDamageOnce()
        {
            if (_asked) return;
            if (Grid.WidthInCells == 0) return;
            if (NetworkIdentityRegistry.Count == 0) return;

            var ids = new List<int>();
            foreach (var hp in Object.FindObjectsByType<BuildingHP>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (hp.IsNullOrDestroyed() || hp.gameObject.IsNullOrDestroyed()) continue;
                if (hp.HitPoints >= hp.MaxHitPoints) continue;

                var identity = hp.gameObject.GetExistingNetIdentity();
                if (identity == null || identity.NetId == 0) continue;

                // Only ask about ids this peer can actually resolve later. One
                // that is not in the registry cannot be matched to the reply, and
                // asking anyway is what produced 36 unanswerable answers a run.
                if (!NetworkIdentityRegistry.Exists(identity.NetId)) continue;

                ids.Add(identity.NetId);
                if (ids.Count >= DamagedBuildingsQueryPacket.MaxIds) break;
            }

            // Marked asked either way. With nothing damaged there is nothing to
            // ask, and retrying every frame would be a busy loop over four
            // thousand components.
            _asked = true;
            if (ids.Count == 0) return;

            PacketSender.SendToHost(new DamagedBuildingsQueryPacket
            {
                RequesterId = MultiplayerSession.LocalUserID,
                NetIds = ids,
            });

            DebugConsole.Log($"[BuildingDamage] asked the host about {ids.Count} building(s) this peer shows as damaged");
        }

        /// <summary>
        /// How many clients were connected last sweep, so a new arrival can be
        /// noticed.
        /// </summary>
        private int _lastClientCount;

        /// <summary>Non-host players that had reported Ready last sweep.</summary>
        private int _lastReadyCount;

        /// <summary>Set when a peer arrives: the next sweep re-sends every damaged building, once.</summary>
        private bool _reassertDamaged;

        // Reused so a sweep over four thousand buildings does not allocate twice per tick.
        private readonly List<(int NetId, int HitPoints)> _damagedFirst = new List<(int, int)>();
        private readonly List<(int NetId, int HitPoints)> _healthy = new List<(int, int)>();

        /// <summary>
        /// Sends one building's hit points and records it only if it reached
        /// somebody.
        /// </summary>
        private bool SendOne(int netId, int hitPoints)
        {
            var packet = new BuildingDamagePacket { NetId = netId, HitPoints = hitPoints };
            int delivered = PacketSender.SendToAllClients(packet, PacketSendMode.Reliable);

            // Recorded only once it has gone somewhere. Marking it sent when
            // nobody received it is what let a tile creep from 43 to 51 hit
            // points on the host while the client held it whole.
            if (delivered > 0)
                _lastSent[netId] = hitPoints;

            return true;
        }

        private void Sweep()
        {
            using var _ = Profiler.Scope();

            // A peer that just joined has been told nothing, whatever this
            // syncer remembers telling the last one.
            //
            // The record is kept even when the send reached nobody, because
            // otherwise a host with no clients re-sends every building forever
            // and never settles. That is right for an empty session and wrong
            // the moment somebody arrives: the host sweeps while alone, marks
            // everything as told, and the client that connects afterwards
            // receives only what changes from then on. Measured, that was six
            // damage packets for a whole session and two tiles left disagreeing.
            //
            // What it re-sends is the damaged buildings, not all of them. Both
            // peers load the same save, so the healthy ones already agree - and
            // the version that re-sent everything proved it: of 3700 packets the
            // client received, 3634 said something it already knew, 16 changed
            // anything, and the flood cost a mid-session reconnect and a round of
            // id disagreements. Thirty-odd packets buy almost all of the value.
            //
            // One case is still not covered: a building the host repaired before
            // this peer joined reads full health here and damaged there, and
            // nothing on the host changed to trigger a send. A client-side query
            // for that was written and removed - the host answered all 36 ids it
            // was asked about and the client resolved none of them, and shipping
            // a reply path whose answers do not land is worse than the gap.
            // Counted on Ready, not on connect.
            //
            // ConnectedPlayers grows the moment the transport attaches, which is
            // well before the peer has a world - so re-asserting then sent 36
            // damaged buildings into an empty registry and all 36 failed to
            // resolve, every run, exactly matching the damaged count. The
            // IRequiresLoadedWorld gate did not catch it because the client has
            // not reported Loading yet at that instant either.
            //
            // Ready is the state that means "I can resolve an id", so that is
            // what to watch.
            int ready = 0;
            foreach (var player in MultiplayerSession.ConnectedPlayers.Values)
            {
                if (player.PlayerId == MultiplayerSession.HostUserID) continue;
                if (player.readyState == States.ClientReadyState.Ready) ready++;
            }

            if (ready > _lastReadyCount && _lastSent.Count > 0)
            {
                DebugConsole.Log(
                    $"[BuildingDamage] a peer became ready ({_lastReadyCount} -> {ready}); " +
                    "re-asserting the damaged buildings");
                _reassertDamaged = true;
            }
            _lastReadyCount = ready;

            int clients = MultiplayerSession.ConnectedPlayers.Count;
            _lastClientCount = clients;

            int scanned = 0;
            int changed = 0;
            int overBudget = 0;

            _damagedFirst.Clear();
            _healthy.Clear();

            foreach (var hp in Object.FindObjectsByType<BuildingHP>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (hp.IsNullOrDestroyed() || hp.gameObject.IsNullOrDestroyed())
                    continue;

                var identity = hp.gameObject.GetExistingNetIdentity();
                if (identity == null || identity.NetId == 0)
                    continue;

                scanned++;

                int current = hp.HitPoints;
                bool damaged = current < hp.MaxHitPoints;

                // A join re-asserts the damaged ones and nothing else.
                //
                // Clearing the whole record and re-sending every building was
                // measured and is not worth it: of 3700 packets the client
                // received, 3634 told it something it already knew, 16 changed
                // anything, and the flood cost a mid-session reconnect and a
                // round of id disagreements. Both peers load the same save, so
                // the healthy buildings agree without being told.
                bool force = _reassertDamaged && damaged;
                if (!force && _lastSent.TryGetValue(identity.NetId, out int previous) && previous == current)
                    continue;

                // Damaged first, because those are the ones a player can see and
                // the ones most likely to disagree. Everything else is a
                // background pass: both peers loaded the same save, so a
                // building at full health almost certainly already agrees - the
                // exception being one the host repaired before this peer joined,
                // which is why the healthy ones are sent at all rather than
                // skipped.
                if (damaged)
                    _damagedFirst.Add((identity.NetId, current));
                else
                    _healthy.Add((identity.NetId, current));
            }

            // With nobody listening, take the baseline silently.
            //
            // This is where the traffic was actually coming from, and it took
            // two measurements to see it. The join re-assert is thirty-odd
            // packets; the flood - 2986 of them, of which 2920 told the client
            // something it already knew - was the very first sweep, run while
            // the host was still alone, working through every building in the
            // world at fifty a tick and still going when the client arrived.
            //
            // A building nobody has been told about needs no packet if there is
            // nobody to tell. Recording the value and staying quiet leaves the
            // syncer settled, so once a client joins only genuine changes go out,
            // plus the damaged set and whatever the client asks about.
            if (clients <= 1)
            {
                foreach (var (netId, current) in _damagedFirst) _lastSent[netId] = current;
                foreach (var (netId, current) in _healthy) _lastSent[netId] = current;

                LastSweepScanned = scanned;
                LastSweepChanged = 0;
                LastSweepDeferred = 0;
                _reassertDamaged = false;
                return;
            }

            foreach (var (netId, current) in _damagedFirst)
            {
                if (!SendOne(netId, current)) { overBudget++; continue; }
                changed++;
            }

            foreach (var (netId, current) in _healthy)
            {
                if (changed >= MaxSendsPerSweep) { overBudget++; continue; }
                if (!SendOne(netId, current)) { overBudget++; continue; }
                changed++;
            }

            LastSweepScanned = scanned;
            LastSweepChanged = changed;
            LastSweepDeferred = overBudget;
            _reassertDamaged = false;
        }
    }
}
