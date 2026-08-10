using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using System.Collections.Generic;
using System.IO;

namespace ONI_Together.Networking.Packets.World
{
	// One entry per pipe cell with non-trivially-changed contents.
	// ConduitType: 0 = gas, 1 = liquid. Solid pipes (rails) deferred — their
	// SolidConduitFlow.ConduitContents references a host-local pickupable handle
	// that does not survive serialization.
	public struct ConduitCellUpdate
	{
		public int Cell;
		public byte ConduitType;
		public int Element;        // SimHashes (int-backed enum)
		public float Mass;
		public float Temperature;
		public byte DiseaseIdx;
		public int DiseaseCount;
	}

	public class ConduitContentsPacket : IPacket
	{
		public const byte CONDUIT_GAS = 0;
		public const byte CONDUIT_LIQUID = 1;

		/// <summary>Cell 4 + type 1 + element 4 + mass 4 + temp 4 + disease idx 1 + disease count 4.</summary>
		public const int BytesPerUpdate = 22;

		/// <summary>The Updates.Count prefix, plus the int packet type the sender frames with.</summary>
		public const int HeaderBytes = 4 + Networking.PacketSender.FramingBytes;

		/// <summary>
		/// How many updates fit in one indivisible payload of the given size.
		///
		/// The batch size used to be a constant picked for one transport's MTU.
		/// Deriving it means the packet stays whole on whichever transport is
		/// actually carrying it, and it follows automatically if the entry layout
		/// or a transport limit ever changes.
		/// </summary>
		public static int MaxUpdatesFor(int payloadLimitBytes)
		{
			int fits = (payloadLimitBytes - HeaderBytes) / BytesPerUpdate;
			return fits < 1 ? 1 : fits;
		}

		public List<ConduitCellUpdate> Updates = new List<ConduitCellUpdate>();

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(Updates.Count);
			foreach (var u in Updates)
			{
				writer.Write(u.Cell);
				writer.Write(u.ConduitType);
				writer.Write(u.Element);
				writer.Write(u.Mass);
				writer.Write(u.Temperature);
				writer.Write(u.DiseaseIdx);
				writer.Write(u.DiseaseCount);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			int count = PacketList.ReadCount(reader, "ConduitContentsPacket.Updates");
			Updates = new List<ConduitCellUpdate>(count);
			for (int i = 0; i < count; i++)
			{
				Updates.Add(new ConduitCellUpdate
				{
					Cell = reader.ReadInt32(),
					ConduitType = reader.ReadByte(),
					Element = reader.ReadInt32(),
					Mass = reader.ReadSingle(),
					Temperature = reader.ReadSingle(),
					DiseaseIdx = reader.ReadByte(),
					DiseaseCount = reader.ReadInt32(),
				});
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost) return;

			ConduitFlowSyncer.Instance?.OnContentsReceived(this);
		}
	}
}
