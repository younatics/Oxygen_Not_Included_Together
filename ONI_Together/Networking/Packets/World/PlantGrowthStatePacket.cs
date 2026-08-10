using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	public struct PlantData
	{
		public int PlantNetId;
		public int ReceptacleNetId;
		public int Cell;
		public string PlantPrefabTag;
		public float Maturity;
		public bool IsWilting;
		public bool IsHarvestReady;
		public bool IsWild;
	}

	public class PlantGrowthStatePacket : IPacket
	{
		public List<PlantData> Plants = new List<PlantData>();

		/// <summary>
		/// Which snapshot this batch belongs to, and where it sits in it.
		///
		/// The receiver reconciles by absence - a plant it holds that is not in
		/// the packet is destroyed - so a snapshot cannot be cut into
		/// independent packets. Each batch would delete the plants belonging to
		/// the others. These let the receiver hold the pieces and reconcile only
		/// once the whole snapshot has arrived.
		/// </summary>
		public int SweepId;
		public int BatchIndex;
		public int BatchCount = 1;

		/// <summary>Counts, sweep header, and what the sender frames every packet with.</summary>
		public const int HeaderBytes = 16 + Networking.PacketSender.FramingBytes;

		/// <summary>
		/// Serialized cost of one entry.
		///
		/// Counted rather than assumed because it is not fixed: PlantPrefabTag
		/// goes out as a full string for every plant on every tick and is most
		/// of the entry. A batch has to be closed on accumulated bytes, not on a
		/// count, or the cap is wrong for whatever happens to be growing.
		/// </summary>
		public static int EntryBytes(in PlantData p)
		{
			int tag = string.IsNullOrEmpty(p.PlantPrefabTag)
				? 1
				: System.Text.Encoding.UTF8.GetByteCount(p.PlantPrefabTag) + LengthPrefixBytes(p.PlantPrefabTag);

			//   net id 4 + receptacle 4 + cell 4 + tag + maturity 4 + three bools
			return 4 + 4 + 4 + tag + 4 + 3;
		}

		/// <summary>BinaryWriter writes a string length as a 7-bit encoded int.</summary>
		private static int LengthPrefixBytes(string s)
		{
			int len = System.Text.Encoding.UTF8.GetByteCount(s);
			int bytes = 1;
			while (len >= 0x80) { len >>= 7; bytes++; }
			return bytes;
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(SweepId);
			writer.Write(BatchIndex);
			writer.Write(BatchCount);
			writer.Write(Plants.Count);
			foreach (var p in Plants)
			{
				writer.Write(p.PlantNetId);
				writer.Write(p.ReceptacleNetId);
				writer.Write(p.Cell);
				writer.Write(p.PlantPrefabTag ?? string.Empty);
				writer.Write(p.Maturity);
				writer.Write(p.IsWilting);
				writer.Write(p.IsHarvestReady);
				writer.Write(p.IsWild);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			SweepId = reader.ReadInt32();
			BatchIndex = reader.ReadInt32();
			BatchCount = reader.ReadInt32();
			int count = PacketList.ReadCount(reader, "PlantGrowthStatePacket.Plants");
			Plants = new List<PlantData>(count);

			for (int i = 0; i < count; i++)
			{
				Plants.Add(new PlantData
				{
					PlantNetId = reader.ReadInt32(),
					ReceptacleNetId = reader.ReadInt32(),
					Cell = reader.ReadInt32(),
					PlantPrefabTag = reader.ReadString(),
					Maturity = reader.ReadSingle(),
					IsWilting = reader.ReadBoolean(),
					IsHarvestReady = reader.ReadBoolean(),
					IsWild = reader.ReadBoolean()
				});
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost) return;

			PlantGrowthSyncer.Instance?.OnPlantStateReceived(this);
		}
	}
}
