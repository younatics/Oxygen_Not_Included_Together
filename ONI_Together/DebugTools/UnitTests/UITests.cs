using System;
using System.Collections.Generic;
using System.Text;
using ONI_Together.Misc;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
    public static class UITests
    {
        [UnitTest(name: "Chat window exists and is active", category: "UI")]
        public static UnitTestResult ChatWindowExistsAndActive()
        {
            GameObject chatScreen = GameObject.Find("ChatScreen");
            if(chatScreen == null)
                return Game.Instance == null
                    ? UnitTestResult.Skip("no game loaded, so no ChatScreen")
                    : UnitTestResult.Fail("ChatScreen object not found in scene");

            bool isActive = chatScreen.activeSelf;
            if (!isActive)
                return UnitTestResult.Fail("ChatScreen object is not active");
            return UnitTestResult.Pass("ChatScreen object exists and is active");
        }

        [UnitTest(name: "Ping & Trail Initialized", category: "UI")]
        public static UnitTestResult PingAndTrailSystemInitialized()
        {
            if (PingManager.Instance == null)
                return UnitTestResult.Fail("PingManager instance is null");
            return UnitTestResult.Pass("PingManager instance exists");
        }

        [UnitTest(name: "No ghost cursors present", category: "UI")]
        public static UnitTestResult NoGhostCursorsPresent()
        {
            if (!MultiplayerSession.IsHost && !MultiplayerSession.IsClient)
                return UnitTestResult.Skip("not connected to a multiplayer session");

            // Counted from the session rather than the transport. This used to
            // take NetworkConfig.GetConnectedClients() and subtract one for the
            // local peer, which holds on the host - its connection list includes
            // itself - but not on a client, whose list is just the host. A
            // healthy client therefore reported "cursors (1) exceeds clients (1)"
            // on every run. One cursor per remote peer is the property either
            // way, so ask the session who the remote peers are.
            int remotePeers = 0;
            foreach (var kvp in MultiplayerSession.ConnectedPlayers)
            {
                if (!kvp.Key.Equals(MultiplayerSession.LocalUserID))
                    remotePeers++;
            }

            var cursors = MultiplayerSession.PlayerCursors.Count;

            if (cursors > remotePeers)
                return UnitTestResult.Fail(
                    $"{cursors} player cursors for {remotePeers} remote peer(s) - a cursor outlived its player");

            if (cursors < remotePeers)
                return UnitTestResult.Fail(
                    $"only {cursors} player cursors for {remotePeers} remote peer(s) - a player has no cursor");

            bool cursorSyncRunning = CursorManager.Instance != null && Utils.IsInGame() && MultiplayerSession.InSession && MultiplayerSession.LocalUserID.IsValid();
            if(!cursorSyncRunning)
                return UnitTestResult.Fail("Cursor synchronization does not appear to be running (CursorManager instance missing or not in game session)");

            return UnitTestResult.Pass("Number of player cursors matches number of clients");
        }
    }
}
