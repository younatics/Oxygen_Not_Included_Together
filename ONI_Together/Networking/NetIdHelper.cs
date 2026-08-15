using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
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

		/// <summary>
		/// One salt per id function, so the three do not draw from the same range.
		///
		/// They all end in an int and there is nothing to stop two of them landing on
		/// the same value, but without salts they do it structurally rather than by
		/// chance: same arithmetic, same inputs, overlapping output. A real colony
		/// produced a building-versus-pickupable collision on nearly every run, and
		/// the suite has a test named for exactly this - "The three id functions do
		/// not overlap" - which is how it was found.
		/// </summary>
		private const int BuildingSalt = 0x42D1D501;
		private const int WorkableSalt = 0x7A6B1E03;
		private const int EntitySalt   = 0x13C7F905;
		private const int StoredItemSalt = 0x5701ED07;

		/// <summary>
		/// Separates a displaced object from its neighbours' hashes.
		///
		/// Arbitrary and fixed, like the other salts. What matters is only that
		/// consecutive attempts land far apart, so a walked id does not squat on the
		/// value some other prefab will compute for itself later.
		/// </summary>
		private const int WalkSalt = 0x2F1B3C5D;

		/// <summary>
		/// Enough tries that running out means something is genuinely wrong rather
		/// than crowded. Sixteen re-mixes into a 32-bit space is already astronomically
		/// unlikely to be exhausted by a colony's worth of objects.
		/// </summary>
		private const int MaxWalkAttempts = 16;

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

			// Mixed, not XORed, and salted apart from the other two id functions.
			//
			// This was `cell ^ prefabHash ^ layer ^ phase`, and XOR keeps the bits
			// it is given. int.GetHashCode() is the int itself, so the cell went in
			// undiffused: two prefabs whose hashes differ only in the low bit, in
			// adjacent cells, produce exactly the same id. A real colony hit that
			// on nearly every run - SuitMarker at 51571 and Iron at 51570 sharing
			// one id, DreckoBaby and SandStone sharing another - and two objects
			// under one id means half the packets about either land on the wrong
			// one.
			//
			// The salt matters as much as the mixing. Workables hash through Mix
			// already, so with no salt the two functions draw from the same
			// structured range and collide with each other far more often than
			// chance; separating the streams makes an overlap a genuine hash
			// coincidence rather than an artefact of using the same arithmetic.
			//
			// Ids are [Serialize]d, so nothing already in a save is renumbered -
			// only objects created from here on, and both peers run the same build.
			int hash = StableHash(go.PrefabID().ToString());
			hash = Mix(hash, cell);
			hash = Mix(hash, phase);
			hash = Mix(hash, BuildingSalt);

			if (!go.TryGetComponent<Building>(out var building))
				return hash;

			return Mix(hash, building.Def.ObjectLayer.GetHashCode());
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
		/// Suppresses the "Registered ..." lines while an id is being recomputed
		/// rather than assigned.
		///
		/// Without it, asking four hundred objects what their id should be writes
		/// four hundred lines claiming they were just registered - into the same log
		/// the next investigation reads.
		/// </summary>
		private static bool _quiet;

		/// <summary>
		/// The id this object should have, by whichever rule its kind uses.
		///
		/// One definition, so that a test asking "would this object move" and the
		/// code that decides whether to move it cannot answer differently.
		/// </summary>
		/// <summary>
		/// The last workable id computation, as prefab|cell|workableType|baseHash|finalId.
		/// Diagnostic only - read by the NetId dump so the two peers can be diffed on the
		/// inputs rather than on the answer.
		/// </summary>
		public static string LastIdInputs { get; private set; } = "";

		/// <summary>
		/// Free-slot walks a client declined, keeping the number both peers computed.
		/// Non-zero means the collision the walk exists for does happen on clients, and
		/// is now being reported instead of routed around.
		/// </summary>
		public static int ClientWalksSkipped { get; private set; }

		public static int GetDeterministicIdFor(GameObject go, bool quiet = true)
		{
			if (go == null) return 0;

			bool previous = _quiet;
			_quiet = quiet;
			try
			{
				if (go.TryGetComponent<Building>(out _))
					return GetDeterministicBuildingId(go);
				if (go.TryGetComponent<Workable>(out _))
					return GetDeterministicWorkableId(go);
				return GetDeterministicEntityId(go);
			}
			finally
			{
				_quiet = previous;
			}
		}

		private static void Note(string message)
		{
			if (!_quiet) DebugConsole.Log(message);
		}

		/// <summary>
		/// The first id at or after <paramref name="hash"/> that this object may
		/// have - counting the slot it already holds as available to it.
		///
		/// Every probe in this file used to ask only "is this id taken", and an
		/// object that already held the id it was recomputing answered yes to
		/// itself. It then walked one slot past its own correct address.
		///
		/// That is what the eggs were doing, and it explains the shape of the
		/// disagreement that six earlier attempts could not: the two peers differed
		/// by exactly one, and which peer was high swapped between runs. A live run
		/// shows both halves at once - the host's egg arrives at cell 61849 holding
		/// some unrelated id, finds the cell's id free and takes it; the client's
		/// egg is already sitting on that same id because the host told it to, sees
		/// the slot "occupied", and walks to the next one. The peer that was right
		/// is the one that moves.
		///
		/// So the probe has to know who is asking. With that, recomputing an id an
		/// object already holds returns the same id, which is what "deterministic"
		/// was supposed to mean.
		/// </summary>
		/// <summary>
		/// The hash the last computation produced before any walk.
		///
		/// Exists so a test can tell the two reasons a recomputed id differs from
		/// the one an object holds apart. An id is issued once and kept - a rock
		/// that has been carried, a wire that has finished construction, a plant
		/// that has been renamed all hash differently now than when they were named,
		/// and keeping the original is the whole point. That is not the defect.
		///
		/// The defect is an object whose hash still comes out exactly where it is
		/// sitting, being walked off it anyway. Comparing against the pre-walk hash
		/// separates the two; comparing against the held id alone cannot, which made
		/// the first version of the test fail on six objects that were all correct.
		///
		/// Only meaningful immediately after a call, and only single-threaded, which
		/// is what the tests do and all this is for.
		/// </summary>
		internal static int LastBaseHash { get; private set; }

		private static int WalkToFreeSlot(GameObject go, int hash)
		{
			using var _ = Profiler.Scope();

			LastBaseHash = hash;

			// A client never walks. It takes the number it computed and stops.
			//
			// The walk exists so two objects that hash alike on one peer still get
			// separate ids there, and for a host issuing addresses that is right. On a
			// client it destroys the only thing that makes the peers agree, because
			// which slots are occupied is local state and the two peers fill them in
			// different orders.
			//
			// Measured on the animal that has failed this all session, with both peers
			// printing their inputs side by side for the first time:
			//
			//   host    CrabBaby cell 62391  netId 881448091  base 1756521244
			//   client  CrabBaby cell 62390  netId 0          base  881448091 -> 776236978
			//
			// The client's base hash is the host's id exactly. Both peers computed the
			// same number from the same animal, and then the walk threw it away on the
			// client because that slot was already taken here - after which the animal
			// had no address at all. Six rounds went into when the id is computed; this
			// is the first look at what it is computed from, and the answer was that the
			// computation already agreed.
			//
			// A taken slot on a client is now a collision to report rather than one to
			// route around: routing around it guarantees the disagreement it is trying
			// to avoid.
			// Reconnect included, for the same reason the preview marking is: walking to
			// a free slot invents an address, and the host is the only peer allowed to.
			if (MultiplayerSession.IsClientOrReconnecting)
			{
				ClientWalksSkipped++;
				return hash;
			}

			// Not every caller has an identity yet - this runs during registration,
			// and on that path there is no self to exempt.
			NetworkIdentity self = null;
			if (go != null && go.TryGetComponent<NetworkIdentity>(out var found) && !found.IsNullOrDestroyed())
				self = found;

			// Re-mixed per attempt, not hash+1.
			//
			// Walking upward lands on the next integer, and the next integer is some
			// other prefab's natural hash. Traced end to end on a live pair: the host
			// walked a Creature pile onto -885421119, announced it, the pile died, and a
			// MushBar then hashed to exactly that value and took it - while the client
			// still held the dead Creature there. Every packet about the host's MushBar
			// landed on the client's corpse. The same two ids collided again on the next
			// run, because a deterministic hash collides in the same place every time.
			//
			// Mixing the attempt into the hash puts a displaced object somewhere
			// unrelated instead of on its neighbour's doorstep. It stays deterministic
			// for the peer that computes it, which is all the walk ever needed to be -
			// the two peers agree by propagation, not by both walking the same way.
			int candidate = hash;
			for (int attempt = 1; attempt <= MaxWalkAttempts; attempt++)
			{
				// Exists, not ExistsOrRetired.
				//
				// Refusing to reissue a retired id was meant to stop a new object taking
				// the number the other peer still holds for a dead one. Measured: the
				// disagreement count stayed inside its usual 2-8 band while the host's
				// collision count went from 0 to 13 and 10. Objects unregister and
				// re-register during their lives - a cell change, a move into storage -
				// and retirement stops them getting their own number back.
				//
				// A measured cost against an unmeasured benefit is not a fix. The
				// retirement bookkeeping stays because retiredIds is worth seeing; only
				// the refusal is gone.
				// Held by somebody else, or free. Retirement is deliberately not
				// consulted - see below.
				//
				// Refusing to reissue retired ids was tried twice and measured worse
				// both times. First bluntly: the host's collisions went 0 to 13 because
				// objects unregister and re-register constantly during normal play.
				// Then with the former owner allowed to reclaim its own number: still 14
				// and 17, because ONI pools pickupables - a recycled object comes back
				// with a new instance id, so "the same object" cannot be recognised at
				// all. The reservation comments in this file already warn about that
				// pooling; I did not carry it into the ownership check.
				//
				// The reuse chain is real - a dropped pile is announced, dies, and its
				// number is handed to something new while the client still holds the
				// dead one - but retirement is not the place to break it. Recorded in
				// REFACTOR_BACKLOG.md so it is not attempted a third time.
				if (!NetworkIdentityRegistry.Exists(candidate)) return candidate;
				if (self != null && NetworkIdentityRegistry.Holds(candidate, self)) return candidate;

				candidate = Mix(hash, attempt * WalkSalt);
				if (candidate == 0) candidate = attempt;
			}

			// Out of attempts. Returning the last candidate keeps the old behaviour of
			// always producing something rather than leaving the object nameless; a
			// collision here is reported by the registry and by the bulk-rehouse test.
			return candidate;
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

			hash = WalkToFreeSlot(go, hash);

			Note($"Registered duplicant {minion.GetProperName()} with id: {hash}");
			return hash;
		}

		/// <summary>
		/// An id for an item that is inside something, keyed off the container
		/// rather than off a map position it does not have.
		///
		/// Several identical items in one container still land on the same base
		/// value and are separated by the probe, which two peers have no reason to
		/// walk in the same order - so this is not perfect. It is strictly better
		/// than the alternative it replaces, where every stored item in the colony
		/// shared one cell and collided with buildings as well as with each other.
		/// </summary>
		/// <summary>
		/// Both ids an object could ever be given, before any walk.
		///
		/// An item is named one way while it lies on the ground and another way once it
		/// is in a container, and it moves between those states constantly. The suite
		/// only ever compared the ids objects hold *right now*, which cannot see a
		/// collision between two objects that are never registered at the same moment -
		/// and that is exactly the collision found in a live pair: the host's MushBar,
		/// stored in a refrigerator at cell 47726, holds the number its Creature pile at
		/// that cell had held earlier and announced to the client.
		///
		/// Returning both forms lets a test ask the question without a time axis: does
		/// any id this object could take collide with any id another object could take.
		/// The walk is deliberately excluded - it depends on what happens to be
		/// registered, and the question here is about the hashes themselves.
		/// </summary>
		public static void BaseHashes(GameObject go, out int loose, out int stored)
		{
			loose = 0;
			stored = 0;
			if (go == null) return;

			int cell = Grid.PosToCell(go);
			if (!go.TryGetComponent<Workable>(out var workable)) return;

			if (Grid.IsValidCell(cell))
			{
				int h = StableHash(go.PrefabID().ToString());
				h = Mix(h, cell);
				h = Mix(h, StableHash(workable.GetType().Name));
				loose = Mix(h, WorkableSalt);
			}

			if (go.TryGetComponent<Pickupable>(out var pickupable)
				&& !pickupable.IsNullOrDestroyed()
				&& pickupable.storage != null
				&& !pickupable.storage.IsNullOrDestroyed())
			{
				int containerCell = Grid.PosToCell(pickupable.storage.gameObject);
				int h = StableHash(go.PrefabID().ToString());
				h = Mix(h, StableHash(pickupable.storage.gameObject.PrefabID().ToString()));
				h = Mix(h, containerCell);
				h = Mix(h, StableHash(workable.GetType().Name));
				stored = Mix(h, StoredItemSalt);
			}
		}

		private static int GetStoredItemId(GameObject go, Storage container, Workable workable)
		{
			int containerCell = Grid.PosToCell(container.gameObject);

			int hash = StableHash(go.PrefabID().ToString());
			hash = Mix(hash, StableHash(container.gameObject.PrefabID().ToString()));
			hash = Mix(hash, containerCell);
			hash = Mix(hash, StableHash(workable.GetType().Name));
			hash = Mix(hash, StoredItemSalt);

			hash = WalkToFreeSlot(go, hash);

			Note(
				$"Registered stored {go.PrefabID()} with id: {hash} inside " +
				$"{container.gameObject.PrefabID()} at cell {containerCell}");
			return hash;
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

			// An item inside a container has no place on the map, and cell 0 is a
			// real cell, so the guard above lets it through and every stored item
			// hashes as though it were standing in the top-left corner of the
			// asteroid.
			//
			// They then collide with each other and with whatever else happens to
			// land on that value. A real colony showed it plainly: NetId 1776225777
			// was an Atmo_Suit at "cell 0" on the host and a SuitLocker at 51570 on
			// the client, and the same locker turned up under two ids with its
			// damage on opposite sides. Earlier runs produced Iron and SandStone in
			// the same role - all of them items sitting in storage.
			//
			// Hashing from the container instead gives a stored item somewhere real
			// to hang off, and the container's own id is a pure function of prefab
			// and cell, so both peers still reach the same answer without talking.
			if (go.TryGetComponent<Pickupable>(out var pickupable)
				&& !pickupable.IsNullOrDestroyed()
				&& pickupable.storage != null
				&& !pickupable.storage.IsNullOrDestroyed())
			{
				return GetStoredItemId(go, pickupable.storage, workable);
			}

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
			hash = Mix(hash, WorkableSalt);

			// Kept so the two peers can be compared on their inputs, not just their
			// answers.
			//
			// Six rounds went into when this is called and none into whether the two
			// peers compute it from the same things. The base hash is a pure function of
			// prefab, cell and workable type; the walk below is not, because it probes
			// this peer's registry. Recording both separates "the peers disagree about
			// what the object is or where it is" from "they agree and the walk moved
			// one of them", and those need opposite fixes.
			LastIdInputs = $"{go.PrefabID()}|{cell}|{workable.GetType().Name}|{hash}";

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
			hash = WalkToFreeSlot(go, hash);
			LastIdInputs += $"|{hash}";

			Note($"Registered workable {go.PrefabID().ToString()} with id: {hash} for workable type {workable.GetType().Name} at cell {cell}");
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
			hash = Mix(hash, EntitySalt);

			if (useBreakOff)
				hash = WalkToFreeSlot(go, hash);
			if (useBreakOff)
				Note($"Registered entity {go.PrefabID().ToString()} with id: {hash}");
			return hash;
		}
	}
}
