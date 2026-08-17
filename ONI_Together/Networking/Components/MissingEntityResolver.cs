using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
    /// <summary>
    /// Asks the host about ids this peer keeps being told about and does not have.
    ///
    /// The registry records every failed lookup already - that is the divergence
    /// gate - and until now the count was all it produced. The same signal is
    /// enough to fix the problem instead of only reporting it: an id that
    /// arrives repeatedly and resolves to nothing is an object this peer is
    /// missing, and the host can send it.
    ///
    /// Everything here is bounded, because an unbounded repair loop on a
    /// divergent world is worse than the divergence. Ids are asked about at a
    /// fixed rate, each one a limited number of times, and an id that never
    /// resolves is given up on rather than asked forever.
    /// </summary>
    public class MissingEntityResolver : MonoBehaviour
    {
        public static MissingEntityResolver Instance { get; private set; }

        /// <summary>Requests per second. Small on purpose: this competes with the traffic that is actually needed.</summary>
        private const float RequestInterval = 0.2f;

        /// <summary>How many times one id is worth asking about before concluding the host cannot answer.</summary>
        private const int MaxAttemptsPerId = 3;

        /// <summary>
        /// Ceiling on the queue. A client that is genuinely far out of sync
        /// would otherwise queue thousands of ids and spend the session asking.
        /// </summary>
        private const int MaxQueued = 512;

        private readonly Queue<int> _pending = new Queue<int>();
        private readonly HashSet<int> _queued = new HashSet<int>();
        private readonly Dictionary<int, int> _attempts = new Dictionary<int, int>();

        private float _nextRequest;

        public int PendingCount => _pending.Count;
        public int RequestsSent { get; private set; }
        public int GaveUpOn { get; private set; }

        /// <summary>
        /// Ids the host answered for by saying it has no such object either.
        ///
        /// These are not gaps. A ground item can spawn, be picked up and be gone
        /// from both peers before anyone asks about it, and the one unresolved id
        /// left in an otherwise clean run was exactly that. Counting a dead object
        /// as something this peer is missing made the divergence gate fire on a
        /// session where nothing had diverged.
        /// </summary>
        public int ConfirmedGone { get; private set; }

        private readonly HashSet<int> _gone = new HashSet<int>();

        /// <summary>Ids already counted as given up on, so the count can be corrected if the host later answers.</summary>
        private readonly HashSet<int> _gaveUpIds = new HashSet<int>();

        /// <summary>
        /// What the given-up ids turned out to be, asked of the registry at the moment the
        /// number is read rather than when the question went out.
        ///
        /// GaveUpOn was one number covering three unrelated situations, and the gate called
        /// all of it "objects this peer is genuinely missing". Split by hand across the two
        /// runs of build 10DC528F, from the two peers' id dumps:
        ///
        ///     run 1   39 given up   2 present here   29 held by the host   8 on neither
        ///     run 2   41 given up   5 present here   30 held by the host   6 on neither
        ///
        /// and every one of the 59 the host held was a Pickupable gas, liquid or dirt pile -
        /// Oxygen, DirtyWater, Water, Methane, CarbonDioxide, Hydrogen, Dirt. No building,
        /// no duplicant, no item a player could point at. The client's own census had
        /// already named the case correctly on the same ids: "this peer had it and retired
        /// it". The two simulations merge loose gas differently, which is a known and
        /// deliberately unfixed item, and this gate was reporting it under another name and
        /// failing every single run for it.
        ///
        /// A gate that is red every run for a benign reason is worse than no gate: a
        /// building that really did fail to replicate would land in the same number and
        /// nobody would look. So the three are separated here, and only the third one is a
        /// gap. Nothing is hidden - all three are reported.
        ///
        /// Present is asked of the registry because the resolver never learns that an id
        /// arrived: the correction path exists only for the host's "gone" and "held"
        /// answers, and an id answered with an actual spawn stayed counted as given up
        /// forever. That is the 2 and the 5 above.
        /// </summary>
        public int GaveUpButPresent { get; private set; }
        public int GaveUpAfterRetiring { get; private set; }
        public int GaveUpNeverHeld { get; private set; }

        /// <summary>
        /// Sorts the given-up ids into the three cases above. Called before the numbers are
        /// read, so they describe the registry as it is now and not as it was mid-flight.
        /// </summary>
        public void ClassifyGaveUp()
        {
            GaveUpButPresent = 0;
            GaveUpAfterRetiring = 0;
            GaveUpNeverHeld = 0;

            foreach (int netId in _gaveUpIds)
            {
                if (NetworkIdentityRegistry.Exists(netId)) GaveUpButPresent++;
                else if (NetworkIdentityRegistry.ExistsOrRetired(netId)) GaveUpAfterRetiring++;
                else GaveUpNeverHeld++;
            }
        }

        /// <summary>Called when the host reports that an id does not exist on its side.</summary>
        public static void NoteConfirmedGone(int netId)
        {
            var instance = Instance;
            if (instance.IsNullOrDestroyed()) return;

            if (instance._gone.Add(netId))
                instance.ConfirmedGone++;

            // The answer can arrive after this id was already written off, because
            // the attempt is counted when the question goes out rather than when it
            // comes back. Move it out of the failure count instead of leaving it in
            // both - a gap the host has explicitly denied is not a gap.
            if (instance._gaveUpIds.Remove(netId) && instance.GaveUpOn > 0)
                instance.GaveUpOn--;

            // Stop asking. Without this the id runs to the attempt limit and is
            // then recorded as given up on, which reads as a real gap.
            instance._attempts[netId] = MaxAttemptsPerId;
            instance._queued.Remove(netId);
        }

        /// <summary>
        /// Where Update stopped, counted.
        ///
        /// The first version of this queued 119 ids and sent none, and every
        /// explanation I could reason out was excluded by something else in the
        /// same reading. Guessing which guard fired is what this session has
        /// paid for repeatedly, so the guards count themselves.
        /// </summary>
        public int Ticks { get; private set; }
        public int SkippedNotInSession { get; private set; }
        public int SkippedNoWorld { get; private set; }
        public int SkippedEmptyOrTooSoon { get; private set; }
        public int SkippedAlreadyPresent { get; private set; }

        public string Describe() =>
            $"ticks={Ticks} sent={RequestsSent} pending={_pending.Count} gaveup={GaveUpOn} gone={ConfirmedGone} " +
            $"skip[session={SkippedNotInSession} world={SkippedNoWorld} " +
            $"idle={SkippedEmptyOrTooSoon} present={SkippedAlreadyPresent}]";

        private void OnEnable() => Instance = this;

        private void OnDisable()
        {
            if (Instance == this) Instance = null;
        }

        public void ResetForNewSession()
        {
            _pending.Clear();
            _queued.Clear();
            _attempts.Clear();
            RequestsSent = 0;
            GaveUpOn = 0;
            ConfirmedGone = 0;
            _gone.Clear();
            _gaveUpIds.Clear();
        }

        /// <summary>
        /// Called from the registry when a lookup fails. Cheap and allocation
        /// free on the common path, because it runs inside a failure that is
        /// already being counted and must not become the expensive part.
        /// </summary>
        public static void Report(int netId)
        {
            var instance = Instance;
            if (instance.IsNullOrDestroyed()) return;
            instance.Enqueue(netId);
        }

        private void Enqueue(int netId)
        {
            if (netId == 0) return;
            if (!MultiplayerSession.IsClient) return;

            // Already asked to the limit, or the host has said it does not exist.
            if (_gone.Contains(netId))
                return;
            if (_attempts.TryGetValue(netId, out int tried) && tried >= MaxAttemptsPerId)
                return;

            if (_queued.Contains(netId)) return;
            if (_pending.Count >= MaxQueued) return;

            _pending.Enqueue(netId);
            _queued.Add(netId);
        }

        private void Update()
        {
            using var _ = Profiler.Scope();

            Ticks++;

            if (!MultiplayerSession.InSession || !MultiplayerSession.IsClient)
            {
                SkippedNotInSession++;
                return;
            }

            // Nothing can be spawned into a world that does not exist yet, and
            // asking during the load competes with the hard sync for the little
            // bandwidth there is.
            if (Grid.WidthInCells == 0)
            {
                SkippedNoWorld++;
                return;
            }

            if (_pending.Count == 0 || Time.unscaledTime < _nextRequest)
            {
                SkippedEmptyOrTooSoon++;
                return;
            }

            _nextRequest = Time.unscaledTime + RequestInterval;

            int netId = _pending.Dequeue();
            _queued.Remove(netId);

            // It may have arrived by another route while it sat in the queue.
            if (NetworkIdentityRegistry.Exists(netId))
            {
                SkippedAlreadyPresent++;
                return;
            }

            _attempts.TryGetValue(netId, out int tried);
            _attempts[netId] = tried + 1;
            if (tried + 1 >= MaxAttemptsPerId)
            {
                GaveUpOn++;
                _gaveUpIds.Add(netId);
                ThrottledLog.Warn(
                    $"[MissingEntityResolver] gave up on NetId {netId} after {MaxAttemptsPerId} requests - " +
                    "the host either does not have it either, or it is not a loose item");
            }

            PacketSender.SendToHost(new EntityResolveRequestPacket
            {
                NetId = netId,
                // Filled in explicitly. A reply addressed to player 0 goes
                // nowhere, and a whole request-reply path in this mod was
                // dropping every answer for exactly that reason.
                RequesterId = MultiplayerSession.LocalUserID,
            });

            RequestsSent++;
        }
    }
}
