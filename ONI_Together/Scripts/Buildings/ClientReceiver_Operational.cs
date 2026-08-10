using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World.Buildings;
using ONI_Together.Networking.States;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shared.Profiling;

namespace ONI_Together.Scripts.Buildings
{
	internal class ClientReceiver_Operational : KMonoBehaviour
	{
		[MyCmpGet] NetworkIdentity o;

		public override void OnSpawn()
		{
			using var _ = Profiler.Scope();

			base.OnSpawn();
			if (!MultiplayerSession.IsClient)
				return;

			// Not before this building has an id.
			//
			// OnSpawn runs while the world is still being built, and the identity
			// may not be registered yet, so the request went out addressed to
			// NetId 0 - which cannot resolve to anything and can only be dropped.
			// A live host logged three of those. Asking again next frame costs
			// nothing; asking with no id costs the answer.
			_pendingRequest = true;
		}

		/// <summary>Set at spawn, cleared once the request has actually gone out with a real id.</summary>
		private bool _pendingRequest;

		private void Update()
		{
			if (!_pendingRequest) return;
			if (!MultiplayerSession.InSession || !MultiplayerSession.IsClient) return;

			int netId = this.GetNetId();
			if (netId == 0) return;

			_pendingRequest = false;
			PacketSender.SendToHost(new RequestOperationalStatePacket(this));
		}

		public bool IsFunctional { get; set; }

		public bool IsOperational { get; set; } = true;

		public bool IsActive { get; set; }
	}
}
