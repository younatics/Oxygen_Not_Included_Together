using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using UnityEngine;

namespace ONI_Together.Networking.Transport
{
    public abstract class TransportPacketSender
    {
        private readonly Dictionary<object, Queue<(IPacket packet, PacketSendMode sendMode)>> _pendingQueues = new Dictionary<object, Queue<(IPacket packet, PacketSendMode sendMode)>>();
        private readonly List<object> _emptyConnections = new List<object>();

        public bool SendToConnection(object conn, IPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate)
        {
            // This does work but if it queues up the client will see what the host saw but will be behind and plays catchup
            if (!Configuration.Instance.EnablePacketQueue)
                return SendPacket(conn, packet, sendType);
            // queue it
            if (!_pendingQueues.TryGetValue(conn, out var queue))
                _pendingQueues[conn] = queue = new();

            // Given the nature of the game and the sync. I'm not sure this is a good idea for late game colonies
            //int MAX_QUEUE_DEPTH = 1000; // After 1000 packets. Discard oldest
            //if (queue.Count >= MAX_QUEUE_DEPTH)
            //    queue.Dequeue();

            queue.Enqueue((packet, sendType));
            return true;
        }

        public void Flush()
        {
            if (!Configuration.Instance.EnablePacketQueue)
                return;

            int maxThisTick = (int)(Configuration.Instance.MaxPacketsPerSecond * Time.unscaledDeltaTime);
            //maxThisTick = Mathf.Clamp(maxThisTick, 1, 60); // never more than 60 per frame
            if (maxThisTick < 1) maxThisTick = 1;

            _emptyConnections.Clear();
            foreach (var kvp in _pendingQueues)
            {
                int sent = 0;
                while (kvp.Value.Count > 0 && sent < maxThisTick)
                {
                    var (packet, sendType) = kvp.Value.Dequeue();
                    SendPacket(kvp.Key, packet, sendType);
                    sent++;
                }
                if (kvp.Value.Count == 0)
                    _emptyConnections.Add(kvp.Key);
            }

            foreach (var key in _emptyConnections)
                _pendingQueues.Remove(key);
        }

        /// <summary>
        /// Serialize, split if the payload will not survive as one piece, and
        /// hand the bytes to the transport.
        ///
        /// Splitting used to live in RiptidePacketSender alone, so the same
        /// packet was delivered over Riptide and refused over Steam. A packet is
        /// written once and sent over whichever transport is active, so where it
        /// gets split cannot be one transport's private business.
        /// </summary>
        public bool SendPacket(object conn, IPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate)
        {
            byte[] bytes = PacketSender.SerializePacketForSending(packet);

            // A chunk is already sized to fit; splitting it again would recurse.
            if (bytes.Length <= MaxUnfragmentedPayloadBytes || packet is ChunkedPacket)
                return SendSerialized(conn, bytes, packet, sendType);

            return SendChunked(conn, bytes, sendType);
        }

        private bool SendChunked(object conn, byte[] fullData, PacketSendMode sendType)
        {
            int chunkDataSize = MaxUnfragmentedPayloadBytes - ChunkedPacket.HeaderOverheadBytes;
            int totalChunks = (fullData.Length + chunkDataSize - 1) / chunkDataSize;

            if (totalChunks > ChunkedPacket.MaxChunks)
            {
                DebugConsole.LogError(
                    $"[Transport] refusing a {fullData.Length} B payload: it needs {totalChunks} chunks and the " +
                    $"reassembler accepts at most {ChunkedPacket.MaxChunks}.", false);
                return false;
            }

            int sequenceId = ChunkedPacket.GetNextSequenceId();
            bool allSent = true;

            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * chunkDataSize;
                int length = System.Math.Min(chunkDataSize, fullData.Length - offset);
                byte[] chunkData = new byte[length];
                System.Array.Copy(fullData, offset, chunkData, 0, length);

                var chunk = new ChunkedPacket
                {
                    SenderId = ChunkedPacket.LocalSenderId,
                    SequenceId = sequenceId,
                    ChunkIndex = i,
                    TotalChunks = totalChunks,
                    ChunkData = chunkData
                };

                byte[] chunkBytes = PacketSender.SerializePacketForSending(chunk);

                // Chunking only reconstructs if every piece arrives, so the
                // reliability of the pieces is not the caller's to choose.
                //
                // This does not contradict the "Unreliable for steady-state
                // drift" invariant the periodic syncers are built on. That
                // invariant assumes a packet fits one MTU, so a loss costs
                // exactly that packet and the next force-refresh repairs it.
                // A payload that has to be split has already left that regime:
                // losing one datagram would discard the whole payload with
                // nothing to rebuild it from and no refresh tick behind it.
                // Reaching here at all means a batch was sized wrong, so say so.
                if (!SendSerialized(conn, chunkBytes, chunk, PacketSendMode.Reliable))
                    allSent = false;
            }

            // Previously this returned true whatever happened, so a partial send
            // looked like a success and the receiver waited for a chunk that was
            // never on the wire.
            if (!allSent)
            {
                DebugConsole.LogError(
                    $"[Transport] only part of a {totalChunks}-chunk payload was sent; the receiver cannot " +
                    "reassemble it.", false);
            }
            return allSent;
        }

        /// <summary>Hand already-serialized bytes to the transport. No size logic here.</summary>
        protected abstract bool SendSerialized(object conn, byte[] bytes, IPacket packet, PacketSendMode sendType);

        /// <summary>
        /// Largest serialized payload this transport delivers as a single
        /// indivisible unit. Past this it is split - by us on Riptide, inside
        /// Steam on Steam - and on an unreliable send, losing any one piece
        /// discards the whole payload.
        ///
        /// Callers that build periodic packets have to size their batches
        /// against this. Without it there was no contract to size against, and
        /// ConduitFlowSyncer ended up hardcoding Steam's number
        /// ("50 * 22 = 1100 bytes, fits Steam P2P unreliable MTU") while running
        /// over Riptide, whose limit is 1000.
        /// </summary>
        public abstract int MaxUnfragmentedPayloadBytes { get; }

        /// <summary>
        /// Largest payload the transport accepts at all. Beyond this the send
        /// fails outright rather than being split.
        /// </summary>
        public abstract int MaxMessageBytes { get; }

        /// <summary>
        /// The strictest limit any supported transport imposes. A packet built
        /// once and sent over whichever transport happens to be active has to
        /// fit this, not the limit of the transport that was in mind when it was
        /// written.
        /// </summary>
        public const int StrictestUnfragmentedPayloadBytes = 1000;
    }
}
