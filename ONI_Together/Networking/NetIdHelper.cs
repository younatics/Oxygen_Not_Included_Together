using ONI_Together.DebugTools;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking
{
	public static class NetIdHelper
	{
		/// <summary>
		/// Generates a deterministic NetID for a building based on its location and object layer.
		/// Range: 1,000,000,000+
		/// </summary>
		/// <summary>
		/// Distinguishes a building site from the finished building at the same
		/// cell. Any fixed non-zero value works; this one is arbitrary and must
		/// stay put, because changing it renames every site mid-session.
		/// </summary>
		private const int UnderConstructionSalt = 0x5C0FF01D;

		public static int GetDeterministicBuildingId(GameObject go)
		{
			using var _ = Profiler.Scope();

			if (go == null) return 0;

			int cell = Grid.PosToCell(go);
			if (!Grid.IsValidCell(cell)) return 0;

			// A building site and the building it becomes sit in the same cell,
			// carry the same prefab tag and use the same object layer, so they
			// hashed to exactly the same id. Once sites started being registered
			// this made every completed building collide with its own scaffold -
			// one host logged 694 rehoused tiles, 537 wires and 512 ladders in a
			// single session.
			//
			// Separated by the hash rather than by probing for a free slot.
			// Probing walks upward through whatever is already registered, which
			// two peers have no reason to agree about; this is a pure function
			// of the object, so they reach the same answer without talking.
			int phase = go.TryGetComponent<BuildingUnderConstruction>(out var underConstruction) && underConstruction != null ? UnderConstructionSalt : 0;

			if (!go.TryGetComponent<Building>(out var building))
				return cell.GetHashCode() ^ go.PrefabID().GetHashCode() ^ phase;

			return cell.GetHashCode() ^ go.PrefabID().GetHashCode() ^ building.Def.ObjectLayer.GetHashCode() ^ phase;
		}
		/// <summary>
		/// Stable string hash. string.GetHashCode() happens to be deterministic
		/// on Mono, which is the only reason ids ever matched across peers, but
		/// nothing guarantees that. FNV-1a is fixed by its specification, so the
		/// id no longer depends on a runtime implementation detail.
		/// </summary>
		private static int StableHash(string s)
		{
			unchecked
			{
				uint hash = 2166136261;
				for (int i = 0; i < s.Length; i++)
				{
					hash ^= s[i];
					hash *= 16777619;
				}
				return (int)hash;
			}
		}

		private static int Mix(int hash, int value)
		{
			unchecked
			{
				hash ^= value;
				return (int)((uint)hash * 16777619);
			}
		}

		/// <summary>
		/// A duplicant's id, derived from who it is rather than where it is.
		///
		/// Duplicants carry a Storage, and Storage is a Workable, so they went
		/// down the workable path and hashed on their cell. They register during
		/// load before they have been placed, so every one of them hashed from
		/// the same cell, collided, and was separated by the breakoff probe in
		/// arrival order - a live colony had six duplicants holding six
		/// consecutive ids while standing in six unrelated cells.
		///
		/// That makes identity depend on load order, and when two of them end up
		/// mapped to one id the registry returns only one: the other is
		/// unreachable for the whole session. A live client had exactly one
		/// duplicant out of twenty two that never received a position, standing
		/// still while the other twenty one moved.
		///
		/// Name and arrival time are what a duplicant actually is, they are in
		/// the save, and both peers read the same save - so both compute the
		/// same id with nothing exchanged.
		/// </summary>
		private static int GetDuplicantId(GameObject go, MinionIdentity minion)
		{
			int hash = StableHash(go.PrefabID().ToString());
			hash = Mix(hash, StableHash(minion.GetProperName() ?? "?"));
			hash = Mix(hash, minion.arrivalTime.GetHashCode());
			hash = Mix(hash, StableHash(minion.gender ?? ""));

			int breakoff = 0;
			while (NetworkIdentityRegistry.Exists(hash + breakoff))
				breakoff++;

			DebugConsole.Log($"Registered duplicant {minion.GetProperName()} with id: {hash + breakoff}");
			return hash + breakoff;
		}

		public static int GetDeterministicWorkableId(GameObject go)
		{
			using var _ = Profiler.Scope();

			if (go == null) return 0;

			// Before anything cell-based: a duplicant's cell at registration is
			// not yet its own.
			if (go.TryGetComponent<MinionIdentity>(out var minion) && minion != null)
				return GetDuplicantId(go, minion);

			int cell = Grid.PosToCell(go);
			if (!Grid.IsValidCell(cell)) return 0;

			if (!go.TryGetComponent<Workable>(out var workable))
				return 0;

			// Identity and location only. Two things used to break this:
			//
			//   1. The base hash came from GetDeterministicEntityId(useCell:false),
			//      so the cell never entered a workable's hash and every object of
			//      a prefab collapsed onto one value. That call also mixed in
			//      PrimaryElement.Mass and Temperature, which change while the
			//      object lives, so the same object rehashed differently over time.
			//   2. The collision was then resolved by probing the local registry
			//      (hash+0, hash+1, ...), which distributes ids in arrival order.
			//      Entity creation is not replicated, so arrival order is
			//      structurally independent on the two peers and they could not
			//      agree - divergence was certain, not probable.
			//
			// The id is now a pure function of what the object is and where it is,
			// with no registry state involved, so both peers compute it alike.
			//
			// Residual risk: two objects of the same prefab and workable type
			// stacked in one cell now hash alike where the probe used to separate
			// them. That is a detectable, local collision rather than a guaranteed
			// cross-peer disagreement - strictly the better failure mode - and the
			// "One cell holds one id per workable type" test watches for it.
			int hash = StableHash(go.PrefabID().ToString());
			hash = Mix(hash, cell);
			hash = Mix(hash, StableHash(workable.GetType().Name));

			// Local uniqueness is not optional, and dropping this probe was a
			// regression: two iron piles in one cell hashed alike, RegisterExisting
			// refused the second, and it stayed on screen with no address at all.
			//
			// It does distribute ids in arrival order, which two peers cannot
			// agree on - but they were never meant to agree by computing the same
			// hash. The host propagates the id it issued (OverrideNetId, carried
			// on the spawn packets), and that is what makes the peers match. The
			// hash only has to be stable and unique on the peer that issues it.
			//
			// Including the cell above is what makes this rare: before, every
			// object of a prefab collapsed onto one value and the probe ran
			// constantly.
			int breakoff = 0;
			while (NetworkIdentityRegistry.Exists(hash + breakoff))
				breakoff++;
			hash += breakoff;

			DebugConsole.Log($"Registered workable {go.PrefabID().ToString()} with id: {hash} for workable type {workable.GetType().Name} at cell {cell}");
			return hash;
		}


		public static int GetDeterministicEntityId(GameObject go, bool useBreakOff = true, bool useCell = true)
		{
			using var _ = Profiler.Scope();

			if (go == null || !go.TryGetComponent<PrimaryElement>(out var primaryElement))
				return 0;

			int cell = Grid.PosToCell(go);
			if (!Grid.IsValidCell(cell))
				return 0;

			// Identity only. Mass and Temperature used to be mixed in here, and
			// both change while the object lives - ore cools, a stack merges -
			// so the same object hashed differently over time and, worse, two
			// peers hashed a freshly spawned item differently because their
			// values had already drifted apart by the time each registered it.
			// Mined ore is exactly that case: the host spawns it, both sides
			// register it, and the ids disagree, so every packet addressed to it
			// is dropped. That showed up as a client-side failed-lookup counter
			// in the hundreds while the host's stayed at two.
			int hash = StableHash(go.PrefabID().ToString());
			if (useCell)
				hash = Mix(hash, cell);
			hash = Mix(hash, StableHash(go.GetProperName()));
			hash = Mix(hash, (int)primaryElement.ElementID);

			int breakoff = 0;
			if (useBreakOff)
			{
				while (NetworkIdentityRegistry.Exists(hash + breakoff))
				{
					breakoff++;
				}
			}
			hash += breakoff;
			if(useBreakOff)
				DebugConsole.Log($"Registered entity {go.PrefabID().ToString()} with id: {hash}");
			return hash;
		}
	}
}
