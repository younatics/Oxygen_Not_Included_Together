using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using System.IO;

namespace ONI_Together.Networking.Packets.World
{
    /// <summary>
    /// "I do not have that either."
    ///
    /// The resolver asks the host about ids it cannot resolve, and the host used
    /// to answer only when it could supply the object. Silence covered two very
    /// different cases: the host has it and would not send it, and the object no
    /// longer exists anywhere. The client counted both as something it was
    /// missing, and the one id left in a clean run was the second kind - a ground
    /// item that spawned, was picked up, and was gone from both peers by the time
    /// anyone asked. Reporting that as a divergence is wrong; nothing is missing,
    /// the object is dead.
    ///
    /// Saying so explicitly also stops the asking. Without this the client retries
    /// to its attempt limit and then records a gap that no fix could ever close.
    /// </summary>
    public class EntityUnknownPacket : IPacket, IRequiresLoadedWorld
    {
        public int NetId;

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();
            writer.Write(NetId);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();
            NetId = reader.ReadInt32();
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (!MultiplayerSession.IsClient)
                return;

            MissingEntityResolver.NoteConfirmedGone(NetId);
        }
    }
}
