using ONI_Together.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// The id this peer stamps on everything it asks for.
    ///
    /// It is worth its own tests because a wrong value here is silent. A request
    /// carrying id 0 is well-formed, arrives, and is handled; only the reply
    /// fails, on the far box, in a warning that used not to name the packet. A
    /// live session logged 5436 undeliverable replies over 26 minutes and the
    /// only visible symptom was a toilet that read as broken on one peer.
    /// </summary>
    public static class LocalIdentityTests
    {
        [UnitTest(name: "Local id is not zero in a session", category: "Identity")]
        public static UnitTestResult LocalIdIsUsable()
        {
            if (!MultiplayerSession.InSession)
                return UnitTestResult.Skip("not in a multiplayer session");

            ulong id = MultiplayerSession.LocalUserID;
            if (id == 0)
                return UnitTestResult.Fail(
                    "LocalUserID is 0 - every request stamped with it is unanswerable, " +
                    "and the syncers that sent them will retry for the rest of the session");

            return UnitTestResult.Pass($"local id = {id}");
        }

        [UnitTest(name: "Local id does not depend on session flags", category: "Identity")]
        public static UnitTestResult LocalIdSurvivesJoinWindow()
        {
            if (!NetworkConfig.IsLanConfig())
                return UnitTestResult.Skip("Steam derives the id from SteamUser, not from session state");
            if (!MultiplayerSession.InSession)
                return UnitTestResult.Skip("not in a multiplayer session");

            // The regression this pins: GetLocalID used to branch on
            // MultiplayerSession.IsClient, which is false until InSession flips.
            // A client that is connected but not yet in session reported the
            // server's id - zero on a box that is not hosting. Toggling the flag
            // reproduces that window without needing to catch a real join.
            bool wasHost = MultiplayerSession.IsHost;
            ulong duringSession = MultiplayerSession.LocalUserID;
            try
            {
                MultiplayerSession.IsHost = !wasHost;
                ulong flipped = MultiplayerSession.LocalUserID;
                if (flipped != duringSession)
                    return UnitTestResult.Fail(
                        $"local id changed with the IsHost flag: {duringSession} -> {flipped}; " +
                        "it must come from the transport that actually holds a connection");
            }
            finally
            {
                MultiplayerSession.IsHost = wasHost;
            }

            return UnitTestResult.Pass($"local id stays {duringSession} regardless of the session flags");
        }
    }
}
