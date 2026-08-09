using ONI_Together.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Whether a reconnect could ever reach the server it lost.
    ///
    /// RiptideClient.CleanupRiptide used to reset MultiplayerSession.ServerIp
    /// and ServerPort to 127.0.0.1:7777 on every disconnect, and
    /// ReconnectToSession reads exactly those fields - so a reconnect dialled
    /// localhost on a port nothing listens on. It could not succeed, ever, and
    /// nothing tested it.
    /// </summary>
    public static class ReconnectTests
    {
        [UnitTest(name: "Reconnect target survives a session", category: "Reconnect")]
        public static UnitTestResult ReconnectTargetIsRemembered()
        {
            if (!MultiplayerSession.IsClient)
                return UnitTestResult.Skip("only a client reconnects");

            string ip = MultiplayerSession.ServerIp;
            int port = MultiplayerSession.ServerPort;

            if (string.IsNullOrEmpty(ip))
                return UnitTestResult.Fail("no server address recorded - a reconnect has nothing to dial");

            if (ip == "127.0.0.1" && port == 7777)
                return UnitTestResult.Fail(
                    "server address is 127.0.0.1:7777, the value cleanup used to write over it. A reconnect " +
                    "will dial localhost on a port nothing listens on. 7777 is not even the LAN default, " +
                    $"which is {Configuration.Instance.Client.LanSettings.Port}.");

            return UnitTestResult.Pass($"reconnect would dial {ip}:{port}");
        }

        [UnitTest(name: "Reconnect target matches the configured host", category: "Reconnect")]
        public static UnitTestResult ReconnectTargetMatchesConfig()
        {
            if (!MultiplayerSession.IsClient)
                return UnitTestResult.Skip("only a client reconnects");

            var lan = Configuration.Instance.Client.LanSettings;
            if (MultiplayerSession.ServerIp != lan.Ip || MultiplayerSession.ServerPort != lan.Port)
            {
                return UnitTestResult.Fail(
                    $"session holds {MultiplayerSession.ServerIp}:{MultiplayerSession.ServerPort} but the client " +
                    $"is configured for {lan.Ip}:{lan.Port} - a reconnect would go somewhere else");
            }

            return UnitTestResult.Pass($"session and configuration agree on {lan.Ip}:{lan.Port}");
        }
    }
}
