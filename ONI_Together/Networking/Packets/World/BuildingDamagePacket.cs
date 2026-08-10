using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using System.IO;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
    /// <summary>
    /// One building's hit points, host to client.
    ///
    /// Damage used to ride along in StructureStatePacket's optional values,
    /// which meant it was only replicated for buildings that happen to have a
    /// StructureSyncerBase - and only when that syncer's other state changed.
    /// Neither condition holds where it matters. A Tile has no syncer at all, so
    /// a tile could crack on the host and read whole on the client forever; the
    /// storage, battery and reactor syncers set checkOptionalsValuesForChanges
    /// to false, so their hit points changed without ever triggering a send.
    /// Three buildings disagreed in one measured run, one of them in the
    /// direction that says the client damaged something the host never did.
    ///
    /// So damage has its own owner now, attached to every BuildingHP, and it is
    /// deliberately viewport-blind: a cracked wall stays cracked whether or not
    /// anyone watched it break, and the packet is small and rare.
    /// </summary>
    public class BuildingDamagePacket : IPacket, IRequiresLoadedWorld
    {
        public int NetId;
        public int HitPoints;

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();
            writer.Write(NetId);
            writer.Write(HitPoints);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();
            NetId = reader.ReadInt32();
            HitPoints = reader.ReadInt32();
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (!MultiplayerSession.IsClient)
                return;

            if (!NetworkIdentityRegistry.TryGet(NetId, out var identity))
                return;

            if (identity.gameObject.IsNullOrDestroyed())
                return;

            var hp = identity.gameObject.GetComponent<BuildingHP>();
            if (hp == null)
                return;

            Apply(hp, HitPoints);
        }

        /// <summary>
        /// Bring this peer's damage in line with the host's.
        ///
        /// BuildingHP.HitPoints has no setter, so the difference goes through the
        /// game's own damage event - the same one raised when something actually
        /// breaks a building. That matters for more than tidiness: the event is
        /// what updates Damaged, queues the repair errand and puts the broken
        /// overlay on. Writing a number would change the number and nothing else.
        /// </summary>
        public static void Apply(BuildingHP buildingHP, int hostHitPoints)
        {
            // Clamped to what this building can hold. The game's damage handler
            // just subtracts, so an unclamped negative delta would push hit
            // points past the maximum and leave it permanently over-healed.
            int hostHp = Mathf.Clamp(hostHitPoints, 0, buildingHP.MaxHitPoints);
            int delta = buildingHP.HitPoints - hostHp;
            if (delta == 0) return;

            // Positive delta: this peer is healthier than the host, so damage it
            // by the difference. Negative: the host repaired, so heal by it.
            buildingHP.gameObject.BoxingTrigger((int)GameHashes.DoBuildingDamage, new BuildingHP.DamageSourceInfo
            {
                damage = delta,
                source = "Multiplayer",
                popString = string.Empty,
            });
        }
    }
}
