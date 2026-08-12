using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	/// <summary>
	/// Finds cells that hold a finished building and that same building's unfinished
	/// construction site at the same time.
	///
	/// This is the shape of a bug reported from a live session: the host had placed
	/// tiles and finished them, and on the client some of those tiles still read as
	/// "scheduled for construction". Two attempts to fix it by guessing where the
	/// leftover site lives were both wrong, and the leftover-scaffold sweep in
	/// BuildCompletePacket - written for exactly this - has now completed four builds
	/// a run twice without ever finding anything to clear.
	///
	/// So this stops guessing and measures instead. The important property is that
	/// the contradiction is visible from one peer alone: a cell cannot legitimately
	/// contain both Tile and Tile-under-construction, no matter what the other side
	/// thinks. That makes it detectable in an ordinary play session with no log
	/// comparison, no scenario, and no second machine - which is what the previous
	/// approach lacked, since the report came from play and the lab never reproduced
	/// it.
	///
	/// Bounded by the number of construction sites in the colony, not by the number
	/// of cells: a full grid sweep would be 400k cells times 40 layers a minute.
	/// </summary>
	public static class GhostSiteScan
	{
		/// <summary>Cells found holding a building and its own site at once, last scan.</summary>
		public static int GhostSites { get; private set; }

		/// <summary>Highest count any single scan has seen this session.</summary>
		public static int GhostSitesWorst { get; private set; }

		/// <summary>Construction sites examined, last scan - the activity number.</summary>
		public static int SitesScanned { get; private set; }

		/// <summary>
		/// A ghost, described. Kept as text because the objects it names may be gone
		/// by the time anybody reads the log.
		/// </summary>
		private static readonly List<string> _examples = new List<string>();

		public static IReadOnlyList<string> Examples => _examples;

		public static void Reset()
		{
			GhostSites = 0;
			GhostSitesWorst = 0;
			SitesScanned = 0;
			_examples.Clear();
		}

		/// <summary>
		/// Scan the colony once. Returns the number of contradicting cells.
		/// </summary>
		public static int Scan()
		{
			int ghosts = 0;
			int scanned = 0;
			_examples.Clear();

			if (Game.Instance == null)
			{
				GhostSites = 0;
				SitesScanned = 0;
				return 0;
			}

			// Sites first, finished buildings second. Walking sites and then asking
			// "is the finished version of this also here" is the cheap direction:
			// there are a handful of sites and thousands of finished buildings.
			foreach (var building in Object.FindObjectsByType<Building>(
						 FindObjectsInactive.Exclude, FindObjectsSortMode.None))
			{
				if (building.IsNullOrDestroyed()) continue;
				if (!(building is BuildingUnderConstruction)) continue;

				int cell = Grid.PosToCell(building.gameObject);
				if (!Grid.IsValidCell(cell)) continue;
				scanned++;

				string prefab = building.gameObject.PrefabID().Name;

				// Every layer, because which layer the site sits on is the thing this
				// bug has already survived two guesses about. Reading all of them
				// costs one array index each and removes the question.
				for (int layer = 0; layer < (int)ObjectLayer.NumLayers; layer++)
				{
					var other = Grid.Objects[cell, layer];
					if (other == null || other.IsNullOrDestroyed()) continue;
					if (ReferenceEquals(other, building.gameObject)) continue;

					// Finished, not another site: two sites for one building at one
					// cell is a different fault and not what was reported.
					if (other.TryGetComponent<BuildingUnderConstruction>(out var alsoSite)
						&& !alsoSite.IsNullOrDestroyed())
						continue;
					if (!other.TryGetComponent<Building>(out var finished) || finished.IsNullOrDestroyed())
						continue;

					// Same building, or it is a legitimate pair - a wire on the wire
					// layer under a tile being built on the foundation layer is normal
					// and must not be reported.
					if (other.PrefabID().Name != prefab) continue;

					ghosts++;
					if (_examples.Count < 8)
						_examples.Add($"{prefab}@{cell} layer={(ObjectLayer)layer}");
					break;
				}
			}

			GhostSites = ghosts;
			SitesScanned = scanned;
			if (ghosts > GhostSitesWorst) GhostSitesWorst = ghosts;
			return ghosts;
		}
	}
}
