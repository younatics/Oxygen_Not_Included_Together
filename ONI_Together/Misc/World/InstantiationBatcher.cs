using ONI_Together.Networking;
using ONI_Together.Networking.Packets;
using System.Collections.Generic;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Misc.World
{
	public static class InstantiationBatcher
	{
		private static readonly List<InstantiationsPacket.InstantiationEntry> queue = new List<InstantiationsPacket.InstantiationEntry>();
		private static float timeSinceLastFlush = 0f;
		private const float FlushInterval = 2.0f;

		public static void Queue(InstantiationsPacket.InstantiationEntry entry)
		{
			using var _ = Profiler.Scope();

			queue.Add(entry);

			// Sent as it happens. Batching over two seconds left the client
			// without the object, or holding an unnamed copy, for that whole
			// window. Waiting was only tolerable while naming depended on
			// arrival order; now that packets carry a sequence and a stale
			// naming is refused, the delay buys nothing.
			Flush();
			timeSinceLastFlush = 0f;
		}

		public static void Update()
		{
			using var _ = Profiler.Scope();

			timeSinceLastFlush += Time.unscaledDeltaTime;

			if (timeSinceLastFlush >= FlushInterval)
			{
				Flush();
				timeSinceLastFlush = 0f;
			}
		}

		public static void Flush()
		{
			using var _ = Profiler.Scope();

			if (queue.Count == 0)
				return;

			var packet = new InstantiationsPacket
			{
				Entries = new List<InstantiationsPacket.InstantiationEntry>(queue)
			};

			// Reliable, not Unreliable. A lost spawn notice is not a frame of
			// staleness that the next tick repairs - the client simply never
			// learns the object exists, and every packet about it afterwards is
			// a failed lookup. There is no periodic resend behind this.
			PacketSender.SendToAllClients(packet, PacketSendMode.Reliable);
			queue.Clear();
		}
	}
}
