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

        private float _nextSweep;

        /// <summary>Last hit points we told clients about, by NetId.</summary>
        private readonly Dictionary<int, int> _lastSent = new Dictionary<int, int>();

        /// <summary>Buildings whose damage differed from what clients were told, this sweep.</summary>
        public int LastSweepChanged { get; private set; }
        public int LastSweepScanned { get; private set; }

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
        }

        private void Update()
        {
            using var _ = Profiler.Scope();

            if (!MultiplayerSession.InSession || !MultiplayerSession.IsHost)
                return;

            if (Time.unscaledTime < _nextSweep)
                return;
            _nextSweep = Time.unscaledTime + SweepInterval;

            Sweep();
        }

        private void Sweep()
        {
            using var _ = Profiler.Scope();

            int scanned = 0;
            int changed = 0;

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
                if (_lastSent.TryGetValue(identity.NetId, out int previous) && previous == current)
                    continue;

                var packet = new BuildingDamagePacket
                {
                    NetId = identity.NetId,
                    HitPoints = current,
                };

                int delivered = PacketSender.SendToAllClients(packet, PacketSendMode.Reliable);

                // Recorded only once it has gone somewhere. Marking it sent when
                // nobody received it is what let a tile creep from 43 to 51 hit
                // points on the host while the client held it whole - the change
                // was noticed once, discarded, and never noticed again.
                if (delivered > 0 || MultiplayerSession.ConnectedPlayers.Count <= 1)
                    _lastSent[identity.NetId] = current;

                changed++;
            }

            LastSweepScanned = scanned;
            LastSweepChanged = changed;
        }
    }
}
