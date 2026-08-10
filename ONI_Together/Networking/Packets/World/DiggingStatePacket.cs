using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// A dig order and the id the host gave it.
	///
	/// The sweep carried cells alone, so a client that had drawn its own marker
	/// had no way to learn the host's name for it. Since clients stopped minting
	/// ids, that left every client-initiated marker unaddressable - which is why
	/// a run where both peers dig showed six times the failed lookups of a
	/// host-only one.
	/// </summary>
	public struct DigEntry
	{
		public int Cell;
		public int NetId;
	}

	public class DiggingStatePacket : IPacket
	{
		public List<DigEntry> Digs = new List<DigEntry>();

		/// <summary>
		/// Which snapshot this batch belongs to, and where it sits in it.
		///
		/// The receiver reconciles by absence, so a snapshot cannot be cut into
		/// independent packets - each would delete the cells belonging to the
		/// others. These let the receiver hold the pieces and reconcile only
		/// once the whole snapshot has arrived.
		/// </summary>
		public int SweepId;
		public int BatchIndex;
		public int BatchCount = 1;

		/// <summary>Counts, sweep header, and the packet type the sender frames with.</summary>
		public const int HeaderBytes = 16 + Networking.PacketSender.FramingBytes;

		/// <summary>Cell 4 + NetId 4.</summary>
		public const int BytesPerCell = 8;

		/// <summary>How many cells fit in one indivisible payload of the given size.</summary>
		public static int MaxCellsFor(int payloadLimitBytes)
		{
			int fits = (payloadLimitBytes - HeaderBytes) / BytesPerCell;
			return fits < 1 ? 1 : fits;
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(SweepId);
			writer.Write(BatchIndex);
			writer.Write(BatchCount);
			writer.Write(Digs.Count);
			foreach (var d in Digs)
			{
				writer.Write(d.Cell);
				writer.Write(d.NetId);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			SweepId = reader.ReadInt32();
			BatchIndex = reader.ReadInt32();
			BatchCount = reader.ReadInt32();
			int count = PacketList.ReadCount(reader, "DiggingStatePacket.Digs");
			Digs = new List<DigEntry>(count);
			for (int i = 0; i < count; i++)
			{
				Digs.Add(new DigEntry { Cell = reader.ReadInt32(), NetId = reader.ReadInt32() });
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost) return;

			WorldStateSyncer.Instance?.OnDiggingStateReceived(this);
		}
	}
}
