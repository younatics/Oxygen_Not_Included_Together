using UnityEngine;

namespace ONI_Together.Networking.Components
{
    /// <summary>
    /// The part of syncing that has nothing to do with being a building.
    ///
    /// This mod replicates state three different ways, and one of them works. Per-object
    /// state syncers - sample, compare, send the difference, resend a keyframe on a timer,
    /// apply - accounted for no divergence at all across this session's runs. Global sweeps
    /// are next. Intercepting events is last, and it produced most of what real play
    /// reported: an artwork that finished on the host and stayed blank on the client, a
    /// priority a player set, damage on a tile. Interception fails the same way every time,
    /// because it requires somebody to have found the event first, and nobody finds all of
    /// them.
    ///
    /// The machinery that makes the first approach work is not about buildings. A timer, a
    /// keyframe clock that only advances when a packet was really delivered, a phase offset
    /// so a thousand objects do not all speak on one frame, and a record of when this object
    /// was last corrected. None of that reads a cell or a BuildingHP. It lived inside
    /// StructureSyncerBase only because structures were the first thing to need it, so
    /// duplicants, critters and loose items were left to event interception - which is where
    /// the defects are.
    ///
    /// So it moves here, unchanged, and structures keep the parts that really are theirs:
    /// the cell, Operational, viewport culling. This step deliberately changes no behaviour;
    /// its whole purpose is to let the next things move onto a base that has already been
    /// measured working.
    ///
    /// Cost is not the argument for any of this and must not be offered as one. The same
    /// colony was measured at 77.1-77.8 ms a frame while hosting and 76.2-76.8 ms with the
    /// session ended, so the entire multiplayer layer is worth about 1 ms a frame here. The
    /// argument is that a keyframe repairs a peer nobody noticed was wrong, and an
    /// intercepted event cannot.
    /// </summary>
    public abstract class SyncedEntityBase : KMonoBehaviour
    {
        /// <summary>How often this object considers whether it has anything to say.</summary>
        protected float sendInterval = 0.5f;
        private float _timer;

        /// <summary>
        /// How often an object states itself even though nothing changed.
        ///
        /// Everything here was delta-only, and a delta cannot repair a peer that is
        /// already wrong and has stopped moving. Measured across a run: a MetalRefinery
        /// holding 310 kg of water on the host and 800 on the client, a MicrobeMusher at
        /// 150 kg of dirt against 75, an AlgaeHabitat at 316 kg against 38. Sixteen
        /// containers more than 5 kg apart, and the values are clean multiples rather
        /// than drift - the client took a different number of deliveries and then both
        /// sides went idle. The host's mass never changed again, so it never spoke
        /// again, and the client stayed wrong for the rest of the session.
        ///
        /// Fifteen seconds, phase-spread per instance so four hundred structures do not
        /// all speak on the same frame. Costs about thirty small packets a second
        /// against the ninety thousand this host already sends per three thousand
        /// frames, and it is the only thing that can close a divergence nobody is
        /// touching.
        /// </summary>
        private const float ResyncInterval = 15f;

        /// <summary>
        /// Nothing is sampled for the first few seconds after this object starts ticking.
        ///
        /// A colony load spawns everything at once, and sampling during that produces
        /// packets about objects whose ids the other peer has not filed yet.
        /// </summary>
        private const float INITIAL_DELAY = 5f;

        private bool _initialized;
        private float _initializationTime;
        private float _nextResync;

        /// <summary>Sends that happened only because the keyframe came due.</summary>
        public static int ResyncsForced { get; private set; }

        public static void ResetResyncCount() => ResyncsForced = 0;

        private float _lastAppliedTime;

        /// <summary>
        /// Seconds since a packet last set this object's state, or -1 if none ever has.
        ///
        /// Three containers of 953 disagree at the instant of the snapshot while every
        /// refusal counter reads zero and a hundred thousand applications succeeded. A
        /// single snapshot cannot say whether that is a container the sync is failing on
        /// or one whose update was simply in flight when both peers froze, and those need
        /// opposite responses. Pausing and dumping again cannot answer it either - a
        /// paused colony produces the same numbers twice.
        ///
        /// When it was last updated does answer it. A container corrected a moment before
        /// the pause was in flight; one that has not been touched in a minute is not.
        /// </summary>
        public float SecondsSinceApplied =>
            _lastAppliedTime == 0 ? -1f : Time.unscaledTime - _lastAppliedTime;

        /// <summary>Call from the apply path, so the reading above means what it says.</summary>
        protected void MarkStateApplied() => _lastAppliedTime = Time.unscaledTime;

        private void Update()
        {
            if (!MultiplayerSession.SessionHasPlayers) return;

            if (MultiplayerSession.IsHost)
            {
                if (!_initialized)
                {
                    _initializationTime = Time.unscaledTime;
                    _initialized = true;

                    // Spread by instance id rather than at random: scripts here may not
                    // call Math.Random, and a deterministic offset is easier to reason
                    // about anyway. Any spread will do - the point is only that four
                    // hundred structures do not come due together.
                    float phase = (System.Math.Abs(GetInstanceID()) % 1024) / 1024f;
                    _nextResync = Time.unscaledTime + INITIAL_DELAY + phase * ResyncInterval;
                    return;
                }

                if (Time.unscaledTime - _initializationTime < INITIAL_DELAY) return;
                HostTick();
            }
            else
            {
                ClientTick();
            }
        }

        /// <summary>
        /// True once per sendInterval. Kept here rather than in each subclass because a
        /// subclass that forgets the reset samples every frame and nothing says so.
        /// </summary>
        protected bool DueToSample()
        {
            _timer += Time.unscaledDeltaTime;
            if (_timer < sendInterval) return false;
            _timer = 0f;
            return true;
        }

        /// <summary>Whether this tick owes a keyframe regardless of what changed.</summary>
        protected bool DueForResync => Time.unscaledTime >= _nextResync;

        /// <summary>
        /// The keyframe clock advances on delivery, not on decision.
        ///
        /// A keyframe that came due while nobody was looking has not repaired anything,
        /// and rescheduling it anyway would mean waiting another fifteen seconds after
        /// the client scrolls over.
        /// </summary>
        protected void MarkResyncDelivered()
        {
            _nextResync = Time.unscaledTime + ResyncInterval;
            ResyncsForced++;
        }

        /// <summary>Sample, compare, send. Called at most once per sendInterval.</summary>
        protected abstract void HostTick();

        /// <summary>Whatever this object does on the peer that only receives. Every frame.</summary>
        protected abstract void ClientTick();
    }
}
