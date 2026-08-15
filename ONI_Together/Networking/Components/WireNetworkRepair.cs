using UnityEngine;

namespace ONI_Together.Networking.Components
{
    /// <summary>
    /// Put back into the electrical network any wire on this peer that is not in it.
    ///
    /// The measured symptom: after a session of building, six wires on the client sat on
    /// the wire layer with Wire.IsConnected false, so the circuits they belonged to were
    /// short several segments. The state dump showed it as a circuit the host said had
    /// seven generators and the client said had none - which on screen is a base that
    /// looks unpowered to one player and fine to the other.
    ///
    /// The cause is still open. Four rounds of investigation disproved four explanations,
    /// and the inputs are identical where they were checked: the same 970 wire cells, the
    /// same prefabs, the same GetWireConnections bits on both peers. Calling Connect() on
    /// those six and rebuilding took the divergence from 384 rows to 0, so the repair is
    /// known to work while the reason they were left out is not.
    ///
    /// Repairing without knowing the cause is the same argument the keyframe makes, and
    /// this project has now paid for that lesson twice. An event that is sent and lost
    /// looks exactly like an event nobody sends; a wire left out of the network by a path
    /// nobody has found looks exactly like one left out by a path that does not exist. In
    /// both cases the peer stays wrong until something looks again, and nothing did.
    ///
    /// Stated as an invariant rather than a fix, because that is what it is: a wire object
    /// standing on the wire layer belongs to the network. Anything that breaks that
    /// invariant in future is repaired by this too, including whatever is breaking it now.
    ///
    /// Client only, and deliberately. The host is the peer everything else is compared
    /// against, and a sweep that silently edited it would make the two agree by changing
    /// the answer rather than the copy.
    /// </summary>
    public class WireNetworkRepair : MonoBehaviour
    {
        /// <summary>
        /// Five seconds. The scan is a walk over the wire layer and the rebuild only runs
        /// on a sweep that actually found something, so the normal case - nothing to fix -
        /// costs the walk and nothing else.
        /// </summary>
        private const float SweepInterval = 5f;

        private float _nextSweep;

        /// <summary>
        /// Wires this peer had to put back into the network. Non-zero is this running and
        /// finding real work; zero across a session where the circuit rows also disappear
        /// means the wires were joining on their own that run and this proved nothing.
        /// </summary>
        public static int WiresReconnected { get; private set; }

        /// <summary>Sweeps that found at least one, so a rebuild was worth doing.</summary>
        public static int RepairSweeps { get; private set; }

        private void Update()
        {
            if (!MultiplayerSession.InSession || !MultiplayerSession.IsClient) return;
            if (Game.Instance == null) return;
            if (Time.unscaledTime < _nextSweep) return;
            _nextSweep = Time.unscaledTime + SweepInterval;

            int fixedUp = 0;
            int layer = (int)ObjectLayer.Wire;

            for (int cell = 0; cell < Grid.CellCount; cell++)
            {
                if (!Grid.IsValidCell(cell)) continue;

                var occupant = Grid.Objects[cell, layer];
                if (occupant == null || occupant.IsNullOrDestroyed()) continue;

                var wire = occupant.GetComponent<Wire>();
                if (wire == null || wire.IsConnected) continue;

                wire.Connect();
                fixedUp++;
            }

            if (fixedUp == 0) return;

            // Only after a real change. Rebuilding the networks every five seconds would
            // be a cost paid forever for a fault that happens a handful of times a
            // session, and this mod has already had one measurement ruined by an
            // instrument that was more expensive than what it measured.
            Game.Instance.electricalConduitSystem.ForceRebuildNetworks();
            Game.Instance.circuitManager.Rebuild();

            WiresReconnected += fixedUp;
            RepairSweeps++;

            DebugTools.ThrottledLog.Warn(
                $"[WireRepair] put {fixedUp} wire(s) back into the electrical network - " +
                "they were standing on the wire layer without belonging to a circuit, " +
                $"{WiresReconnected} in this session");
        }
    }
}
