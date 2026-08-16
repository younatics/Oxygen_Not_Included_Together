using UnityEngine;

namespace ONI_Together.Networking.Components
{
    /// <summary>
    /// Join a wire that is provably in the wrong place: one whose own connection bits say
    /// it touches a neighbour, where that neighbour is on a network and it is not.
    ///
    /// This is the second attempt. The first swept every wire reading IsConnected false and
    /// called Connect() on all of them - about seventy - and it never converged: the same
    /// population came back five seconds later, forty-six sweeps running, and the
    /// divergence it aimed at did not move. It was reverted.
    ///
    /// The measurement that followed explains it. Sixty-seven of those seventy are isolated
    /// stubs that BOTH peers hold with no network, and they are not wrong. At the moment
    /// the two peers are compared the host has 67 wires with no network and the client has
    /// 70, and the three extra are cells 38796, 38797 and 48247 - network 103 on the host,
    /// none on the client. The client's own diagnostic for one of them reads
    /// "inGrid=True active=True complete=True wireNet=none connected=False" with the same
    /// connection bits the host has.
    ///
    /// Those three sit at a junction, so their absence splits a 517-cell circuit into 43
    /// and 471 on the client, and that split is the entire 474-row electrical divergence -
    /// the largest single disagreement in the whole comparison, from three wires.
    ///
    /// So the invariant is narrowed to what is actually provable. A wire with no network
    /// whose bits point at a neighbour that HAS one cannot be right: conductors that touch
    /// share a network. An isolated stub points at nothing, or at neighbours equally
    /// unnetworked, and is left alone - which is what the previous version got wrong.
    ///
    /// Client only. The host is what everything else is compared against, and a sweep that
    /// edited it would make the peers agree by changing the answer rather than the copy.
    /// </summary>
    public class WireRejoin : MonoBehaviour
    {
        /// <summary>
        /// Ten seconds. The scan is one walk of the wire layer and the rebuild only runs on
        /// a sweep that found something, so the ordinary case - nothing to fix - costs the
        /// walk. The previous version rebuilt the electrical networks every five seconds
        /// for nothing, which is a cost this codebase has already paid once.
        /// </summary>
        private const float SweepInterval = 10f;

        /// <summary>
        /// Nothing for the first half minute. A join or a load leaves the networks being
        /// built, and a wire is legitimately without one for a moment.
        /// </summary>
        private const float StartDelay = 30f;

        private float _next;
        private float _startedAt;
        private bool _started;

        /// <summary>
        /// Cells that qualified on the previous sweep, so only a wire that is still wrong
        /// ten seconds later is touched.
        ///
        /// Without this the sweep called Connect() 1,260 times in a run over 20 sweeps -
        /// sixty-three each time - for a fault that is three wires. Right after
        /// ForceRebuildNetworks a wire reads NetworkID MaxValue for a moment, so the sweep
        /// kept re-qualifying wires it had just joined and rebuilding the networks again on
        /// the next tick. It also over-corrected: the client finished with 68 unnetworked
        /// wires against the host's 70, having joined two the host leaves alone.
        ///
        /// The census settled the same problem the same way. An object missing once is
        /// probably in flight; one missing twice is wrong. A wire unnetworked beside a
        /// networked neighbour on two consecutive sweeps is not mid-rebuild.
        /// </summary>
        private readonly System.Collections.Generic.HashSet<int> _suspectLastSweep = new();

        /// <summary>
        /// Wires joined because a neighbour they touch was already on a network.
        ///
        /// Judge this against the one-sided count in the state dump, not on its own. The
        /// previous attempt reported 3,160 repairs and fixed nothing; a number of calls is
        /// not a number of fixes, and that is exactly how it misled.
        /// </summary>
        public static int Rejoined { get; private set; }

        /// <summary>Sweeps that found at least one, so a rebuild was worth doing.</summary>
        public static int RejoinSweeps { get; private set; }

        private void Update()
        {
            if (!MultiplayerSession.InSession || !MultiplayerSession.IsClient) return;
            if (Game.Instance == null || Grid.WidthInCells == 0) return;

            if (!_started)
            {
                _started = true;
                _startedAt = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - _startedAt < StartDelay) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + SweepInterval;

            int layer = (int)ObjectLayer.Wire;
            int fixedUp = 0;
            var suspectNow = new System.Collections.Generic.HashSet<int>();

            for (int cell = 0; cell < Grid.CellCount; cell++)
            {
                if (!Grid.IsValidCell(cell)) continue;

                var occupant = Grid.Objects[cell, layer];
                if (occupant == null || occupant.IsNullOrDestroyed()) continue;

                var wire = occupant.GetComponent<Wire>();
                if (wire == null || wire.NetworkID != ushort.MaxValue) continue;

                // The wire's own account of which sides it joins. Read from the system the
                // circuit walk reads, so this asks the same question the game does.
                var bits = Game.Instance.electricalConduitSystem.GetConnections(cell, true);
                if (bits == 0) continue;

                if (!TouchesANetworkedNeighbour(cell, bits, layer)) continue;

                suspectNow.Add(cell);

                // Only if it was already wrong last sweep - see _suspectLastSweep.
                if (!_suspectLastSweep.Contains(cell)) continue;

                wire.Connect();
                fixedUp++;
            }

            _suspectLastSweep.Clear();
            foreach (int c in suspectNow) _suspectLastSweep.Add(c);

            if (fixedUp == 0) return;

            Game.Instance.electricalConduitSystem.ForceRebuildNetworks();
            Game.Instance.circuitManager.Rebuild();

            Rejoined += fixedUp;
            RejoinSweeps++;

            DebugTools.ThrottledLog.Warn(
                $"[WireRejoin] joined {fixedUp} wire(s) that touch a neighbour already on a " +
                $"network and had none themselves - {Rejoined} this session");
        }

        /// <summary>
        /// True when a side this wire says it connects on holds a wire that is on a network.
        ///
        /// Only the four sides, and only where the bit is set - a wire that does not claim
        /// the side is not touching anything there, whatever stands next to it.
        /// </summary>
        private static bool TouchesANetworkedNeighbour(int cell, UtilityConnections bits, int layer)
        {
            if ((bits & UtilityConnections.Left) != 0 && IsNetworked(Grid.CellLeft(cell), layer)) return true;
            if ((bits & UtilityConnections.Right) != 0 && IsNetworked(Grid.CellRight(cell), layer)) return true;
            if ((bits & UtilityConnections.Up) != 0 && IsNetworked(Grid.CellAbove(cell), layer)) return true;
            if ((bits & UtilityConnections.Down) != 0 && IsNetworked(Grid.CellBelow(cell), layer)) return true;
            return false;
        }

        private static bool IsNetworked(int cell, int layer)
        {
            if (!Grid.IsValidCell(cell)) return false;
            var o = Grid.Objects[cell, layer];
            if (o == null || o.IsNullOrDestroyed()) return false;
            var w = o.GetComponent<Wire>();
            return w != null && w.NetworkID != ushort.MaxValue;
        }
    }
}
