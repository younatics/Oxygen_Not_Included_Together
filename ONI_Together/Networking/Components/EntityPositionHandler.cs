using System;
using ONI_Together.Networking.Packets.Core;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	public class EntityPositionHandler : KMonoBehaviour
	{
        [MyCmpGet] public KBatchedAnimController kbac;
        [MyCmpGet] public Navigator navigator;

        private Vector3 lastSentPosition;
		private float lastSendTime;

		private static readonly System.Collections.Generic.HashSet<ulong> _viewportScratch = new System.Collections.Generic.HashSet<ulong>();

		/// <summary>Whether this entity is one the player follows, cached because tags do not change.</summary>
		private bool? _alwaysSend;

		private const float PositionThreshold = 0.05f;
		private const float MIN_DT = 0.016f;

        public Vector3 serverPosition;
        public long serverTimestamp;
        public bool serverFlipX;
        public bool serverFlipY;
        public NavType serverNavType;

        private const float SNAP_DISTANCE = 1.5f;
        private const float LERP_SPEED = 20f;

        private float _lastRequestTime;
        private const float REQUEST_COOLDOWN = 0.5f;
        private const float STALE_THRESHOLD = 2f;
		private const float HEARTBEAT_INTERVAL = 1f;

        public override void OnSpawn()
		{
			using var _ = Profiler.Scope();
			base.OnSpawn();

			lastSentPosition = transform.position;
			lastSendTime = Time.unscaledTime;
		}

		private void Update()
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession)
				return;

            if (MultiplayerSession.IsClient)
			{
				// Applied even without a NetId of our own. A client-side preview
				// waits unnamed until the host names it, and bailing out here
				// left it frozen for that whole time - not because position
				// packets were missing, but because this handler never ran. A
				// duplicant measured five cells from where the host had it, with
				// 1859 position packets in flight.
				//
				// serverTimestamp still gates whether there is anything to
				// apply, so an object that has genuinely never been told stays
				// where it is.
				UpdatePosition();
                // Only do this if this entity is NOT visible by the host but is visible by the client
                TryRequestEntityPositionIfVisible();
                return;
			}

			// The send path does need one - a position addressed to id 0 cannot
			// be matched to anything on the far side.
			if (this.GetNetId() == 0)
				return;

			// Skip if no clients connected
			if (MultiplayerSession.ConnectedPlayers.Count == 0)
				return;

			SendPositionUpdate();
		}

        private void TryRequestEntityPositionIfVisible()
        {
            if (WorldStateSyncer.TryGetLocalViewport(out var viewport))
            {
                int cell = Grid.PosToCell(transform.position);
                if (WorldStateSyncer.IsCellInRect(cell, viewport) && Time.unscaledTime - _lastRequestTime > REQUEST_COOLDOWN)
                {
                    // serverTimestamp is stale or we've never heard from the host
                    if (serverTimestamp == 0 || Time.unscaledTime - (serverTimestamp / 1000f) > STALE_THRESHOLD)
                    {
                        // RequesterId was never set here, so it went out as 0
                        // and the host replied to a player that does not exist.
                        // Every position request a client has ever made was
                        // answered into nothing, and because the answer is what
                        // clears serverTimestamp, the staleness check above
                        // stayed true and the request repeated at every cooldown
                        // for the whole session - 3312 dropped replies in the
                        // last fifteen-minute one.
                        PacketSender.SendToHost(new EntityPositionRequestPacket
                        {
                            NetId = this.GetNetId(),
                            RequesterId = MultiplayerSession.LocalUserID,
                        });
                        _lastRequestTime = Time.unscaledTime;
                    }
                }
            }
        }

        private void SendPositionUpdate()
        {
	        using var _ = Profiler.Scope();

	        try
	        {
		        Vector3 currentPosition = transform.position;
		        float currentTime = Time.unscaledTime;

                if (currentTime - lastSendTime < MIN_DT)
			        return;

                bool moved = Vector3.Distance(currentPosition, lastSentPosition) >= PositionThreshold;
                bool heartbeatDue = currentTime - lastSendTime >= HEARTBEAT_INTERVAL;
                if (!moved && !heartbeatDue)
			        return;

		        NavType navType = NavType.Floor;
		        if (navigator != null && navigator.CurrentNavType != NavType.NumNavTypes)
			        navType = navigator.CurrentNavType;

		        var packet = new EntityPositionPacket
		        {
			        NetId = this.GetNetId(),
			        Position = currentPosition,
			        FlipX = kbac != null && kbac.FlipX,
			        FlipY = kbac != null && kbac.FlipY,
			        NavType = navType,
			        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
		        };

		        // Only to peers that can see where this thing is.
		        //
		        // Spawns are culled to a client's viewport; position was not, so
		        // the host reported where objects were to clients that had never
		        // been given them. 361 of the 460 distinct ids a live client
		        // failed to resolve were ore, gas and plants the host held and
		        // the client had never been sent - every one of those packets
		        // could only produce a warning.
		        //
		        // A moving object that leaves a client's view simply stops being
		        // reported to it, which is correct: it cannot be drawn there, and
		        // the heartbeat resumes the moment it comes back into view.
		        // Duplicants and critters are never culled. The saving comes from
		        // the numerous and cheap - ore, gas, plants were 361 of the 460
		        // ids a client could not resolve - and duplicants are twenty
		        // objects the player watches constantly. Culling them cost a
		        // real thing: a duplicant off screen never received a position at
		        // all, so it had nothing to draw with the moment the camera
		        // reached it, and the sync test said so on the first run.
		        int posCell = Grid.PosToCell(currentPosition);
		        if (!_alwaysSend.HasValue)
			        _alwaysSend = gameObject.HasTag(GameTags.BaseMinion) || gameObject.HasTag(GameTags.Creature);

		        if (!_alwaysSend.Value && Grid.IsValidCell(posCell) && WorldStateSyncer.Instance != null)
		        {
			        _viewportScratch.Clear();
			        WorldStateSyncer.Instance.GetClientsViewingCell(posCell, _viewportScratch, 4);
			        if (_viewportScratch.Count > 0)
			        {
				        foreach (var playerId in _viewportScratch)
					        PacketSender.SendToPlayer(playerId, packet, PacketSendMode.Unreliable);
			        }
		        }
		        else
		        {
			        PacketSender.SendToAllClients(packet, sendType: PacketSendMode.Unreliable);
		        }

		        lastSentPosition = currentPosition;
		        lastSendTime = currentTime;
	        }
	        catch (Exception)
	        {
	        }
        }

        private void UpdatePosition()
        {
	        using var _ = Profiler.Scope();

            if (serverTimestamp == 0)
                return;

            if (kbac != null)
            {
	            kbac.FlipX = serverFlipX;
	            kbac.FlipY = serverFlipY;
            }

            if (navigator != null && navigator.CurrentNavType != serverNavType)
	            navigator.SetCurrentNavType(serverNavType);

            Vector3 currentPos = transform.position;
            float error = Vector3.Distance(currentPos, serverPosition);

            if (error > SNAP_DISTANCE)
            {
                transform.SetPosition(serverPosition);
                return;
            }

            float t = Mathf.Clamp01(LERP_SPEED * Time.unscaledDeltaTime);
            transform.SetPosition(Vector3.Lerp(currentPos, serverPosition, t));
        }
	}
}
