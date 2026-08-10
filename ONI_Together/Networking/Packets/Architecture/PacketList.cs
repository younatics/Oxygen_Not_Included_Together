using System.IO;
using ONI_Together.DebugTools;

namespace ONI_Together.Networking.Packets.Architecture
{
    /// <summary>
    /// Reading a collection length off the wire without trusting it.
    ///
    /// Nearly every list-carrying packet did the same thing: read an int and hand
    /// it straight to a List constructor as the capacity. The count is whatever
    /// arrived - a truncated packet, a reordered one, a byte flipped in transit -
    /// and the reader would allocate for it before discovering there was nothing
    /// to fill it with. That is the same defect the chunk reassembler had, where
    /// a header claiming a huge chunk count reserved the buffer for it, and it was
    /// fixed there and left everywhere else.
    ///
    /// The bound here is a sanity limit, not a batching rule. What a packet may
    /// carry is decided by whoever splits the sweep, against the transport's
    /// payload size; this only stops a corrupt number from costing memory, so it
    /// sits far above any legitimate batch and still far below anything harmful.
    /// A count that fails it is a malformed packet, and the caller gets an empty
    /// list rather than a partially filled one.
    /// </summary>
    public static class PacketList
    {
        /// <summary>
        /// Generous default. The strictest transport carries a thousand bytes
        /// unfragmented and even a two-byte entry cannot reach this, so nothing
        /// real is refused - chunked packets, which can legitimately be larger,
        /// pass their own limit.
        /// </summary>
        public const int SaneMax = 4096;

        private static long _rejected;

        /// <summary>How many malformed counts have been refused, for the diagnostics.</summary>
        public static long RejectedCount => _rejected;

        public static void ResetForNewSession() => _rejected = 0;

        /// <summary>
        /// Reads a length and returns it, or 0 if it cannot be believed.
        /// </summary>
        /// <param name="where">Packet and field, for the log line - a rejected
        /// count with no name tells you a packet was malformed and nothing about
        /// which one, and that gap has cost real time on this codebase.</param>
        public static int ReadCount(BinaryReader reader, string where, int max = SaneMax)
        {
            int count = reader.ReadInt32();
            if (count >= 0 && count <= max)
                return count;

            _rejected++;
            ThrottledLog.Warn($"[PacketList] {where} declared {count} entries (limit {max}); treating it as empty");
            return 0;
        }
    }
}
