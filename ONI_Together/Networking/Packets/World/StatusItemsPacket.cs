using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using System;
using System.Collections.Generic;
using System.IO;

namespace ONI_Together.Networking.Packets.World
{
    public class StatusItemsPacket : IPacket
    {
        /// <summary>
        /// Sanity bound on a single batch, not the batching rule.
        ///
        /// This used to be the only cap, and a count cannot bound a packet whose
        /// entries are four strings each - one of them a rendered tooltip. Sixty
        /// four entries is several kilobytes against a 1000 byte payload; the
        /// real limit is the byte budget the sender splits on, which leaves
        /// about four to eleven entries per batch. This only stops a corrupt
        /// count from allocating.
        /// </summary>
        public const int MaxEntries = 64;

        public int DupeNetId;
        public List<StatusItemEntry> Entries = new();

        /// <summary>
        /// Which sweep this batch belongs to, and where in it.
        ///
        /// The receiver clears every status item on the entity and rebuilds from
        /// the list, so a snapshot is indivisible: applying one batch of three
        /// would delete the items carried by the other two. Nothing is applied
        /// until the sweep is whole.
        /// </summary>
        public int SweepId;
        public int BatchIndex;
        public int BatchCount = 1;

        /// <summary>Net id, count, sweep header, and the sender's framing.</summary>
        public const int HeaderBytes = 20 + Networking.PacketSender.FramingBytes;

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();
            writer.Write(DupeNetId);
            writer.Write(SweepId);
            writer.Write(BatchIndex);
            writer.Write(BatchCount);
            int count = Math.Min(Entries.Count, MaxEntries);
            writer.Write(count);
            for (int i = 0; i < count; i++)
                Entries[i].Serialize(writer);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();
            DupeNetId = reader.ReadInt32();
            SweepId = reader.ReadInt32();
            BatchIndex = reader.ReadInt32();
            BatchCount = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxEntries)
            {
                Entries = new List<StatusItemEntry>();
                return;
            }
            Entries = new List<StatusItemEntry>(count);
            for (int i = 0; i < count; i++)
                Entries.Add(StatusItemEntry.Deserialize(reader));
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();
            if (!MultiplayerSession.IsClient)
                return;
            if (!NetworkIdentityRegistry.TryGet(DupeNetId, out var entity))
                return;
            if (!entity.TryGetComponent<ClientReceiver_StatusItems>(out var receiver))
                return;

            // Per entity, because the sweep is per entity: each one clears and
            // rebuilds only its own status items, so two entities' sweeps
            // interleave freely and must not share assembly state.
            receiver.AcceptBatch(SweepId, BatchIndex, BatchCount, Entries);
        }
    }
}
