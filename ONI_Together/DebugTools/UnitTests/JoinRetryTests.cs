using System.Collections.Generic;
using ONI_Together.Networking;
using ONI_Together.Networking.States;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// Can the player press join again after a join that failed?
    ///
    /// A duplicate join is refused now, and it had to be: one arriving while a
    /// 2.2 MB save transfer was in flight reset the state, the transfer was
    /// abandoned, and the client sat at the main menu in-session with an empty
    /// registry until it was killed.
    ///
    /// The refusal then produced the opposite bug. The LAN timeout path left the
    /// state at Connecting - it unsubscribes both transport callbacks and Riptide
    /// raises no Disconnected for an attempt that never connected - so after one
    /// failed join every later click was refused and nothing happened at all. That
    /// is what "sometimes pressing join does nothing" was.
    ///
    /// Two failures with opposite fixes meeting in one branch is exactly the kind of
    /// thing that gets re-broken by a later edit that only remembers one of them, so
    /// both directions are pinned here.
    /// </summary>
    public static class JoinRetryTests
    {
        [UnitTest(name: "A live connect attempt is not interrupted", category: "Join")]
        public static UnitTestResult LiveAttemptIsProtected()
        {
            var complaints = new List<string>();

            // A young Connecting state is a real attempt in progress.
            if (GameClient.IsStaleConnectAttempt(ClientState.Connecting, 0f))
                complaints.Add("a connect attempt was called dead the instant it started");
            if (GameClient.IsStaleConnectAttempt(ClientState.Connecting, GameClient.StaleConnectSeconds - 1f))
                complaints.Add($"an attempt {GameClient.StaleConnectSeconds - 1f:0}s old was called dead, " +
                               "which would let a duplicate join abandon a save transfer");

            // Everything past Connecting is a live session, at any age. These are
            // the states where accepting a join does real damage.
            foreach (var state in new[] { ClientState.Connected, ClientState.LoadingWorld, ClientState.InGame })
            {
                if (GameClient.IsStaleConnectAttempt(state, 10_000f))
                    complaints.Add($"{state} was treated as a dead connect attempt - a join would " +
                                   "be accepted on top of a live session");
            }

            return complaints.Count == 0
                ? UnitTestResult.Pass($"live attempts and live sessions are protected (threshold {GameClient.StaleConnectSeconds:0}s)")
                : UnitTestResult.Fail(string.Join("; ", complaints));
        }

        [UnitTest(name: "A dead connect attempt does not block retrying", category: "Join")]
        public static UnitTestResult DeadAttemptIsRetryable()
        {
            // Past the threshold, Connecting means a failure nobody reported. If
            // this returns false the join button is dead until the game restarts.
            if (!GameClient.IsStaleConnectAttempt(ClientState.Connecting, GameClient.StaleConnectSeconds + 1f))
            {
                return UnitTestResult.Fail(
                    $"a Connecting state {GameClient.StaleConnectSeconds + 1f:0}s old is still treated as " +
                    "live, so a failed join can never be retried - pressing join would do nothing");
            }

            // The threshold has to sit above the transport's own timeout, or a slow
            // but succeeding join gets declared dead while it is still working.
            int transportTimeout = Configuration.Instance.Client.TimeoutSeconds;
            if (GameClient.StaleConnectSeconds <= transportTimeout)
            {
                return UnitTestResult.Fail(
                    $"the stale threshold ({GameClient.StaleConnectSeconds:0}s) is not above the client " +
                    $"timeout ({transportTimeout}s), so a join still in progress can be judged dead");
            }

            return UnitTestResult.Pass(
                $"a failed attempt is retryable after {GameClient.StaleConnectSeconds:0}s, " +
                $"safely above the {transportTimeout}s transport timeout");
        }
    }
}
