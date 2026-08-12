using ONI_Together.Networking;
using ONI_Together.Networking.Components;
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
            string role = MultiplayerSession.IsHost ? "host" : "client";

            // Judged on the quiet median, not on a reading taken here.
            //
            // This test used to call GetPing() at this line and failed nine runs out
            // of nine at 169 to 262 ms. The readings were accurate and the verdict
            // was wrong: the suite that asks the question is also four minutes of the
            // heaviest traffic in the session - an eight-thousand-row identity dump,
            // deliberate rubbish fed to every packet handler - so the number
            // measured the measurement. Sampled between suite runs on the same link,
            // the same client reads 41 to 76 ms.
            //
            // The instantaneous value is still logged, because the gap between the
            // two is itself the finding and hiding it would lose the evidence.
            int now = NetworkConfig.TransportClient?.GetPing() ?? -1;
            int median = LinkQualitySampler.MedianRttMs;
            int worst = LinkQualitySampler.WorstRttMs;
            int samples = LinkQualitySampler.SampleCount;

            DebugConsole.Log(
                $"[LATENCY] role={role} underLoad={now}ms quietMedian={median}ms quietWorst={worst}ms " +
                $"samples={samples} players={MultiplayerSession.ConnectedPlayers.Count}");

            if (MultiplayerSession.IsHost)
                return UnitTestResult.Skip("the host is the far end of the link and has no round trip of its own");

            // Refusing to answer beats answering from two samples. A window this
            // short means the session only just started or a hard sync just ended.
            if (samples < 10)
                return UnitTestResult.Skip($"only {samples} quiet samples so far, not enough to judge the link");

            if (median > NetworkConfig.PingRanges.BAD)
            {
                return UnitTestResult.Fail(
                    $"{role} sees a quiet median of {median} ms (worst {worst} ms over {samples} samples), " +
                    $"past the BAD threshold of {NetworkConfig.PingRanges.BAD} - this one is not the suite's " +
                    "own load, because these samples were taken while it was not running");
            }

            return UnitTestResult.Pass(
                $"{role} quiet median {median} ms, worst {worst} ms over {samples} samples " +
                $"({now} ms while the suite runs)");
        }
    }
}
