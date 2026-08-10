using System.Collections.Generic;
using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
    /// <summary>
    /// "These are the buildings I think are broken - are they?"
    ///
    /// The damage syncer only speaks when the host's own hit points move, which
    /// leaves one case it structurally cannot see: a building the host repaired
    /// before this peer joined. The host is at full health and has nothing to
    /// report; the client loaded the save and still shows it cracked, and no
    /// amount of waiting fixes it. That is the last disagreement measured on a
    /// live colony - one tile at 68 of 100 on the client and untouched on the
    /// host - and it is the only one left.
    ///
    /// The client asks once and only about what it believes is damaged, which
    /// bounds the exchange to the number of broken buildings, three or four dozen
    /// in a large colony. Re-sending every building instead was tried and
    /// measured: 3700 packets, of which 3634 told the client something it already
    /// knew.
    ///
    /// This was written once before and removed, because the host answered all 36
    /// ids and the client resolved none of them. That was not this packet's fault:
    /// the client was announcing Ready from the main menu, so it asked before it
    /// had a registry to match the answers against. With that fixed the same
    /// design works.
    /// </summary>
    public class DamagedBuildingsQueryPacket : IPacket, IRequiresLoadedWorld
    {
        /// <summary>
        /// Sanity bound. A colony with more broken buildings than this has larger
        /// problems than replication, and the query would not fit in one packet.
        /// </summary>
        public const int MaxIds = 200;

        public ulong RequesterId;
        public List<int> NetIds = new List<int>();

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();

            writer.Write(RequesterId);
            int count = Mathf.Min(NetIds.Count, MaxIds);
            writer.Write(count);
            for (int i = 0; i < count; i++)
                writer.Write(NetIds[i]);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();

            RequesterId = reader.ReadUInt64();
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxIds)
            {
                NetIds = new List<int>();
                return;
            }

            NetIds = new List<int>(count);
            for (int i = 0; i < count; i++)
                NetIds.Add(reader.ReadInt32());
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (!MultiplayerSession.IsHost)
                return;

            if (RequesterId == 0)
            {
                // An unfilled requester id has silently swallowed a whole reply
                // path in this mod before, so it is named rather than dropped.
                ThrottledLog.Warn("[BuildingDamage] damaged-buildings query arrived with no requester id");
                return;
            }

            int answered = 0;
            foreach (int netId in NetIds)
            {
                if (!NetworkIdentityRegistry.TryGet(netId, out var identity)) continue;
                if (identity.gameObject.IsNullOrDestroyed()) continue;

                var hp = identity.gameObject.GetComponent<BuildingHP>();
                if (hp == null) continue;

                // Answered whatever the value, full health included - "it is fine
                // here" is exactly the answer the asker cannot get any other way.
                PacketSender.SendToPlayer(RequesterId, new BuildingDamagePacket
                {
                    NetId = netId,
                    HitPoints = hp.HitPoints,
                }, PacketSendMode.Reliable);

                answered++;
            }

            DebugConsole.Log(
                $"[BuildingDamage] answered {answered} of {NetIds.Count} damage queries from player {RequesterId}");
        }
    }
}
