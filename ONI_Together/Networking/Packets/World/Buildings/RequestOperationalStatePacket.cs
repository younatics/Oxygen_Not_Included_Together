using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Scripts.Buildings;
using System;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;
using UnityEngine;
namespace ONI_Together.Networking.Packets.World.Buildings
{
	internal class RequestOperationalStatePacket : IPacket
	{
		public RequestOperationalStatePacket() { }
		public RequestOperationalStatePacket(MonoBehaviour o)
		{
			using var _ = Profiler.Scope();

			NetId = o.GetNetId();
			RequesterId = MultiplayerSession.LocalUserID;
		}

		public int NetId;

		/// <summary>
		/// Who to answer. Without it the host cannot reply to one client, and a
		/// reply addressed to player 0 goes nowhere - a mistake this mod has made
		/// on more than one request path.
		/// </summary>
		public ulong RequesterId;

		public bool IsActive, IsOperational, IsFunctional;
		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			NetId = reader.ReadInt32();
			RequesterId = reader.ReadUInt64();
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(NetId);
			writer.Write(RequesterId);
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.IsHost)
				return;

			// The reply was never written.
			//
			// This handler used to end with `server.IsOperational =
			// server.IsOperational` - an assignment to itself, which compiles and
			// does nothing. So a client asked for a building's operational state
			// and the host looked it up and dropped it. OperationalStatePacket
			// exists, is complete, and applies exactly these three fields on the
			// client; nothing anywhere sent it.
			//
			// The visible effect is a joining client showing machines as running or
			// stopped according to whatever its save happened to say, until some
			// other event corrects them.
			if (NetId == 0 || RequesterId == 0)
			{
				// Named rather than dropped. These were the three id-less packets a
				// live host logged, and they were only attributable at all because
				// the registry warning started naming the packet instead of the
				// method - every packet's handler is called OnDispatched.
				ThrottledLog.Warn(
					$"[RequestOperationalState] ignoring a request for NetId {NetId} from player {RequesterId}");
				return;
			}

			if (!NetworkIdentityRegistry.TryGet(NetId, out var entity))
				return;
			if (!entity.TryGetComponent<Operational>(out var server))
				return;

			PacketSender.SendToPlayer(RequesterId, new OperationalStatePacket(server), PacketSendMode.Reliable);
		}
	}
}
