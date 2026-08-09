using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
    public struct BuildingState
    {
        public int Cell;
        public string PrefabName;
    }

    public class BuildingStatePacket : IPacket
    {
        public List<BuildingState> Buildings = new List<BuildingState>();

        /// <summary>
        /// Which sweep this batch belongs to, and where in it.
        ///
        /// Unlike the plant and dig sweeps, this receiver does NOT reconcile by
        /// absence - a building missing from the snapshot is left alone - so the
        /// batches would be safe to apply independently. They are still assembled
        /// first, for a different reason: Reconcile walks every local building
        /// twice and starts a coroutine per packet. Applying N batches
        /// independently would do that N times over the whole colony, which on a
        /// large one costs more than the oversize packet it was meant to fix.
        /// </summary>
        public int SweepId;
        public int BatchIndex;
        public int BatchCount = 1;

        /// <summary>Count, sweep header, and what the sender frames every packet with.</summary>
        public const int HeaderBytes = 16 + Networking.PacketSender.FramingBytes;

        /// <summary>
        /// Serialized cost of one entry.
        ///
        /// Counted, not assumed: the prefab name goes out as a full string for
        /// every completed building in the colony, and it is most of the entry.
        /// "Tile" and "SuperInsulatedLiquidConduitBridge" are the same row here,
        /// so a batch has to close on accumulated bytes rather than on a count.
        /// </summary>
        public static int EntryBytes(in BuildingState b)
        {
            int name = string.IsNullOrEmpty(b.PrefabName)
                ? 1
                : System.Text.Encoding.UTF8.GetByteCount(b.PrefabName) + LengthPrefixBytes(b.PrefabName);

            return 4 + name;   // cell + prefab name
        }

        /// <summary>BinaryWriter writes a string length as a 7-bit encoded int.</summary>
        private static int LengthPrefixBytes(string s)
        {
            int len = System.Text.Encoding.UTF8.GetByteCount(s);
            int bytes = 1;
            while (len >= 0x80) { len >>= 7; bytes++; }
            return bytes;
        }

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();

            writer.Write(SweepId);
            writer.Write(BatchIndex);
            writer.Write(BatchCount);

            writer.Write(Buildings.Count);
            foreach (var b in Buildings)
            {
                writer.Write(b.Cell);
                writer.Write(b.PrefabName ?? string.Empty);
            }
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();

            SweepId = reader.ReadInt32();
            BatchIndex = reader.ReadInt32();
            BatchCount = reader.ReadInt32();

            int count = reader.ReadInt32();
            Buildings = new List<BuildingState>(count);

            for (int i = 0; i < count; i++)
            {
                Buildings.Add(new BuildingState
                {
                    Cell = reader.ReadInt32(),
                    PrefabName = reader.ReadString()
                });
            }
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (MultiplayerSession.IsHost)
                return;

            Components.BuildingSyncer.Instance?.OnPacketReceived(this);
        }
    }
}
