using System;
using System.Collections.Generic;
using System.Text;

namespace ONI_Together.DebugTools
{
    public enum TestState
    {
        NotRun,
        InProgress,
        Passed,
        Failed,

        /// <summary>
        /// The test could not apply here - no session, not the host, nothing
        /// selected. Distinct from Failed on purpose: a suite run from the main
        /// menu used to report six failures that only meant "not in a session",
        /// and real failures hid among them.
        /// </summary>
        Skipped
    }

    public class UnitTestResult
    {
        public TestState State { get; private set; } = TestState.NotRun;
        public string Message { get; private set; }

        public static UnitTestResult Pass(string message = null)
            => new UnitTestResult { State = TestState.Passed, Message = message };

        public static UnitTestResult Fail(string message)
            => new UnitTestResult { State = TestState.Failed, Message = message };

        public static UnitTestResult InProgress(string message = null)
            => new UnitTestResult { State = TestState.InProgress, Message = message };

        /// <summary>Preconditions for this test are not met here; it proves nothing either way.</summary>
        public static UnitTestResult Skip(string message)
            => new UnitTestResult { State = TestState.Skipped, Message = message };
    }
}
