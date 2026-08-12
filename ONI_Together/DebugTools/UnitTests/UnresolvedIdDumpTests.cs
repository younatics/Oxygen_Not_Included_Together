using ONI_Together.Networking;

namespace ONI_Together.DebugTools.UnitTests
{
    /// <summary>
    /// The whole set of ids this peer could not resolve, written out for comparison.
    ///
    /// Two measurements of the same gap disagree by twenty times: the registries differ
    /// by 113 identities out of 8,268, while 2,300 distinct ids have been referenced and
    /// never found. Both cannot be the size of the problem, and the sampled log cannot
    /// settle it - it prints whatever fails loudest, which is how a single Cuprite pile
    /// came to account for eight thousand failures in one run.
    ///
    /// So the set goes to the log in full and compare-netids.ps1 can put it against the
    /// host's table. Then "how many objects is the client actually missing" stops being
    /// an argument between two counters.
    /// </summary>
    public static class UnresolvedIdDumpTests
    {
        [UnitTest(name: "Dump every unresolved id for cross-peer comparison", category: "NetId")]
        public static UnitTestResult DumpUnresolved()
        {
            int n = NetworkIdentityRegistry.UnresolvedIdCount;
            if (n == 0)
                return UnitTestResult.Pass("nothing unresolved");

            // Bounded: a set this large is a finding in itself, and a hundred thousand
            // log lines would make the run's other evidence unreadable.
            const int Cap = 3000;
            int written = 0;

            foreach (int id in NetworkIdentityRegistry.UnresolvedIds)
            {
                if (written >= Cap) break;
                DebugConsole.Log($"[UNRESOLVED] {id}");
                written++;
            }

            return UnitTestResult.Pass(
                $"dumped {written} of {n} unresolved ids" + (written < n ? " (capped)" : ""));
        }
    }
}
