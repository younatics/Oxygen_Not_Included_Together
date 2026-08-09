using System;
using System.IO;
using System.Text;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// What the chunking path does when the network misbehaves.
    ///
    /// The existing coverage (NetworkingTests.AutoChunkingWorks) splits a
    /// payload and puts it back together in order, complete and unduplicated -
    /// which is the one case unreliable UDP guarantees you will not always get.
    /// Every packet over the Riptide limit takes this path, and after the
    /// conduit fix that is still every save transfer and every world snapshot.
    ///
    /// These drive ChunkedPacket.OnDispatched directly. Nothing here completes a
    /// set except the reordering case, which reassembles a real TestPacket so
    /// the dispatch at the end is harmless and its logged ClientID proves the
    /// bytes came back intact.
    /// </summary>
    public static class ChunkingTests
    {
        private const ulong Marker = 0xFEEDFACECAFEBEEFUL;

        private static byte[] SerializedTestPacket()
            => PacketSender.SerializePacketForSending(new Tests.TestPacket { ClientID = Marker });

        private static ChunkedPacket Chunk(int sequence, int index, int total, byte[] data)
            => new ChunkedPacket
            {
                SequenceId = sequence,
                ChunkIndex = index,
                TotalChunks = total,
                ChunkData = data
            };

        private static byte[][] Split(byte[] payload, int parts)
        {
            var result = new byte[parts][];
            int size = (payload.Length + parts - 1) / parts;
            for (int i = 0; i < parts; i++)
            {
                int offset = i * size;
                int length = Math.Min(size, payload.Length - offset);
                if (length < 0) length = 0;
                result[i] = new byte[length];
                Array.Copy(payload, offset, result[i], 0, length);
            }
            return result;
        }

        [UnitTest(name: "Chunks arriving out of order still reassemble", category: "Chunking")]
        public static UnitTestResult OutOfOrderReassembles()
        {
            ChunkedPacket.ResetPending();
            try
            {
                var parts = Split(SerializedTestPacket(), 3);
                const int seq = 90001;

                // Reverse order. UDP gives no ordering guarantee.
                Chunk(seq, 2, 3, parts[2]).OnDispatched();
                Chunk(seq, 0, 3, parts[0]).OnDispatched();
                if (ChunkedPacket.PendingSetCount != 1)
                    return UnitTestResult.Fail($"expected 1 pending set while incomplete, got {ChunkedPacket.PendingSetCount}");

                Chunk(seq, 1, 3, parts[1]).OnDispatched();

                if (ChunkedPacket.PendingSetCount != 0)
                    return UnitTestResult.Fail("set did not complete after every chunk arrived");

                return UnitTestResult.Pass("reversed arrival still reassembled and dispatched");
            }
            catch (Exception ex)
            {
                return UnitTestResult.Fail($"threw on out-of-order arrival: {ex.GetType().Name}: {ex.Message}");
            }
            finally { ChunkedPacket.ResetPending(); }
        }

        [UnitTest(name: "A duplicated chunk does not corrupt the set", category: "Chunking")]
        public static UnitTestResult DuplicateChunkIsHarmless()
        {
            ChunkedPacket.ResetPending();
            try
            {
                var parts = Split(SerializedTestPacket(), 3);
                const int seq = 90002;

                Chunk(seq, 0, 3, parts[0]).OnDispatched();
                Chunk(seq, 0, 3, parts[0]).OnDispatched();   // retransmit
                Chunk(seq, 1, 3, parts[1]).OnDispatched();
                Chunk(seq, 2, 3, parts[2]).OnDispatched();

                if (ChunkedPacket.PendingSetCount != 0)
                    return UnitTestResult.Fail("set did not complete when a chunk was duplicated");

                return UnitTestResult.Pass("duplicate chunk absorbed");
            }
            catch (Exception ex)
            {
                return UnitTestResult.Fail($"threw on duplicate chunk: {ex.GetType().Name}: {ex.Message}");
            }
            finally { ChunkedPacket.ResetPending(); }
        }

        [UnitTest(name: "Two senders using the same sequence stay separate", category: "Chunking")]
        public static UnitTestResult ConcurrentSendersDoNotSplice()
        {
            ChunkedPacket.ResetPending();
            try
            {
                // _nextSequenceId starts at 0 on every peer, so two clients
                // sending to the host both produce sequence 0, 1, 2 ... The host
                // keys pending sets by sequence id alone, with no sender, so the
                // sets land on top of each other.
                var a = Split(SerializedTestPacket(), 3);
                var b = Split(SerializedTestPacket(), 3);
                const int seq = 90003;

                Chunk(seq, 0, 3, a[0]).OnDispatched();
                Chunk(seq, 1, 3, a[1]).OnDispatched();
                Chunk(seq, 0, 3, b[0]).OnDispatched();      // different sender, same sequence

                int pending = ChunkedPacket.PendingSetCount;
                if (pending < 2)
                    return UnitTestResult.Fail(
                        $"two senders' sets collapsed into {pending} buffer(s). Sender B's chunk 0 overwrote " +
                        "sender A's, so when A's last chunk arrives the set counts as complete and a payload " +
                        "spliced from both is dispatched as if it were one message.");

                return UnitTestResult.Pass($"{pending} independent sets tracked");
            }
            catch (Exception ex)
            {
                return UnitTestResult.Fail($"threw on concurrent senders: {ex.GetType().Name}: {ex.Message}");
            }
            finally { ChunkedPacket.ResetPending(); }
        }

        [UnitTest(name: "A chunk index past the end is rejected, not thrown", category: "Chunking")]
        public static UnitTestResult OutOfRangeIndexIsRejected()
        {
            ChunkedPacket.ResetPending();
            try
            {
                // The buffer is sized from whichever chunk arrived first, and
                // ChunkIndex is used to index it with no bounds check. A colliding
                // sequence or a corrupt header is enough to walk off the end.
                const int seq = 90004;
                Chunk(seq, 0, 2, new byte[4]).OnDispatched();
                Chunk(seq, 4, 5, new byte[4]).OnDispatched();

                return UnitTestResult.Pass("out-of-range index was ignored");
            }
            catch (Exception ex)
            {
                return UnitTestResult.Fail(
                    $"threw instead of rejecting: {ex.GetType().Name}. A malformed or colliding chunk header " +
                    "escapes as an exception through the dispatch path, which is where the client's " +
                    "\"Failed to handle packet\" warnings come from.");
            }
            finally { ChunkedPacket.ResetPending(); }
        }

        [UnitTest(name: "An abandoned set does not leak forever", category: "Chunking")]
        public static UnitTestResult IncompleteSetIsEvicted()
        {
            ChunkedPacket.ResetPending();
            try
            {
                // Unreliable chunks are lost routinely. Nothing ever removes a
                // set that never completes, so each loss costs one buffer for the
                // rest of the session.
                for (int i = 0; i < 64; i++)
                    Chunk(95000 + i, 0, 4, new byte[64]).OnDispatched();

                int pending = ChunkedPacket.PendingSetCount;
                if (pending >= 64)
                    return UnitTestResult.Fail(
                        $"{pending} abandoned sets are still held with no eviction policy. Every lost chunk " +
                        "leaks a buffer for the life of the session, and the payload never completes.");

                return UnitTestResult.Pass($"{pending} of 64 abandoned sets retained");
            }
            catch (Exception ex)
            {
                return UnitTestResult.Fail($"threw while abandoning sets: {ex.GetType().Name}: {ex.Message}");
            }
            finally { ChunkedPacket.ResetPending(); }
        }

        [UnitTest(name: "An absurd TotalChunks is refused", category: "Chunking")]
        public static UnitTestResult AbsurdTotalChunksRefused()
        {
            ChunkedPacket.ResetPending();
            try
            {
                // Deserialize trusts TotalChunks from the wire and OnDispatched
                // allocates byte[TotalChunks][] from it. WorldDataPacket and
                // InstantiationsPacket already bound their equivalents; this path
                // never got the same guard. 100k is used rather than int.MaxValue
                // so an unguarded build survives to report the failure.
                const int absurd = 100_000;
                Chunk(90005, 0, absurd, new byte[8]).OnDispatched();

                if (ChunkedPacket.PendingSetCount > 0)
                    return UnitTestResult.Fail(
                        $"a header claiming {absurd} chunks was accepted and allocated. The count is never " +
                        "checked against a ceiling, so a corrupt or hostile header sizes an array for us.");

                return UnitTestResult.Pass("absurd chunk count refused");
            }
            catch (Exception ex)
            {
                return UnitTestResult.Fail($"threw instead of refusing: {ex.GetType().Name}: {ex.Message}");
            }
            finally { ChunkedPacket.ResetPending(); }
        }
    }
}
