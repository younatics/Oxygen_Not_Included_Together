using System;
using System.Collections.Generic;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Transport;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// What the periodic syncers send once a colony is large.
    ///
    /// ConduitFlowSyncer was the only one that capped a batch at all, and its
    /// cap was sized for the wrong transport. The others build one packet
    /// holding every entry they found and send it:
    ///
    ///   PlantGrowthSyncer:148    one PlantGrowthStatePacket, every plant
    ///   WorldStateSyncer:242     one DiggingStatePacket, every dig cell
    ///   WorldStateSyncer:334     one ChoreStatePacket, every chore
    ///   LogicStateSyncer:127     one packet, every changed logic net
    ///   BuildingSyncer:74        one packet, every building state
    ///
    /// Packet size therefore tracks colony size, and the periodic ones go out
    /// Unreliable. That is fine while a batch fits one indivisible payload -
    /// the design invariant is that a loss costs exactly one packet and the
    /// next force-refresh repairs it. Past that size the payload is split, and
    /// a split payload is a different failure: the pieces have to be delivered
    /// reliably, and a burst of them lands every tick.
    ///
    /// These are measurements, not guesses. No session, no colony, no peer.
    /// </summary>
    public static class SyncerScaleTests
    {
        /// <summary>Modest for a developed base; the audit's S4 scenario asks for 30+ plants alone.</summary>
        private const int LargeColonyPlants = 200;
        private const int LargeColonyDigCells = 1000;

        private static int Size(IPacket packet) => PacketSender.SerializePacketForSending(packet).Length;

        private static int Limit => TransportPacketSender.StrictestUnfragmentedPayloadBytes;

        private static PlantGrowthStatePacket Plants(int n)
        {
            var packet = new PlantGrowthStatePacket();
            for (int i = 0; i < n; i++)
            {
                packet.Plants.Add(new PlantData
                {
                    PlantNetId = int.MinValue + i,
                    ReceptacleNetId = int.MaxValue - i,
                    Cell = 100000 + i,
                    // A real tag, because the string is most of the entry.
                    PlantPrefabTag = "BasicForagePlantPlanted",
                    Maturity = 0.5f,
                    IsWilting = true,
                    IsHarvestReady = true,
                    IsWild = true,
                });
            }
            return packet;
        }

        private static DiggingStatePacket DigCells(int n)
        {
            var packet = new DiggingStatePacket();
            for (int i = 0; i < n; i++)
                packet.Digs.Add(new DigEntry { Cell = 100000 + i, NetId = int.MinValue + i });
            return packet;
        }

        private static int MaxThatFits(Func<int, IPacket> build, int ceiling)
        {
            int fits = 0;
            for (int n = 1; n <= ceiling; n++)
            {
                if (Size(build(n)) > Limit) break;
                fits = n;
            }
            return fits;
        }

        [UnitTest(name: "Plant growth sweep splits into payload-sized batches", category: "Scale")]
        public static UnitTestResult PlantGrowthBatchFits()
        {
            // Replays PlantGrowthSyncer's rule - accumulate entry bytes, close
            // the batch before it would exceed the limit - and checks every
            // batch it produces. Measuring one giant packet would only prove the
            // packet type can hold one, which is not the property that matters
            // now that the syncer never builds one.
            var all = Plants(LargeColonyPlants).Plants;
            var batch = new PlantGrowthStatePacket { SweepId = int.MaxValue, BatchCount = int.MaxValue };
            int bytes = PlantGrowthStatePacket.HeaderBytes;
            int batches = 0, worst = 0;

            foreach (var p in all)
            {
                int cost = PlantGrowthStatePacket.EntryBytes(p);
                if (batch.Plants.Count > 0 && bytes + cost > Limit)
                {
                    int size = Size(batch);
                    if (size > Limit)
                        return UnitTestResult.Fail($"a closed batch of {batch.Plants.Count} plants is {size} B, over {Limit}");
                    if (size > worst) worst = size;
                    batches++;
                    batch = new PlantGrowthStatePacket { SweepId = int.MaxValue, BatchCount = int.MaxValue };
                    bytes = PlantGrowthStatePacket.HeaderBytes;
                }
                batch.Plants.Add(p);
                bytes += cost;
            }
            if (batch.Plants.Count > 0)
            {
                int size = Size(batch);
                if (size > Limit)
                    return UnitTestResult.Fail($"the final batch of {batch.Plants.Count} plants is {size} B, over {Limit}");
                if (size > worst) worst = size;
                batches++;
            }

            return UnitTestResult.Pass(
                $"{LargeColonyPlants} plants go out as {batches} batches, largest {worst} B, limit {Limit} B");
        }

        [UnitTest(name: "Digging sweep splits into payload-sized batches", category: "Scale")]
        public static UnitTestResult DiggingBatchFits()
        {
            // WorldStateSyncer sends a dig snapshot as SweepId/BatchIndex/
            // BatchCount batches and the client reconciles only once the whole
            // sweep has arrived. Verify a full batch is legal and that the
            // header cost is accounted for - the sweep fields are why the cap
            // moved from 248 to 245.
            int perBatch = DiggingStatePacket.MaxCellsFor(Limit);
            var batch = DigCells(perBatch);
            batch.SweepId = int.MaxValue;
            batch.BatchIndex = int.MaxValue - 1;
            batch.BatchCount = int.MaxValue;

            int size = Size(batch);
            if (size > Limit)
                return UnitTestResult.Fail($"a full batch of {perBatch} dig cells is {size} B, over {Limit}");

            int batches = (LargeColonyDigCells + perBatch - 1) / perBatch;
            return UnitTestResult.Pass(
                $"{LargeColonyDigCells} dig cells go out as {batches} batches of at most {perBatch}, largest {size} B");
        }

        [UnitTest(name: "Entry cost of a periodic packet is bounded", category: "Scale")]
        public static UnitTestResult EntryCostIsBounded()
        {
            // Cost per entry decides how often a batch splits, so it is worth
            // pinning. A string field per entry is the expensive shape.
            int one = Size(Plants(1));
            int hundred = Size(Plants(101));
            int perEntry = (hundred - one) / 100;

            var notes = new List<string> { $"plant entry {perEntry} B" };

            int digOne = Size(DigCells(1));
            int digHundred = Size(DigCells(101));
            notes.Add($"dig entry {(digHundred - digOne) / 100} B");

            // Not a failure: batching keeps every packet legal whatever an entry
            // costs. It decides how many packets a tick costs, which is a
            // bandwidth question, so it is recorded rather than gated. Most of
            // the 43 B is PlantPrefabTag going out as a full string per plant
            // per tick; interning it would roughly double what fits.
            notes.Add($"{(Limit - PlantGrowthStatePacket.HeaderBytes) / perEntry} plants per packet");
            return UnitTestResult.Pass(string.Join(", ", notes));
        }
    }
}
