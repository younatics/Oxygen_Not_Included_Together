using System;
using System.Collections.Generic;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Animation;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Transport;
using ONI_Together.Networking.Transport.Lan;
using ONI_Together.Networking.Transport.Steam;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Does a periodic packet survive the wire intact?
    ///
    /// Nothing measured a serialized packet against what the transport will
    /// actually carry, and both transports punish going over - differently, so
    /// neither is the safe one:
    ///
    ///   Riptide  splits anything over 1000 B into ChunkedPackets, inheriting
    ///            the caller's send mode. An unreliable batch becomes unreliable
    ///            chunks, and losing one destroys the whole payload with no
    ///            retransmit.
    ///   Steam    fragments an unreliable message past roughly one MTU and drops
    ///            the entire message if any fragment is lost, and refuses
    ///            anything over 512 KB outright - with the failure log commented
    ///            out, so it vanishes silently.
    ///
    /// A packet is built once and sent over whichever transport is active, so it
    /// has to fit the strictest limit, not the one its author had in mind.
    /// These run on one box in milliseconds - no session, no colony, no peer.
    /// </summary>
    public static class PacketSizeTests
    {
        private static int SerializedSize(IPacket packet)
            => PacketSender.SerializePacketForSending(packet).Length;

        private static string LimitTable(int bytes)
        {
            return $"{bytes} B " +
                   $"(riptide {RiptidePacketSender.MAX_PAYLOAD_BYTES}, " +
                   $"steam-unfragmented {SteamworksPacketSender.STEAM_UNRELIABLE_MTU_BYTES}, " +
                   $"strictest {TransportPacketSender.StrictestUnfragmentedPayloadBytes})";
        }

        private static ConduitContentsPacket BuildConduitPacket(int updates)
        {
            var packet = new ConduitContentsPacket();
            for (int i = 0; i < updates; i++)
            {
                packet.Updates.Add(new ConduitCellUpdate
                {
                    Cell = 100000 + i,
                    ConduitType = ConduitContentsPacket.CONDUIT_LIQUID,
                    Element = int.MaxValue,
                    Mass = 1234.5f,
                    Temperature = 320.15f,
                    DiseaseIdx = 3,
                    DiseaseCount = int.MaxValue,
                });
            }
            return packet;
        }

        [UnitTest(name: "Conduit batch fits every transport", category: "PacketSize")]
        public static UnitTestResult ConduitBatchFits()
        {
            int size = SerializedSize(BuildConduitPacket(ConduitFlowSyncer.MAX_UPDATES_PER_PACKET));
            int limit = TransportPacketSender.StrictestUnfragmentedPayloadBytes;

            if (size > limit)
            {
                return UnitTestResult.Fail(
                    $"a full batch of {ConduitFlowSyncer.MAX_UPDATES_PER_PACKET} conduit updates " +
                    $"serializes to {LimitTable(size)}. ConduitFlowSyncer sends these Unreliable, so " +
                    "over Riptide every periodic pipe update is split into unreliable chunks and one " +
                    "lost chunk discards the batch permanently - pipes desync and never recover.");
            }

            return UnitTestResult.Pass($"full conduit batch is {LimitTable(size)}");
        }

        [UnitTest(name: "Conduit batch cap is derived, not hardcoded", category: "PacketSize")]
        public static UnitTestResult ConduitCapIsDerived()
        {
            // Find the largest batch that still fits, and compare it to the cap
            // the syncer actually uses. If the cap was picked for one transport
            // it will sit above what the strictest one carries.
            int limit = TransportPacketSender.StrictestUnfragmentedPayloadBytes;
            int fits = 0;
            for (int n = 1; n <= 512; n++)
            {
                if (SerializedSize(BuildConduitPacket(n)) > limit) break;
                fits = n;
            }

            if (ConduitFlowSyncer.MAX_UPDATES_PER_PACKET > fits)
            {
                return UnitTestResult.Fail(
                    $"MAX_UPDATES_PER_PACKET is {ConduitFlowSyncer.MAX_UPDATES_PER_PACKET} but only {fits} " +
                    $"updates fit in {limit} B. The cap has to come from the transport's limit, not from a " +
                    "constant chosen for one of them.");
            }

            return UnitTestResult.Pass(
                $"cap {ConduitFlowSyncer.MAX_UPDATES_PER_PACKET} <= {fits} that fit in {limit} B");
        }

        /// <summary>
        /// A colony's worth of buildings, with names long enough to be honest
        /// about the cost. The prefab name is most of an entry, so a batch cap
        /// expressed as a count is wrong for whatever happens to be built.
        /// </summary>
        private static List<BuildingState> BuildBuildingStates(int count)
        {
            var list = new List<BuildingState>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(new BuildingState
                {
                    Cell = 100000 + i,
                    // A real prefab name from the long end of the range - the
                    // batcher has to hold for these, not for "Tile".
                    PrefabName = (i % 3 == 0) ? "Tile"
                               : (i % 3 == 1) ? "LiquidConditionerComplete"
                                              : "SuperInsulatedLiquidConduitBridge"
                });
            }
            return list;
        }

        [UnitTest(name: "Every building batch fits every transport", category: "PacketSize")]
        public static UnitTestResult BuildingBatchesFit()
        {
            // 4000 completed buildings is an ordinary mature colony, and this
            // packet used to carry all of them in one message every 30 s. It ran
            // to tens of kilobytes against a 1000 B payload, and because
            // SendChunked always sends Reliable, the result was not a fragmented
            // unreliable packet but a reliable burst of a hundred-odd chunks.
            var all = BuildBuildingStates(4000);
            var batches = SweepBatcher.Split(all, BuildingStatePacket.HeaderBytes,
                                             b => BuildingStatePacket.EntryBytes(b));

            int limit = TransportPacketSender.StrictestUnfragmentedPayloadBytes;
            int total = 0;
            for (int i = 0; i < batches.Count; i++)
            {
                int size = SerializedSize(new BuildingStatePacket
                {
                    SweepId = 7,
                    BatchIndex = i,
                    BatchCount = batches.Count,
                    Buildings = batches[i]
                });
                total += batches[i].Count;

                if (size > limit)
                {
                    return UnitTestResult.Fail(
                        $"batch {i} of {batches.Count} holds {batches[i].Count} buildings and serializes to " +
                        $"{LimitTable(size)}. The byte budget is understated - check " +
                        "BuildingStatePacket.HeaderBytes against the sweep fields and the sender's framing.");
                }
            }

            // Splitting must not lose anyone. The receiver treats absence as a
            // no-op, so a dropped entry would not delete a building - it would
            // just never spawn one the host has, which is silent.
            if (total != all.Count)
                return UnitTestResult.Fail($"split {all.Count} buildings into batches holding {total}");

            return UnitTestResult.Pass(
                $"{all.Count} buildings -> {batches.Count} batches, each within {limit} B");
        }

        [UnitTest(name: "Every status item batch fits every transport", category: "PacketSize")]
        public static UnitTestResult StatusItemBatchesFit()
        {
            // Tooltips as they really are: a rendered sentence with runtime
            // numbers in it. The old cap of 64 entries assumed a fixed width and
            // there is none - a stressed duplicant with a dozen status items was
            // sending several kilobytes twice a second.
            var entries = new List<StatusItemEntry>();
            for (int i = 0; i < 64; i++)
            {
                entries.Add(new StatusItemEntry
                {
                    ItemId = "Stressed",
                    CategoryId = "Status",
                    DisplayName = "Stressed",
                    Tooltip = "Stress is at 42.3% and rising by 0.4% per cycle. " +
                              "This duplicant will have a stress reaction at 100%. " +
                              "Recent causes: Sopping Wet, Dark, Unhygienic Surroundings."
                });
            }

            var batches = SweepBatcher.Split(entries, StatusItemsPacket.HeaderBytes, e => e.Bytes());
            int limit = TransportPacketSender.StrictestUnfragmentedPayloadBytes;
            int total = 0;

            for (int i = 0; i < batches.Count; i++)
            {
                int size = SerializedSize(new StatusItemsPacket
                {
                    DupeNetId = 1234,
                    SweepId = 3,
                    BatchIndex = i,
                    BatchCount = batches.Count,
                    Entries = batches[i]
                });
                total += batches[i].Count;

                if (size > limit)
                {
                    return UnitTestResult.Fail(
                        $"batch {i} of {batches.Count} holds {batches[i].Count} status items and serializes " +
                        $"to {LimitTable(size)}. StatusBroadcaster sends these Unreliable, and SendChunked " +
                        "always sends Reliable - so going over does not fragment, it converts a cosmetic " +
                        "twice-a-second broadcast into a reliable chunk burst per entity.");
                }
            }

            if (total != entries.Count)
                return UnitTestResult.Fail($"split {entries.Count} status items into batches holding {total}");

            // MaxEntries must not be the thing doing the limiting: if a batch
            // ever reaches it, the byte budget stopped being what decides.
            foreach (var batch in batches)
            {
                if (batch.Count >= StatusItemsPacket.MaxEntries)
                    return UnitTestResult.Fail(
                        $"a batch reached the {StatusItemsPacket.MaxEntries}-entry sanity bound; the byte " +
                        "budget should always close a batch first");
            }

            return UnitTestResult.Pass(
                $"{entries.Count} status items -> {batches.Count} batches, each within {limit} B");
        }

        [UnitTest(name: "An empty sweep still produces one batch", category: "PacketSize")]
        public static UnitTestResult EmptySweepIsStillSent()
        {
            // A receiver that reconciles by absence needs to hear "nothing", and
            // a batcher that returned no batches for an empty snapshot would
            // leave the previous one standing forever.
            var batches = SweepBatcher.Split(new List<BuildingState>(),
                                             BuildingStatePacket.HeaderBytes,
                                             b => BuildingStatePacket.EntryBytes(b));

            if (batches.Count != 1 || batches[0].Count != 0)
                return UnitTestResult.Fail($"empty snapshot produced {batches.Count} batches");

            return UnitTestResult.Pass("an empty snapshot is one empty batch");
        }

        [UnitTest(name: "Anim resync request fits every transport", category: "PacketSize")]
        public static UnitTestResult AnimResyncRequestFits()
        {
            // AnimResyncRequester caps at 64 NetIds per packet and says why:
            // "Unreliable UDP fragments silently when the payload exceeds MTU."
            // Confirm the number actually delivers on that.
            var packet = new AnimResyncRequestPacket();
            var ids = new int[64];
            for (int i = 0; i < ids.Length; i++) ids[i] = int.MinValue + i;
            packet.NetIds = ids;

            int size = SerializedSize(packet);
            int limit = TransportPacketSender.StrictestUnfragmentedPayloadBytes;

            if (size > limit)
                return UnitTestResult.Fail($"64 NetIds serialize to {LimitTable(size)}");

            return UnitTestResult.Pass($"64 NetIds is {LimitTable(size)}");
        }

        [UnitTest(name: "Every transport can carry the largest declared packet", category: "PacketSize")]
        public static UnitTestResult DeclaredCeilingsAreDeliverable()
        {
            // These caps bound a hostile or corrupt payload, but they are also a
            // statement about how large a packet may legitimately get. Splitting
            // now lives in the shared sender, so both transports have to be able
            // to carry that much - it used to live only in the Riptide one, and
            // the same packet was delivered there and refused over Steam.
            var declared = new (string name, int bytes)[]
            {
                ("WorldDataPacket.MaxCompressedBytes", 32 * 1024 * 1024),
                ("InstantiationsPacket.MaxCompressedBytes", 16 * 1024 * 1024),
            };

            var transports = new (string name, int unfragmented)[]
            {
                ("riptide", RiptidePacketSender.MAX_PAYLOAD_BYTES),
                ("steam", SteamworksPacketSender.STEAM_UNRELIABLE_MTU_BYTES),
            };

            var problems = new List<string>();
            foreach (var (tname, unfragmented) in transports)
            {
                long capacity = (long)ChunkedPacket.MaxChunks * (unfragmented - ChunkedPacket.HeaderOverheadBytes);
                foreach (var (pname, bytes) in declared)
                {
                    if (bytes > capacity)
                        problems.Add($"{pname}={bytes / 1024 / 1024} MB over {tname} capacity {capacity / 1024 / 1024} MB");
                }
            }

            if (problems.Count > 0)
            {
                return UnitTestResult.Fail(
                    string.Join("; ", problems) +
                    $". Capacity is MaxChunks ({ChunkedPacket.MaxChunks}) times the per-chunk payload, so a " +
                    "declared ceiling above it cannot be delivered on that transport however it is split.");
            }

            long minCapacity = long.MaxValue;
            foreach (var (_, unfragmented) in transports)
            {
                long c = (long)ChunkedPacket.MaxChunks * (unfragmented - ChunkedPacket.HeaderOverheadBytes);
                if (c < minCapacity) minCapacity = c;
            }
            return UnitTestResult.Pass(
                $"every declared ceiling fits the smallest transport capacity ({minCapacity / 1024 / 1024} MB)");
        }
    }
}
