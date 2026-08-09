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
            for (int i = 0; i < n; i++) packet.DigCells.Add(100000 + i);
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

        [UnitTest(name: "Plant growth batch fits one payload at colony scale", category: "Scale")]
        public static UnitTestResult PlantGrowthBatchFits()
        {
            int fits = MaxThatFits(n => Plants(n), 512);
            int actual = Size(Plants(LargeColonyPlants));

            if (actual > Limit)
            {
                return UnitTestResult.Fail(
                    $"{LargeColonyPlants} plants serialize to {actual} B against a {Limit} B limit - only {fits} " +
                    "fit. PlantGrowthSyncer has no batch cap, so the packet grows with the colony and is sent " +
                    $"Unreliable; past {fits} plants every periodic update is split. This is the 25-plant cliff " +
                    "the audit recorded as S4, and the number falls out of the entry layout: the prefab tag is " +
                    "sent as a string on every plant, every tick.");
            }

            return UnitTestResult.Pass($"{LargeColonyPlants} plants is {actual} B; {fits} fit in {Limit} B");
        }

        [UnitTest(name: "Digging batch fits one payload at colony scale", category: "Scale")]
        public static UnitTestResult DiggingBatchFits()
        {
            int fits = MaxThatFits(n => DigCells(n), 4096);
            int actual = Size(DigCells(LargeColonyDigCells));

            if (actual > Limit)
            {
                return UnitTestResult.Fail(
                    $"{LargeColonyDigCells} dig cells serialize to {actual} B against a {Limit} B limit - only " +
                    $"{fits} fit. WorldStateSyncer sends every outstanding dig in one Unreliable packet, so a " +
                    "large dig order splits every tick.");
            }

            return UnitTestResult.Pass($"{LargeColonyDigCells} dig cells is {actual} B; {fits} fit in {Limit} B");
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

            if (perEntry > 32)
            {
                return UnitTestResult.Fail(
                    $"a plant costs {perEntry} B per entry, so only {(Limit - 8) / perEntry} fit one payload. " +
                    "PlantPrefabTag is written as a full string on every plant on every tick; interning it to an " +
                    "id would cut the entry to under 20 B and roughly double what fits.");
            }

            return UnitTestResult.Pass(string.Join(", ", notes));
        }
    }
}
