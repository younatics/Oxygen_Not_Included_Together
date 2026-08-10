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

		/// <summary>
		/// How many position packets this handler has actually put on the wire,
		/// and when. Read by the diagnostic dump: "the host is sending and the
		/// client is not receiving" and "the host never sent" look identical
		/// from the client, and telling them apart by reasoning has now cost
		/// several rounds.
		///
		/// Counted from what the sender reports it delivered, not from reaching
		/// the send call. The first version counted attempts, so a handler whose
		/// every packet was culled one layer below still read "sent=79" - which
		/// is precisely the reading that sent me looking at the receiver for the
		/// bug that was in the sender. A counter that cannot distinguish success
		/// from failure is worse than none, because it is trusted.
		/// </summary>
		public int SentCount { get; private set; }

		/// <summary>Reached the sender and went to nobody, almost always viewport culling.</summary>
		public int CulledCount { get; private set; }

		public float LastSendTime => lastSendTime;


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

		        // Asked every time, not cached. Caching it looked free and was not:
		        // the answer is taken the first time this runs, and a duplicant
		        // whose tags were not applied yet at that instant cached "cull me"
		        // and was culled for the rest of the session. HasTag is a hash
		        // lookup against a set; it is not worth a bug.
		        bool alwaysSend = gameObject.HasTag(GameTags.BaseMinion) || gameObject.HasTag(GameTags.Creature);

		        var packet = new EntityPositionPacket
		        {
			        NetId = this.GetNetId(),
			        Position = currentPosition,
			        FlipX = kbac != null && kbac.FlipX,
			        FlipY = kbac != null && kbac.FlipY,
			        NavType = navType,
			        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			        AlwaysSend = alwaysSend,
		        };

		        // Only to peers that can see where this thing is - decided by the
		        // sender, not here.
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
		        // Duplicants and critters opt out via AlwaysSend, because an off
		        // screen duplicant that never received a position has nothing to
		        // draw with the moment the camera reaches it.
		        //
		        // This used to cull here as well, and that second layer is what
		        // made the opt-out a lie: EntityPositionPacket is IViewportCullable,
		        // so the "send to everyone" branch went to SendToAllClients and was
		        // culled one level down anyway. One duplicant out of twenty two
		        // never received a position - the only one that never moved, so the
		        // only one that never wandered into the client's view. It cost
		        // several rounds of investigation because from up here it looked
		        // like the packet had been sent.
		        int recipients = PacketSender.SendToAllClients(packet, sendType: PacketSendMode.Unreliable);

		        if (recipients > 0)
			        SentCount++;
		        else
			        CulledCount++;

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
