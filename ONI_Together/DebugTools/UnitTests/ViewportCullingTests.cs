using System.Collections.Generic;
using System.Linq;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Who a broadcast actually reaches.
    ///
    /// Culling lives inside the sender, which means a caller cannot opt out by
    /// picking a different send method - and one that believed it could went
    /// unnoticed for a long time. The position handler had an explicit "never
    /// cull duplicants" branch that handed its packet to SendToAllClients,
    /// which culled it anyway, because the packet type is IViewportCullable.
    /// One duplicant out of twenty two never received a position on the client:
    /// the only one that never moved, so the only one that never wandered into
    /// the client's view. Everything upstream said the packet had been sent.
    ///
    /// These pin the contract that made that possible to state at all: a
    /// negative cell means do not cull, and the sender is the single place that
    /// decides.
    /// </summary>
    public static class ViewportCullingTests
    {
        [UnitTest(name: "AlwaysSend opts a position packet out of culling", category: "Viewport")]
        public static UnitTestResult AlwaysSendOptsOut()
        {
            var packet = new EntityPositionPacket
            {
                NetId = 1234,
                Position = new Vector3(10f, 10f, 0f),
                AlwaysSend = true,
            };

            int cell = packet.GetViewportCell();
            if (cell >= 0)
            {
                return UnitTestResult.Fail(
                    $"AlwaysSend must yield a negative cell so the sender skips the viewport test, got {cell}. " +
                    "A non-negative cell here means duplicants are culled again, which is the bug this pins.");
            }

            return UnitTestResult.Pass($"cell={cell}");
        }

        [UnitTest(name: "A plain position packet still reports its cell", category: "Viewport")]
        public static UnitTestResult PlainPacketReportsCell()
        {
            var position = new Vector3(12f, 8f, 0f);
            var packet = new EntityPositionPacket { NetId = 1235, Position = position };

            int expected = Grid.PosToCell(position);
            if (!Grid.IsValidCell(expected))
                return UnitTestResult.Skip("no world loaded, so there is no cell to compare against");

            int actual = packet.GetViewportCell();
            if (actual != expected)
            {
                return UnitTestResult.Fail(
                    $"expected cell {expected} for {position}, got {actual}. Culling would then be " +
                    "decided against the wrong part of the map.");
            }

            return UnitTestResult.Pass($"cell={actual}");
        }

        /// <summary>
        /// The opt-out is only worth anything if the sender honours it, and the
        /// sender is what the handler could not see past. This asks the real
        /// broadcast path, so it needs a session; off-session it reports that
        /// rather than passing on nothing.
        /// </summary>
        [UnitTest(name: "The sender delivers an uncullable packet to every client", category: "Viewport")]
        public static UnitTestResult SenderHonoursTheOptOut()
        {
            if (!MultiplayerSession.IsHost)
                return UnitTestResult.Skip("host only: SendToAllClients refuses to run anywhere else");

            var eligible = MultiplayerSession.ConnectedPlayers.Values
                .Count(p => p.PlayerId != MultiplayerSession.HostUserID);
            if (eligible == 0)
                return UnitTestResult.Skip("no clients connected, so every count would be zero either way");

            // Deliberately a cell no camera is looking at. Under the old double
            // layer this arrived at nobody even with AlwaysSend set.
            var packet = new EntityPositionPacket
            {
                NetId = 0,
                Position = Vector3.zero,
                AlwaysSend = true,
            };

            int reached = PacketSender.SendToAllClients(packet, PacketSendMode.Unreliable);
            if (reached < eligible)
            {
                return UnitTestResult.Fail(
                    $"an uncullable packet reached {reached} of {eligible} clients. " +
                    "The sender is still culling something that asked not to be.");
            }

            return UnitTestResult.Pass($"reached {reached}/{eligible}");
        }

        /// <summary>
        /// Every duplicant with a handler should be reporting to somebody. A
        /// handler whose entire output is culled is the exact shape of the bug,
        /// and it is invisible from the client - which only ever sees silence.
        /// </summary>
        [UnitTest(name: "No duplicant has all of its positions culled", category: "Viewport")]
        public static UnitTestResult NoDuplicantIsFullyCulled()
        {
            if (!MultiplayerSession.IsHost)
                return UnitTestResult.Skip("only the host sends positions");
            if (MultiplayerSession.ConnectedPlayers.Count <= 1)
                return UnitTestResult.Skip("no clients connected, so nothing is expected to be sent");

            var starved = new List<string>();
            int checkedCount = 0;

            foreach (var minion in Object.FindObjectsByType<MinionIdentity>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (minion.IsNullOrDestroyed()) continue;
                var handler = minion.GetComponent<EntityPositionHandler>();
                if (handler == null) continue;

                checkedCount++;

                // Nothing attempted yet is not a failure - it may have spawned
                // this frame. Attempted and never delivered is.
                if (handler.CulledCount > 0 && handler.SentCount == 0)
                    starved.Add($"{minion.gameObject.GetProperName()} culled={handler.CulledCount}");
            }

            if (starved.Count > 0)
            {
                return UnitTestResult.Fail(
                    $"{starved.Count} of {checkedCount} duplicants had every position culled and none delivered: " +
                    string.Join(", ", starved) +
                    ". On the client these simply never move.");
            }

            return UnitTestResult.Pass($"{checkedCount} duplicants, all reaching at least one client");
        }
    }
}
