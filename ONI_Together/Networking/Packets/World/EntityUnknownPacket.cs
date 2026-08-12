using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using System.IO;

namespace ONI_Together.Networking.Packets.World
{
    /// <summary>
    /// The host's answer when it cannot hand over the object that was asked for.
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
    ///
    /// That fix only covered the dead case, and the other one stayed silent - the
    /// handler still had three bare returns for "I have it but it is not a loose
    /// item I can spawn for you": no Pickupable, no PrimaryElement, or an element
    /// with no substance, which is what a seed or an artifact is. The counts show
    /// it plainly: 118 requests sent, 56 answered, 62 met with nothing at all.
    ///
    /// So the answer now carries which of the two it is. Both stop the retries;
    /// only the first means anything is missing. Keeping them apart matters
    /// because "the client lacks an object the host still holds" is the larger
    /// problem the resolver deliberately refuses to paper over, and counting it as
    /// a dead ground item would hide it for good.
    /// </summary>
    public class EntityUnknownPacket : IPacket, IRequiresLoadedWorld
    {
        /// <summary>Why the host cannot supply the object.</summary>
        public enum Answer : byte
        {
            /// <summary>It does not exist here either. Nothing is missing.</summary>
            Gone = 0,

            /// <summary>
            /// The host holds it, but it is not a loose item that can be spawned
            /// from an element - a building, a duplicant, a seed, an artifact. The
            /// client is genuinely missing something and this names it.
            /// </summary>
            HeldButNotSpawnable = 1,
        }

        public int NetId;
        public Answer Reason;

        /// <summary>
        /// Ids the host holds and the client does not, by prefab.
        ///
        /// Reported rather than counted, because the prefab is what says whether
        /// this is a building that failed to replicate or a seed that was never
        /// meant to.
        /// </summary>
        public static int HeldButNotSpawnableCount { get; private set; }

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();
            writer.Write(NetId);
            writer.Write((byte)Reason);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();
            NetId = reader.ReadInt32();
            Reason = (Answer)reader.ReadByte();
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (!MultiplayerSession.IsClient)
                return;

            // Stop asking either way: three requests and a warning per id was the
            // whole cost of the silence, and the answer does not change with a
            // fourth attempt.
            MissingEntityResolver.NoteConfirmedGone(NetId);

            if (Reason == Answer.HeldButNotSpawnable)
            {
                HeldButNotSpawnableCount++;
                ThrottledLog.Warn(
                    $"[EntityResolve] the host holds NetId {NetId} and this peer does not - it is not a " +
                    "loose item, so it cannot be sent as one. This is a replication gap, not a dead object.");
            }
        }
    }
}
