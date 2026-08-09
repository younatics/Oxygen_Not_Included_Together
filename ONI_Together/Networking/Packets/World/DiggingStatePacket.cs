using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	public class DiggingStatePacket : IPacket
	{
		public List<int> DigCells = new List<int>();

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
		public const int HeaderBytes = 20;

		public const int BytesPerCell = 4;

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
			writer.Write(DigCells.Count);
			foreach (var cell in DigCells)
			{
				writer.Write(cell);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			SweepId = reader.ReadInt32();
			BatchIndex = reader.ReadInt32();
			BatchCount = reader.ReadInt32();
			int count = reader.ReadInt32();
			DigCells = new List<int>(count);
			for (int i = 0; i < count; i++)
			{
				DigCells.Add(reader.ReadInt32());
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
