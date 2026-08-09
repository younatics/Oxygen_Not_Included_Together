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

		/// <summary>DigCells.Count prefix plus the packet type the sender frames with.</summary>
		public const int HeaderBytes = 8;

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

			writer.Write(DigCells.Count);
			foreach (var cell in DigCells)
			{
				writer.Write(cell);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

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
