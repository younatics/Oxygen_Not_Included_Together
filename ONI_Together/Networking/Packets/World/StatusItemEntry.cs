using Shared.Profiling;
using System.IO;

namespace ONI_Together.Networking.Packets.World
{
    public struct StatusItemEntry
    {
        public string ItemId;
        public string CategoryId;
        public string DisplayName;
        public string Tooltip;

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();
            writer.Write(ItemId ?? string.Empty);
            writer.Write(CategoryId ?? string.Empty);
            writer.Write(DisplayName ?? string.Empty);
            writer.Write(Tooltip ?? string.Empty);
        }

        /// <summary>
        /// Serialized cost of this entry.
        ///
        /// Every field is a string and Tooltip is a rendered sentence - "Stress:
        /// 42.3% (+0.4%/cycle)" and worse - so an entry is not a fixed width and
        /// a cap counted in entries says nothing about the bytes on the wire.
        /// Four status items with long tooltips already exceed the payload
        /// limit that a cap of 64 was supposed to protect.
        /// </summary>
        public int Bytes()
            => StringBytes(ItemId) + StringBytes(CategoryId) + StringBytes(DisplayName) + StringBytes(Tooltip);

        private static int StringBytes(string s)
        {
            if (string.IsNullOrEmpty(s)) return 1;   // just the length prefix
            int len = System.Text.Encoding.UTF8.GetByteCount(s);
            int prefix = 1;
            int n = len;
            while (n >= 0x80) { n >>= 7; prefix++; }
            return len + prefix;
        }

        public static StatusItemEntry Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();
            return new StatusItemEntry
            {
                ItemId = reader.ReadString(),
                CategoryId = reader.ReadString(),
                DisplayName = reader.ReadString(),
                Tooltip = reader.ReadString(),
            };
        }
    }
}
