using System.Collections.Generic;
using ONI_Together.Networking.Packets.Core;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
    /// <summary>
    /// Ask, continuously and for every kind of object at once, the question that has so far
    /// only been asked by hand: does the client have what the host has?
    ///
    /// See IdCensusPacket for why. In short: every "the client is missing it" defect found
    /// so far was found one type at a time, after a player noticed, by reading logs. The
    /// harness answers the general form of the question offline, after a run. This is the
    /// same question asked while the game is running.
    ///
    /// The host walks its registry forty ids at a time, once a second. The client looks
    /// each one up and remembers what it could not find. An id missing on one pass is
    /// probably in flight; an id missing on two consecutive passes is gone, and that is the
    /// number worth reporting.
    ///
    /// What this cannot see, stated so a zero is not over-read:
    /// - objects the HOST never named. The state dump has "Water#noid@51550" in it, a pile
    ///   the host holds with no id at all, and nothing without an id can appear in a census
    ///   of ids.
    /// - objects the client has and the host does not. The traffic only goes one way. That
    ///   direction has been the rarer one - HOST-ONLY runs 80 to 89 a run against
    ///   CLIENT-ONLY 4 to 11 - so it is the cheaper half to start with, not the only half
    ///   worth having.
    /// - anything at all while the client is not connected, which is when a reconnect is
    ///   most likely to have lost the registry.
    /// </summary>
    public class IdCensus : MonoBehaviour
    {
        /// <summary>
        /// Four batches a second, forty ids in each.
        ///
        /// The first version sent one batch a second, and that made the interesting counter
        /// impossible to read. This colony holds about 10,300 ids, so a pass took 4.3
        /// minutes and the persistent count - which needs two consecutive passes - needed
        /// 8.6. A scenario run is shorter than that from join to dump, so censusMissing2
        /// would have read zero in every run, and a zero that cannot be anything else is
        /// the trap this project has already been caught by four times.
        ///
        /// At four batches a second a pass takes about 65 seconds and two fit inside the
        /// settle window. 640 bytes a second of payload, against the ninety thousand sends
        /// this host already makes per three thousand frames.
        ///
        /// Forty per packet rather than a bigger batch less often: 40 ids is 160 bytes plus
        /// the header, well inside the 1000-byte Riptide payload limit. Two hundred would
        /// be 800 and close enough to that limit that LAN would start fragmenting, which is
        /// the path this codebase documents as where its worst bugs live.
        /// </summary>
        private const float SendInterval = 0.25f;
        private const int BatchSize = 40;

        /// <summary>
        /// Nothing until the colony has settled. A join or a load spawns everything at
        /// once and the two peers are legitimately out of step for a few seconds; asking
        /// then produces a page of absences that are all in flight.
        /// </summary>
        private const float StartDelay = 20f;

        private float _next;
        private float _startedAt;
        private bool _started;

        /// <summary>
        /// The keys to walk, snapshotted per pass. Taken once rather than re-read each
        /// batch, because the registry changes while the walk is in progress and a moving
        /// list would silently skip entries - which in an instrument built to find missing
        /// things is the one failure that cannot be allowed.
        /// </summary>
        private List<int> _snapshot;
        private int _position;
        private int _cycle;

        // --- client side ---

        private static readonly HashSet<int> _missingThisCycle = new();
        private static readonly HashSet<int> _missingLastCycle = new();
        private static int _cycleSeen = -1;

        /// <summary>Ids the host offered and this peer looked up.</summary>
        public static int Checked { get; private set; }

        /// <summary>Ids not found on this pass. Includes things merely in flight.</summary>
        public static int MissingNow { get; private set; }

        /// <summary>
        /// Ids missing on two consecutive passes. This is the number that means something:
        /// a pass is about a minute on this colony, and nothing legitimately in flight
        /// survives that long.
        /// </summary>
        public static int MissingPersistent { get; private set; }

        /// <summary>
        /// Of those, the ones this peer once held and let go.
        ///
        /// Splitting them was forced by an over-claim. The first run found 44 persistent
        /// absences, mostly Oxygen, DirtyWater and CarbonDioxide, and the host had
        /// announced every one of them - so it was reported as "the announcement was sent
        /// and the object never appeared". The evidence did not support that. "The client
        /// log never mentions this id" is equally consistent with the client having held
        /// the object and destroyed it afterwards, which is ordinary for gas piles: they
        /// merge constantly, and the two peers' simulations do not have to merge them the
        /// same way.
        ///
        /// Those two need opposite fixes. Never arrived means the announcement path is
        /// losing entries. Arrived and went means the peers' own simulations diverged
        /// about a pile, and no amount of delivery would help.
        ///
        /// The registry already keeps retired ids, so the question costs one lookup.
        /// </summary>
        public static int MissingAfterRetire { get; private set; }

        /// <summary>
        /// Persistent absences for ids this peer has no record of ever holding.
        ///
        /// Read this as "the two peers do not agree about this number", NOT as "this peer
        /// does not have the object". They are not the same thing and the first run of this
        /// counter proved it: id 1228387309 came up as never seen, the host has it as a
        /// Tile at cell 48770, and the client's own dump has a Tile at cell 48770 too. The
        /// tile is there. The number is not.
        ///
        /// This census walks ids, so an object present under a different id is
        /// indistinguishable from an object that is absent. That limit is inherent to
        /// asking the question this cheaply - 640 bytes a second buys id agreement and
        /// nothing else - and the run before this one was reported without it, which turned
        /// "the peers disagree about some numbers" into "the announcement never arrived".
        ///
        /// Answering the object question needs cell and prefab on the wire rather than a
        /// bare id, which is what the harness's state_compare does offline and what makes
        /// it five times the size. Worth doing if this number ever matters; so far it is
        /// 3 against 47 of the other kind.
        /// </summary>
        public static int MissingNeverSeen { get; private set; }

        /// <summary>Passes the host has completed, so a zero above can be told from "never ran".</summary>
        public static int CyclesCompleted { get; private set; }

        public static void Reset()
        {
            _missingThisCycle.Clear();
            _missingLastCycle.Clear();
            _cycleSeen = -1;
            Checked = 0;
            MissingNow = 0;
            MissingPersistent = 0;
            MissingAfterRetire = 0;
            MissingNeverSeen = 0;
            CyclesCompleted = 0;
            PrioritiesCorrected = 0;
            PrioritiesSent = 0;
        }

        private void Update()
        {
            if (!MultiplayerSession.InSession || !MultiplayerSession.IsHost) return;
            if (MultiplayerSession.ConnectedPlayers.Count <= 1) return;

            if (!_started)
            {
                _started = true;
                _startedAt = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - _startedAt < StartDelay) return;

            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + SendInterval;

            if (_snapshot == null || _position >= _snapshot.Count)
            {
                _snapshot = new List<int>();
                foreach (var entry in NetworkIdentityRegistry.AllEntries)
                    _snapshot.Add(entry.Key);
                _position = 0;
                _cycle++;
            }

            int take = Mathf.Min(BatchSize, _snapshot.Count - _position);
            if (take <= 0) return;

            var batch = new int[take];
            _snapshot.CopyTo(_position, batch, 0, take);
            _position += take;

            // Priority alongside the id, for the reason on IdCensusPacket.Priorities: it is
            // sampled periodically for buildings and for nothing else, so a suit in a
            // checkpoint keeps whatever the client last guessed.
            var priorities = new short[take];
            for (int i = 0; i < take; i++)
            {
                priorities[i] = SamplePriority(batch[i]);
                if (priorities[i] >= 0) PrioritiesSent++;
            }

            PacketSender.SendToAllClients(
                new IdCensusPacket { Cycle = _cycle, NetIds = batch, Priorities = priorities },
                PacketSendMode.Reliable);
        }

        private static short SamplePriority(int netId)
        {
            if (!NetworkIdentityRegistry.TryGet(netId, out var identity)
                || identity.IsNullOrDestroyed()
                || !identity.TryGetComponent<Prioritizable>(out var prioritizable))
                return -1;

            var p = prioritizable.GetMasterPriority();
            return (short)(((int)p.priority_class * 100) + p.priority_value);
        }

        /// <summary>Client side: look each id up, and remember what was not there.</summary>
        internal static void Receive(int cycle, int[] netIds, short[] priorities)
        {
            if (netIds == null) return;

            if (cycle != _cycleSeen)
            {
                // A new pass started, so the previous one is complete and can be judged.
                // Kept whole rather than counted here: the comparison that matters is
                // against the pass before, and that needs both sets to still exist.
                if (_cycleSeen >= 0)
                {
                    CyclesCompleted++;
                    _missingLastCycle.Clear();
                    foreach (int id in _missingThisCycle) _missingLastCycle.Add(id);
                }
                _missingThisCycle.Clear();
                _cycleSeen = cycle;
            }

            for (int i = 0; i < netIds.Length; i++)
            {
                int id = netIds[i];
                if (id == 0) continue;
                Checked++;

                if (NetworkIdentityRegistry.TryGet(id, out var identity) && !identity.IsNullOrDestroyed())
                {
                    if (priorities != null && i < priorities.Length)
                        ApplyPriority(identity, priorities[i]);
                    continue;
                }

                _missingThisCycle.Add(id);
                MissingNow++;

                // Two passes apart, which is about two minutes on this colony. Anything
                // still absent after that was not in flight.
                if (_missingLastCycle.Contains(id))
                {
                    MissingPersistent++;

                    // Held once and let go, or never held at all - see MissingAfterRetire.
                    bool wasHere = NetworkIdentityRegistry.ExistsOrRetired(id);
                    if (wasHere) MissingAfterRetire++; else MissingNeverSeen++;

                    DebugTools.ThrottledLog.Warn(
                        $"[Census] the host holds NetId {id} and this peer does not, on two " +
                        $"consecutive passes; this peer {(wasHere ? "had it and retired it" : "has no record of ever holding it")} " +
                        $"- grep the host log for that id to see what it is " +
                        $"({MissingAfterRetire} retired, {MissingNeverSeen} never seen)");
                }
            }
        }

        /// <summary>Priorities this peer had to correct because no event carried them.</summary>
        public static int PrioritiesCorrected { get; private set; }

        /// <summary>
        /// Priorities the host actually put on the wire - the control for the number above.
        ///
        /// The first run of the priority field read censusPrio=0 across 37,846 ids, and a
        /// zero like that has two readings that look identical: the two peers agree about
        /// every priority, or nothing was ever sampled and the check could not fire. This
        /// project has been caught by the second four times, so the sender counts what it
        /// sent and the two are read together. A large number here beside a zero above is
        /// agreement; a zero here means the sampler is broken and the client's zero means
        /// nothing at all.
        /// </summary>
        public static int PrioritiesSent { get; private set; }

        private static void ApplyPriority(NetworkIdentity identity, short packed)
        {
            if (packed < 0) return;
            if (!identity.TryGetComponent<Prioritizable>(out var prioritizable)) return;

            int cls = packed / 100;
            int val = packed % 100;

            var current = prioritizable.GetMasterPriority();
            if ((int)current.priority_class == cls && current.priority_value == val) return;

            // Under the flag the dedicated priority packet uses. PrioritizablePatch hooks
            // SetMasterPriority and broadcasts from it, so an unguarded correction would go
            // straight back to the sender and the two peers would trade the same value for
            // as long as they disagreed. The structure path does exactly this already.
            bool wasApplying = Packets.World.PrioritizeStatePacket.IsApplying;
            Packets.World.PrioritizeStatePacket.IsApplying = true;
            try
            {
                prioritizable.SetMasterPriority(
                    new PrioritySetting((PriorityScreen.PriorityClass)cls, val));
            }
            finally
            {
                Packets.World.PrioritizeStatePacket.IsApplying = wasApplying;
            }

            PrioritiesCorrected++;
        }
    }
}
