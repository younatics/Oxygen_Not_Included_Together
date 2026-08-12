using ONI_Together.Networking.Packets.Architecture;
using System.IO;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Tools.Dig
{
    public class DiggablePacket : IPacket
    {
        /// <summary>
        /// Gets a value indicating whether incoming messages are currently being processed.
        /// Use in patches to prevent recursion when applying tool changes.
        /// </summary>
        public static bool ProcessingIncoming { get; private set; }

        private int             Cell;
        private int             AnimationDelay;
        private PrioritySetting Priority;

        public DiggablePacket()
        {
        }

        public DiggablePacket(int cell, int animationDelay)
        {
            using var _ = Profiler.Scope();

            Cell           = cell;
            AnimationDelay = animationDelay;
        }

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();

            // See PriorityWire: a guarded assignment left the struct default, and zero
            // is not a priority the game accepts.
            Priority = PriorityWire.Sample();

            writer.Write(Cell);
            writer.Write(AnimationDelay);
            PriorityWire.Write(writer, Priority);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();

            Cell           = reader.ReadInt32();
            AnimationDelay = reader.ReadInt32();
            Priority       = PriorityWire.Read(reader);
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            GameObject game_object;
            ProcessingIncoming = true;
            try
            {
                game_object = DigTool.PlaceDig(Cell, AnimationDelay);
            }
            finally
            {
                ProcessingIncoming = false;
            }

            Prioritizable prioritizable = game_object?.GetComponent<Prioritizable>();
            prioritizable?.SetMasterPriority(Priority);
        }
    }
}
