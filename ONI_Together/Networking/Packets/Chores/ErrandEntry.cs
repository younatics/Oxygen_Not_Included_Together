using Shared.Profiling;
using System.IO;

namespace ONI_Together.Networking.Packets.Chores
{
	public struct ErrandEntry
	{
		public string ChoreTypeId;
		public int TargetCell;
		public string TargetLabel;
		public int PriorityClass;
		public int Priority;
		public int PersonalPriority;
		public bool IsCurrent;
		public int MoreAmount;
		public string IconSpriteName;
		public int ListIndex;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			writer.Write(ChoreTypeId ?? string.Empty);
			writer.Write(TargetCell);
			writer.Write(TargetLabel ?? string.Empty);
			writer.Write(PriorityClass);
			writer.Write(Priority);
			writer.Write(PersonalPriority);
			writer.Write(IsCurrent);
			writer.Write(MoreAmount);
			writer.Write(IconSpriteName ?? string.Empty);
			writer.Write(ListIndex);
		}

		/// <summary>
		/// Serialized cost of this entry.
		///
		/// Three of the ten fields are strings - a chore type id, a target label
		/// like "Deliver Coal to Coal Generator", and an icon name - so an entry
		/// is nowhere near a fixed width and a cap counted in entries cannot bound
		/// the packet. Six ints, a bool, and those three strings run to well over
		/// a hundred bytes each in practice.
		/// </summary>
		public int Bytes()
			=> 4 + 4 + 4 + 4 + 4 + 1 + 4                       // cell, three priorities, moreAmount, isCurrent, listIndex
			 + StringBytes(ChoreTypeId) + StringBytes(TargetLabel) + StringBytes(IconSpriteName);

		private static int StringBytes(string s)
		{
			if (string.IsNullOrEmpty(s)) return 1;   // just the length prefix
			int len = System.Text.Encoding.UTF8.GetByteCount(s);
			int prefix = 1;
			int n = len;
			while (n >= 0x80) { n >>= 7; prefix++; }
			return len + prefix;
		}

		public static ErrandEntry Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			return new ErrandEntry
			{
				ChoreTypeId = reader.ReadString(),
				TargetCell = reader.ReadInt32(),
				TargetLabel = reader.ReadString(),
				PriorityClass = reader.ReadInt32(),
				Priority = reader.ReadInt32(),
				PersonalPriority = reader.ReadInt32(),
				IsCurrent = reader.ReadBoolean(),
				MoreAmount = reader.ReadInt32(),
				IconSpriteName = reader.ReadString(),
				ListIndex = reader.ReadInt32()
			};
		}
	}
}
