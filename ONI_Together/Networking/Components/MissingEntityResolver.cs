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
            $"ticks={Ticks} sent={RequestsSent} pending={_pending.Count} gaveup={GaveUpOn} " +
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
