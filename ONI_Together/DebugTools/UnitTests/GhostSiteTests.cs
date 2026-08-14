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
			int appeared = GhostSiteScan.GhostSitesNew;

			// Judged on what appeared during this session, not on what the save shipped
			// with.
			//
			// Wire@53893 has failed this in every run of this project: the same cell
			// every time, on both peers, in a colony re-cloned from the same save before
			// each run, and the scenario never builds there - where it builds moves with
			// the duplicants and that cell is not among them in any run. It is in the
			// save file, both peers load it, and it says nothing about replication.
			//
			// A gate that is permanently red is a gate nobody reads, and this one was
			// the only failing test left on the host. What it exists to catch is a cell
			// that becomes contradictory while the two peers are playing.
			if (appeared > 0)
			{
				return UnitTestResult.Fail(
					$"{appeared} cell(s) became a building and its own unfinished site during " +
					$"this session: {string.Join(", ", GhostSiteScan.Examples)}. " +
					"On a client this reads as a tile that is built and scheduled at once.");
			}

			// Says what the pass covered. A pass over nothing is the failure mode this
			// project has misread three times, so it is reported rather than hidden.
			return UnitTestResult.Pass(
				$"{GhostSiteScan.SitesScanned} construction site(s) examined, none became " +
				$"contradictory here ({ghosts} arrived that way in the save)");
		}
	}
}
