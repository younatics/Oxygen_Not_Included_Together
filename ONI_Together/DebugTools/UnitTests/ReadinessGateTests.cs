using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Animation;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.States;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Does the host stop sending world state to a client that has no world?
    ///
    /// A joining client answers every NetId-addressed packet with a failed
    /// lookup, because its registry is empty until the world spawns - and that
    /// counter is the divergence gate, so a perfectly good join reported itself
    /// as a desync. In one measured run eight distinct ids arrived in a burst
    /// seven seconds before the client's world existed, every one logged against
    /// a registry holding zero objects.
    ///
    /// The host had the information all along: the client reports Loading, and
    /// that report reached the transport and stopped there, while the player
    /// object the broadcast path reads still said Ready - its default.
    /// </summary>
    public static class ReadinessGateTests
    {
        /// <summary>
        /// The packets that named themselves in the failure data, by count:
        /// WorkableProgress 826, StandardWorker working state 44, SymbolOverride
        /// 18, StorageItem 10. EntityPosition is here because it is the same
        /// shape of packet, addressed the same way.
        /// </summary>
        private static readonly System.Type[] MustBeGated =
        {
            typeof(WorkableProgressPacket),
            typeof(StandardWorker_WorkingState_Packet),
            typeof(SymbolOverridePacket),
            typeof(StorageItemPacket),
            typeof(EntityPositionPacket),
        };

        [UnitTest(name: "World-state packets are marked as needing a loaded world", category: "Readiness")]
        public static UnitTestResult GatedPacketsCarryTheMarker()
        {
            var missing = MustBeGated
                .Where(t => !typeof(IRequiresLoadedWorld).IsAssignableFrom(t))
                .Select(t => t.Name)
                .ToList();

            if (missing.Count > 0)
            {
                return UnitTestResult.Fail(
                    "these address objects by NetId but are not gated on the receiver having a world: " +
                    string.Join(", ", missing) +
                    ". A joining client turns each one into a failed lookup.");
            }

            return UnitTestResult.Pass($"{MustBeGated.Length} packet types gated");
        }

        /// <summary>
        /// The gate is only as good as the state it reads, and that state was
        /// the part that was broken: Loading arrived, was recorded on the
        /// transport, and never reached the player.
        /// </summary>
        [UnitTest(name: "Reporting Loading records when it started", category: "Readiness")]
        public static UnitTestResult LoadingIsStamped()
        {
            var player = new MultiplayerPlayer(123456UL);
            if (player.readyState != ClientReadyState.Ready)
                return UnitTestResult.Fail($"a new player should start Ready, was {player.readyState}");

            player.SetReadyState(ClientReadyState.Loading);

            if (player.readyState != ClientReadyState.Loading)
                return UnitTestResult.Fail("SetReadyState did not record Loading");

            // Stamped, so the gate can give up on a client that says Loading and
            // never says Ready instead of starving it for the session.
            if (player.LoadingSince <= 0f && Time.unscaledTime > 0f)
                return UnitTestResult.Fail("Loading was recorded without a timestamp, so the gate can never expire");

            player.SetReadyState(ClientReadyState.Ready);
            if (player.readyState != ClientReadyState.Ready)
                return UnitTestResult.Fail("could not return to Ready");

            return UnitTestResult.Pass("Loading is stamped and reversible");
        }

        /// <summary>
        /// Live check: nobody is still held behind the gate. A client stuck in
        /// Loading receives no world state at all, which would be a far worse
        /// bug than the warnings the gate removes - so it is worth asserting on
        /// a running session rather than trusting the expiry.
        /// </summary>
        [UnitTest(name: "No connected client is stuck loading", category: "Readiness")]
        public static UnitTestResult NobodyStuckLoading()
        {
            if (!MultiplayerSession.IsHost)
                return UnitTestResult.Skip("only the host tracks other players' ready state");

            var stuck = MultiplayerSession.ConnectedPlayers.Values
                .Where(p => p.PlayerId != MultiplayerSession.HostUserID)
                .Where(p => p.readyState == ClientReadyState.Loading)
                .Select(p => $"{p.PlayerName} ({p.PlayerId}) for {Time.unscaledTime - p.LoadingSince:0}s")
                .ToList();

            if (stuck.Count > 0)
            {
                return UnitTestResult.Fail(
                    "still marked Loading, so the host is withholding world state from them: " +
                    string.Join(", ", stuck));
            }

            int clients = MultiplayerSession.ConnectedPlayers.Count - 1;
            return clients <= 0
                ? UnitTestResult.Skip("no clients connected")
                : UnitTestResult.Pass($"{clients} client(s), none held behind the gate");
        }
    }
}
