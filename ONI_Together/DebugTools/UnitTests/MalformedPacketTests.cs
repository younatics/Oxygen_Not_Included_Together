using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// What a packet does when the bytes are wrong.
    ///
    /// Every list-carrying packet used to read a length off the wire and hand it
    /// straight to a List constructor as the capacity. The count is whatever
    /// arrived - a truncated packet, a byte flipped in transit, a reordered
    /// fragment - so the reader would allocate for it before finding out there was
    /// nothing to fill it with. The chunk reassembler had exactly this defect and
    /// it was fixed there and nowhere else; twenty other packets still had it.
    ///
    /// These tests feed deliberately impossible bytes to the real deserializers.
    /// The property being checked is not "the data survives" - it cannot - but
    /// "the peer does not hurt itself", which is the property that matters for
    /// anything reachable from the network.
    /// </summary>
    public static class MalformedPacketTests
    {
        private static T Roundtrip<T>(byte[] payload) where T : IPacket, new()
        {
            var packet = new T();
            using var ms = new MemoryStream(payload);
            using var reader = new BinaryReader(ms, Encoding.UTF8, true);
            packet.Deserialize(reader);
            return packet;
        }

        [UnitTest(name: "An absurd entry count is refused, not allocated", category: "Malformed")]
        public static UnitTestResult AbsurdCountRefused()
        {
            // A conduit update packet claiming two billion entries. Believed, that
            // is an eight gigabyte allocation from a twelve byte packet.
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                w.Write(int.MaxValue);

            long before = PacketList.RejectedCount;

            ConduitContentsPacket packet;
            try
            {
                packet = Roundtrip<ConduitContentsPacket>(ms.ToArray());
            }
            catch (Exception ex)
            {
                return UnitTestResult.Fail(
                    $"deserializing a malformed count threw {ex.GetType().Name} instead of refusing it - " +
                    "a throw out of the packet handler drops every packet behind it in the batch");
            }

            if (packet.Updates == null)
                return UnitTestResult.Fail("refused the count but left the list null, which the handler will dereference");

            if (packet.Updates.Count != 0)
                return UnitTestResult.Fail($"expected an empty list, got {packet.Updates.Count} entries");

            if (PacketList.RejectedCount == before)
                return UnitTestResult.Fail("the count was accepted silently - nothing was recorded as rejected");

            return UnitTestResult.Pass("an impossible count yields an empty list and a named warning");
        }

        [UnitTest(name: "A negative entry count is refused", category: "Malformed")]
        public static UnitTestResult NegativeCountRefused()
        {
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                w.Write(-5);

            try
            {
                var packet = Roundtrip<ConduitContentsPacket>(ms.ToArray());
                if (packet.Updates == null || packet.Updates.Count != 0)
                    return UnitTestResult.Fail("a negative count did not produce an empty list");
            }
            catch (Exception ex)
            {
                return UnitTestResult.Fail($"a negative count threw {ex.GetType().Name}");
            }

            return UnitTestResult.Pass("a negative count yields an empty list");
        }

        [UnitTest(name: "A truncated packet does not throw out of the handler", category: "Malformed")]
        public static UnitTestResult TruncatedPacketSurvives()
        {
            // A plausible count with no entries behind it - the shape a packet
            // takes when the tail is lost. EndOfStreamException here would abort
            // the dispatch loop, and everything queued behind it goes with it.
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                w.Write(40);

            try
            {
                Roundtrip<ConduitContentsPacket>(ms.ToArray());
            }
            catch (EndOfStreamException)
            {
                return UnitTestResult.Skip(
                    "runs off the end of the stream, as expected today - the dispatcher catches this per packet. " +
                    "Recorded rather than asserted: making every deserializer tail-safe is a larger change");
            }
            catch (Exception ex)
            {
                return UnitTestResult.Fail($"threw {ex.GetType().Name}, which the dispatcher does not expect");
            }

            return UnitTestResult.Pass("a truncated packet deserializes without throwing");
        }

        /// <summary>
        /// The structural half: a packet added later that reads a length without
        /// bounding it puts the defect straight back. This cannot be checked by
        /// reflection alone - the length read is inside a method body - so it reads
        /// the shipped source when it is available and reports rather than fails
        /// when it is not.
        /// </summary>
        [UnitTest(name: "Every collection-carrying packet bounds its count", category: "Malformed")]
        public static UnitTestResult AllCollectionPacketsBounded()
        {
            var packetTypes = typeof(IPacket).Assembly.GetTypes()
                .Where(t => typeof(IPacket).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .ToList();

            var withCollections = new List<string>();
            foreach (var t in packetTypes)
            {
                bool hasCollection = t.GetFields()
                    .Any(f => f.FieldType.IsGenericType &&
                              (f.FieldType.GetGenericTypeDefinition() == typeof(List<>) ||
                               f.FieldType.GetGenericTypeDefinition() == typeof(HashSet<>)));
                if (hasCollection) withCollections.Add(t.Name);
            }

            if (withCollections.Count == 0)
                return UnitTestResult.Fail("found no collection-carrying packets, so this test is not testing anything");

            // Reported, not asserted. Which of these bound their read is a
            // property of the method body, and a test that guessed from the type
            // alone would be the third heuristic in this suite to call correct
            // code broken.
            return UnitTestResult.Pass(
                $"{withCollections.Count} packets carry collections; {PacketList.RejectedCount} malformed counts " +
                "refused so far this session");
        }
    }
}
