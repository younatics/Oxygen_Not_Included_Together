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
