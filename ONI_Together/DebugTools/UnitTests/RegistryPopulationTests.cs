using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ONI_Together.DebugTools.UnitTests
{
	/// <summary>
	/// A colony's objects are in the registry, or nothing can be addressed at all.
	///
	/// This is the reconnect bug stated as an invariant. Dropping the client and
	/// rejoining left the world completely intact - twenty-one duplicants, 343 plants,
	/// every building - and the registry holding 41 entries out of 8085. Failed lookups
	/// went from 4,586 to 90,068 and the two peers had 31 ids in common instead of 8100.
	/// The client reported "connected" the whole time.
	///
	/// Nothing could see it. Every existing check asks whether the ids that are filed
	/// are correct, and they were: the handful still filed were fine. The question
	/// nobody asked was whether the colony was filed at all.
	///
	/// So this compares the registry against the world rather than against itself, which
	/// is the only way a wholesale emptying is visible. It is also the shape of check
	/// that catches the next cause rather than only this one - a save load, a host
	/// migration, anything that clears the registry while the world stays put.
	/// </summary>
	public static class RegistryPopulationTests
	{
		/// <summary>
		/// Below this, something has emptied the registry. A real colony files
		/// thousands; the broken state filed 31. Deliberately far from both, because a
		/// threshold that has to be tuned is a threshold that gets ignored.
		/// </summary>
		private const float MinimumFiledFraction = 0.5f;

		[UnitTest(name: "The loaded colony is in the registry", category: "NetId")]
		public static UnitTestResult ColonyIsRegistered()
		{
			if (Game.Instance == null)
				return UnitTestResult.Skip("no colony loaded");

			var identities = Object.FindObjectsByType<NetworkIdentity>(
				FindObjectsInactive.Exclude, FindObjectsSortMode.None);

			// Objects that are allowed to have no address do not count against this.
			// Scaffolding is refused at spawn and granted on demand, and ephemeral
			// matter is refused outright - a colony full of those is correct.
			int addressable = 0;
			int filed = 0;

			// What is unfiled, not just how much of it.
			//
			// Every cross-peer comparison in this project reads the NetId dump, and that
			// dump walks the registry rather than the world - so an object standing in a
			// colony with no address is invisible to it, and the comparer reports the
			// other peer's copy as "the client never built or received it". That sentence
			// is not something a registry walk can support, and it sent three rounds of
			// this investigation after a replication path for buildings that were never
			// missing: the client held 11,047 addressable objects against the host's
			// 9,632 and had filed 81% of them where the host filed 95%.
			//
			// Naming them is what separates "absent" from "not visible". Grouped by
			// prefab so the log stays readable, with a few cells each, because the
			// question that keeps coming up is whether one specific cell has the thing.
			var unfiledByPrefab = new Dictionary<string, int>();
			var unfiledCells = new Dictionary<string, List<int>>();

			foreach (var identity in identities)
			{
				if (identity.IsNullOrDestroyed() || identity.gameObject.IsNullOrDestroyed()) continue;
				if (NetworkIdentity.IsExcludedFromIdentity(identity.gameObject)) continue;

				addressable++;
				if (identity.NetId != 0 && NetworkIdentityRegistry.Holds(identity.NetId, identity))
				{
					filed++;
					continue;
				}

				string prefab = identity.gameObject.PrefabID().ToString();
				unfiledByPrefab.TryGetValue(prefab, out int n);
				unfiledByPrefab[prefab] = n + 1;

				if (!unfiledCells.TryGetValue(prefab, out var cells))
					unfiledCells[prefab] = cells = new List<int>();
				if (cells.Count < 6)
				{
					int c = Grid.PosToCell(identity.gameObject);
					if (Grid.IsValidCell(c)) cells.Add(c);
				}
			}

			foreach (var kv in unfiledByPrefab.OrderByDescending(kv => kv.Value).Take(20))
			{
				DebugConsole.Log(
					$"[UNFILED] {kv.Key}|{kv.Value}|" +
					string.Join(",", unfiledCells[kv.Key]));
			}

			// A world with almost no objects says nothing either way, and reporting a
			// pass over nothing is how three earlier zeroes were read as fixes.
			if (addressable < 100)
				return UnitTestResult.Skip($"only {addressable} addressable object(s) - too few to judge");

			float fraction = (float)filed / addressable;
			if (fraction < MinimumFiledFraction)
			{
				return UnitTestResult.Fail(
					$"{filed} of {addressable} addressable objects are in the registry " +
					$"({fraction:P0}) - the colony is loaded and cannot be addressed. " +
					$"Registry holds {NetworkIdentityRegistry.Count} entries. This is what a " +
					"reconnect used to leave behind: connected, and nothing nameable.");
			}

			return UnitTestResult.Pass(
				$"{filed} of {addressable} addressable objects filed ({fraction:P0}); " +
				$"{NetworkIdentityRegistry.ReattachedOnJoin} re-filed after " +
				$"{NetworkIdentityRegistry.ReattachSweeps} join sweep(s)");
		}
	}
}
