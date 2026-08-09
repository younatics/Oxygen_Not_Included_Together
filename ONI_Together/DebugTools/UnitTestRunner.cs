using System;
using System.IO;
using System.Linq;
using System.Text;
using ONI_Together.Networking;

namespace ONI_Together.DebugTools
{
    /// <summary>
    /// Runs the [UnitTest] suite without the ImGui dev tool and writes the
    /// results to Player.log.
    ///
    /// The suite could only be started from a button in DevToolMultiplayer, and
    /// the results only ever lived in memory, so nothing outside the running
    /// game could see them. That makes the suite useless to an automated
    /// two-box loop: the whole point is to read a verdict off each machine
    /// without a human at the keyboard.
    ///
    /// Trigger is a file rather than an env var so a run can be requested at any
    /// moment - in the middle of a session, right after a join - without
    /// restarting the game and losing the state under test.
    ///
    /// Drop a file at %TEMP%\oni_together_runtests to request a run. Its
    /// contents, if any, are used as a category filter (one per line).
    /// </summary>
    public static class UnitTestRunner
    {
        public const string TriggerFileName = "oni_together_runtests";
        public const string Tag = "[TEST]";

        private static int _runCounter;
        private static int _frameSkip;

        public static string TriggerPath =>
            Path.Combine(Path.GetTempPath(), TriggerFileName);

        /// <summary>Called every frame from GameUpdatePatch; cheap by design.</summary>
        public static void Tick()
        {
            // Polling the filesystem every frame would be silly; ~twice a second
            // is far faster than a human can ask for a run.
            if (++_frameSkip < 30) return;
            _frameSkip = 0;

            string trigger = TriggerPath;
            string[] categories = null;
            try
            {
                if (!File.Exists(trigger)) return;

                var lines = File.ReadAllLines(trigger)
                                .Select(l => l.Trim())
                                .Where(l => l.Length > 0)
                                .ToArray();
                if (lines.Length > 0) categories = lines;

                // Delete before running: a test that hard-crashes must not leave
                // the trigger behind and re-run on every load forever.
                File.Delete(trigger);
            }
            catch (Exception ex)
            {
                DebugConsole.Log($"{Tag} trigger read failed: {ex.Message}");
                return;
            }

            RunAndLog("file-trigger", categories);
        }

        public static void RunAndLog(string reason, string[] categories = null)
        {
            int run = ++_runCounter;

            if (UnitTestRegistry.Tests.Count == 0)
                UnitTestRegistry.DiscoverTests();

            var selected = UnitTestRegistry.Tests.AsEnumerable();
            if (categories != null && categories.Length > 0)
                selected = selected.Where(t => categories.Contains(t.Category, StringComparer.OrdinalIgnoreCase));

            var tests = selected.ToList();

            DebugConsole.Log($"{Tag} BEGIN run={run} reason={reason} {DescribeContext()} tests={tests.Count}");

            foreach (var test in tests)
            {
                try { test.Run(); }
                catch (Exception ex)
                {
                    // UnitTest.Run already catches; this only guards the harness
                    // itself so one bad test cannot abort the whole sweep.
                    DebugConsole.Log($"{Tag} RESULT FAIL | {test.Category} | {test.Name} | 0 | harness error: {OneLine(ex.ToString())}");
                    continue;
                }

                string state = test.State switch
                {
                    TestState.Passed => "PASS",
                    TestState.Failed => "FAIL",
                    TestState.Skipped => "SKIP",
                    _ => "NOTRUN"
                };
                DebugConsole.Log(
                    $"{Tag} RESULT {state} | {test.Category} | {test.Name} | " +
                    $"{test.DurationMs:F1} | {OneLine(test.Message)}");
            }

            int passed = tests.Count(t => t.IsPassed);
            int failed = tests.Count(t => t.IsFailed);
            int skipped = tests.Count(t => t.IsSkipped);
            int notrun = tests.Count - passed - failed - skipped;

            DebugConsole.Log(
                $"{Tag} END run={run} passed={passed} failed={failed} skipped={skipped} notrun={notrun}");
        }

        private static string DescribeContext()
        {
            var sb = new StringBuilder();
            try
            {
                string role = MultiplayerSession.IsHost ? "host"
                            : MultiplayerSession.IsClient ? "client"
                            : "none";
                sb.Append("role=").Append(role);
                sb.Append(" transport=").Append(NetworkConfig.transport);
                sb.Append(" insession=").Append(MultiplayerSession.InSession);
            }
            catch (Exception ex)
            {
                sb.Append("context-unavailable=").Append(OneLine(ex.Message));
            }
            return sb.ToString();
        }

        /// <summary>Log lines are parsed one per record, so newlines must go.</summary>
        private static string OneLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\r", " ").Replace("\n", " ").Replace("|", "/");
        }
    }
}
