using ONI_Together.Networking;
using ONI_Together.Networking.Transport.Lan;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// What a round trip actually costs on this link.
    ///
    /// It decides whether the host can own object creation without the game
    /// feeling laggy. A client that draws a preview and waits for the host to
    /// name it pays one round trip before the object becomes real; if that is a
    /// few milliseconds on a LAN, host authority is affordable and the peers can
    /// be exactly consistent. If it is tens of milliseconds, the preview has to
    /// carry more weight.
    ///
    /// Reported rather than gated: the number is a property of the network, not
    /// of the code. What is gated is that we can read it at all.
    /// </summary>
    public static class LatencyTests
    {
        /// <summary>A LAN round trip should be single-digit milliseconds.</summary>
        private const int LanExpectedMs = 20;

        [UnitTest(name: "Round-trip time to the host", category: "Latency")]
        public static UnitTestResult RoundTripTime()
        {
            if (!MultiplayerSession.IsClient)
                return UnitTestResult.Skip("only a client measures RTT to the host");

            var client = RiptideClient.Client;
            if (client == null)
                return UnitTestResult.Skip("not on the Riptide transport");

            int rtt = client.SmoothRTT;
            int raw = client.RTT;

            if (rtt < 0)
                return UnitTestResult.Fail("no RTT sample yet - the link has not settled");

            string verdict = rtt <= LanExpectedMs
                ? "host authority costs one of these per spawn, which a LAN can afford"
                : $"above {LanExpectedMs} ms, so waiting for the host to name an object would be felt";

            return UnitTestResult.Pass($"smoothed {rtt} ms, raw {raw} ms - {verdict}");
        }

        [UnitTest(name: "Both peers agree the link is healthy", category: "Latency")]
        public static UnitTestResult LinkHealth()
        {
            if (!MultiplayerSession.InSession)
                return UnitTestResult.Skip("not in a session");

            // Each peer reports its own view; comparing the two logs is what
            // shows an asymmetric link, which a single-sided number hides.
            int ping = NetworkConfig.TransportClient?.GetPing() ?? -1;
            string role = MultiplayerSession.IsHost ? "host" : "client";

            DebugConsole.Log($"[LATENCY] role={role} ping={ping}ms players={MultiplayerSession.ConnectedPlayers.Count}");

            if (ping < 0)
                return UnitTestResult.Skip($"{role} has no ping reading");

            if (ping > NetworkConfig.PingRanges.BAD)
                return UnitTestResult.Fail($"{role} sees {ping} ms, past the BAD threshold of {NetworkConfig.PingRanges.BAD}");

            return UnitTestResult.Pass($"{role} sees {ping} ms");
        }
    }
}
