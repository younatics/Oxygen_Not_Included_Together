using System.Linq;
using ONI_Together.Networking.Components;

namespace ONI_Together.DebugTools.UnitTests
{
	/// <summary>
	/// One cell cannot hold both a finished building and that building's own
	/// unfinished construction site.
	///
	/// This is the reported symptom stated as an invariant rather than as a guess
	/// about where the leftover object lives. Two fixes were written from such
	/// guesses, and the sweep the second one added has now finished four builds a run
	/// twice without finding anything - so the assertion is what decides, not the
	/// sweep's counter.
	///
	/// Judged from one peer, deliberately. The report came from ordinary play, where
	/// there is no log comparison to run, and a contradiction inside a single colony
	/// needs no second opinion to be wrong.
	/// </summary>
	public static class GhostSiteTests
	{
		[UnitTest(name: "No cell holds a building and its own construction site",
			category: "Build")]
		public static UnitTestResult NoGhostSites()
		{
			int ghosts = GhostSiteScan.Scan();

			if (ghosts > 0)
			{
				return UnitTestResult.Fail(
					$"{ghosts} cell(s) hold a finished building and its own unfinished site " +
					$"at the same time: {string.Join(", ", GhostSiteScan.Examples)}. " +
					"On a client this reads as a tile that is built and scheduled at once.");
			}

			// Says what the pass covered. A pass over nothing is the failure mode this
			// project has misread three times, so it is reported rather than hidden.
			return UnitTestResult.Pass(
				$"{GhostSiteScan.SitesScanned} construction site(s) examined, none contradicted");
		}
	}
}
