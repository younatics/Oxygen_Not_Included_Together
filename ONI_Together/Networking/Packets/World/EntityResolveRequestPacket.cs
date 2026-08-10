using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using System.IO;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
    /// <summary>
    /// "I keep being told about NetId X and I have no such object - what is it?"
    ///
    /// Entity creation is not replicated for loose items: both peers run their
    /// own simulation and each drops its own debris, and the deterministic id
    /// makes the two line up whenever both produced the same thing. When they
    /// do not, the host holds items the client has never heard of, and every
    /// packet about one of them - work progress, a pickup, a storage move - can
    /// only produce a warning. In one measured run that was 445 failed lookups
    /// on the client and 80 objects the host had and the client did not, almost
    /// all of them ore and gas and seeds lying on the ground.
    ///
    /// This is deliberately demand-driven rather than a periodic sweep. The
    /// sweep shape is what deleted 294 of a client's plants: a receiver that
    /// reconciles by absence will believe an empty snapshot. Here nothing is
    /// ever removed - the client names one id it is missing and the host either
    /// answers with the object or says nothing. When the two peers agree, which
    /// is the normal case, no traffic happens at all.
    /// </summary>
    public class EntityResolveRequestPacket : IPacket
    {
        public int NetId;
        public ulong RequesterId;

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();
            writer.Write(NetId);
            writer.Write(RequesterId);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();
            NetId = reader.ReadInt32();
            RequesterId = reader.ReadUInt64();
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (!MultiplayerSession.IsHost)
                return;

            if (NetId == 0 || RequesterId == 0)
            {
                // A request nobody can be answered to. Named rather than
                // dropped: an unfilled requester id has been the cause of a
                // whole reply path silently going nowhere before.
                ThrottledLog.Warn($"[EntityResolve] ignoring request for {NetId} from player {RequesterId}");
                return;
            }

            if (!NetworkIdentityRegistry.TryGet(NetId, out var identity) || identity.IsNullOrDestroyed())
                return;   // The host does not have it either. Nothing to say.

            var go = identity.gameObject;
            if (go.IsNullOrDestroyed())
                return;

            // Only loose items. A building or a duplicant the client is missing
            // is a different and much larger problem, and spawning one from here
            // would paper over it.
            if (!go.TryGetComponent<Pickupable>(out var pickupable) || pickupable.IsNullOrDestroyed())
                return;

            if (!go.TryGetComponent<PrimaryElement>(out var element) || element.IsNullOrDestroyed())
                return;

            var elementDef = ElementLoader.GetElement(element.Element.tag);
            var substance = elementDef?.substance;
            if (substance == null)
                return;   // Not something SpawnResource can make - a seed, an artifact.

            // Reuses the drop packet rather than inventing a second spawn path.
            // That one is the busiest in the mod and has already been taught the
            // things this needs to know: reserve the id before the object exists,
            // refuse to spawn without a world, and consume a pickup that arrived
            // while the item did not exist yet.
            PacketSender.SendToPlayer(RequesterId, new WorldDamageSpawnResourcePacket(
                NetId,
                go.transform.position,
                element.Mass,
                element.Temperature,
                ElementLoader.GetElementIndex(element.ElementID),
                element.DiseaseIdx,
                element.DiseaseCount), PacketSendMode.Reliable);
        }
    }
}
