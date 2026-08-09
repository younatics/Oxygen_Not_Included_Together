using System.Collections.Generic;
using ONI_Together.Networking.Packets.Architecture;
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

        public abstract bool SendPacket(object conn, IPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate);

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
