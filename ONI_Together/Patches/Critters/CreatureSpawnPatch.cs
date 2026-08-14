using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Scripts.Creatures;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.Critters
{
	/// <summary>
	/// Gives every critter a network identity at spawn, whatever built it.
	///
	/// EntityTemplatesPatch attaches these at template time, but it hooks one
	/// overload of ExtendEntityToBasicCreature, so anything assembled by another
	/// route never got them. A live host logged "no netId found on" sixteen
	/// times for pokeshells, pokeshell juveniles and a hatch morph - and a
	/// creature with no id is one the host cannot say anything about at all: not
	/// where it is, not what it is doing. Moving one into a ranch simply did not
	/// reach the client, because there was nothing to address.
	///
	/// Hooked at spawn rather than at template time on purpose. Templates are a
	/// graph of overloads and extension helpers that changes between game
	/// updates; OnSpawn is the one thing every critter in the world has done.
	/// This mirrors BuildingSpawnPatch, which had the same problem with
	/// buildings and solved it the same way.
	/// </summary>
	[HarmonyPatch(typeof(KPrefabID), nameof(KPrefabID.OnSpawn))]
	public static class CreatureSpawnPatch
	{
		/// <summary>
		/// Critters given an address at spawn despite having no anim controller yet.
		/// These used to get nothing, and a critter with no address does not move on
		/// the other peer.
		/// </summary>
		public static int CrittersWithoutAnim { get; private set; }

		/// <summary>Critters the host told its clients about as they spawned.</summary>
		public static int CrittersAnnounced { get; private set; }

		/// <summary>
		/// Tell the clients about a critter the host has just made.
		///
		/// An egg hatches on the host and only on the host - the client's incubation is
		/// suppressed, correctly, because both peers running it produces two babies with
		/// two ids. What was missing is the other half: nothing then told the client the
		/// baby exists. Measured as the host's registry holding a HatchBaby the client's
		/// did not, and before that as a critter the suite reported having no id at all.
		///
		/// Sent only when a client is actually there. During a load or a hard sync the
		/// send gate already withholds world traffic from a peer that is still loading,
		/// so a save load with nobody connected announces nothing.
		///
		/// Safe to repeat now, and not before: SpawnPrefabPacket used to build whatever
		/// it was told to, so a client that already had the object got a second one. It
		/// checks the registry first as of this change.
		/// </summary>
		private static void AnnounceIfHostSpawned(GameObject go)
		{
			if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession) return;
			if (!MultiplayerSession.SessionHasPlayers) return;

			if (!go.TryGetNetIdentity(out var identity) || identity.NetId == 0) return;

			Networking.PacketSender.SendToAllClients(new Networking.Packets.World.SpawnPrefabPacket(
				identity.NetId, go.PrefabID().GetHashCode(), go.transform.position));
			CrittersAnnounced++;
		}

		public static void Postfix(KPrefabID __instance)
		{
			if (__instance == null) return;
			Attach(__instance.gameObject);
		}

		/// <summary>
		/// Attach identity and syncers to one object, from whichever hook reaches it first.
		///
		/// KPrefabID.OnSpawn is not always late enough. A CrabBaby keeps arriving with an
		/// identity attached lazily by whichever peer first sent a packet about it, and
		/// widening the gate here changed nothing - critterNoAnim stayed 0 - so the object
		/// is not tagged Creature yet when that hook runs. Navigator.OnSpawn is the second
		/// hook: everything that walks has one, and by the time it spawns the tags are
		/// settled.
		///
		/// Idempotent by construction. AddOrGet returns what is already there and
		/// RegisterIdentity leaves an existing id alone, so being reached twice costs
		/// nothing and reaching an object from a third hook later would too.
		/// </summary>
		internal static void Attach(GameObject go)
		{
			using var _ = Profiler.Scope();
			try
			{
				if (go.IsNullOrDestroyed()) return;

				// Eggs, before the critter check rejects them.
				//
				// An egg is not tagged Creature, so it fell outside this patch and
				// got its identity the lazy way instead - attached by whichever peer
				// first tried to send a packet about it. That is one-sided by
				// construction: the sender has an address and the peer holding the
				// same egg never asked for one, so everything sent about it is
				// dropped. The suite reports it every run as "1 prefabs only got an
				// identity when a packet needed one: PuftBleachstoneEgg".
				//
				// An egg does not need the position handler or the anim syncer - it
				// does not walk and it is not animated - so it only gets the
				// identity, attached at the same point on both peers.
				if (IsEgg(go))
				{
					var eggIdentity = go.AddOrGet<NetworkIdentity>();
					eggIdentity.RegisterIdentity();

					// And make sure the id is the one the final cell implies.
					//
					// The host may already have minted one earlier, from wherever the
					// egg was when something first asked about it - RegisterIdentity
					// leaves an existing id alone, so without this the host keeps the
					// early one and the client mints a different one here.
					eggIdentity.ConvergeOnDeterministicId();

					// Two hypotheses, and the count alone cannot separate them.
					//
					// Eggs still turn up as lazily attached - PacuEgg, DreckoEgg and
					// PacuTropicalEgg, four runs in ten - with a prefab tag that ends
					// in "Egg", which is exactly what IsEgg matches. So either this
					// hook never runs for those objects, or it runs and the component
					// does not survive to the moment something asks for its id.
					//
					// If this line appears and the lazy warning still appears for the
					// same prefab, the identity is being lost after spawn. If this
					// line never appears, the hook is not reached. One run settles it.
					DebugTools.ThrottledLog.Info(
						$"[EggIdentity] attached at spawn to '{go.PrefabID()}'#{go.GetInstanceID()} " +
						$"(netId={eggIdentity.NetId})");
					return;
				}

				// An address for every critter; the anim syncer only for the animated.
				//
				// This asked IsAnimatedCritter for both, and that check requires a
				// KBatchedAnimController - which the anim syncer genuinely needs and an
				// identity does not. A critter that failed it got nothing at all, and a
				// critter with no address is one the host cannot say anything about:
				// not where it is, not what it is doing. The suite reports it every run
				// as "1 of 80 creatures have no NetId: CrabBaby", with the host's own
				// log agreeing - "'CrabBaby' had no identity until a packet needed one"
				// - and the note there saying it should have been attached at spawn on
				// both peers.
				//
				// The test that reports it uses the tag pair alone, which is the honest
				// definition of a critter. Matching it here means the check and the
				// thing it checks now agree about what qualifies.
				if (!go.HasTag(GameTags.Creature) || go.HasTag(GameTags.BaseMinion))
					return;

				// Adding a component during OnSpawn means Unity will not call the
				// new component's own OnSpawn, so registration is driven by hand -
				// the same reason BuildingSpawnPatch calls RegisterIdentity here.
				go.AddOrGet<EntityPositionHandler>();
				go.AddOrGet<CreatureMultiplayerInitializer>();

				// Whether this animal already had an address before we touched it.
				//
				// Read first, because RegisterIdentity fills it in and the answer is the
				// whole point: an identity that was already there came from the ordinary
				// path and is already the same on both peers, and moving it is pure churn.
				// Not "does it have one" - "was it given one before it spawned".
				//
				// The first version asked the former and converged only objects that
				// arrived here with nothing, which excluded exactly the case this is for:
				// a lazily addressed animal has an identity by the time this runs,
				// attached four milliseconds earlier by whatever asked. lazyFixed read 0
				// for two whole runs because of it, on both peers.
				//
				// This asks whether the kind is on the lazily-addressed list, which is
				// the small set whose ids came from an arbitrary moment rather than from
				// where the animal is.
				bool addressedEarly = NetworkIdentity.WasAddressedBeforeSpawn(go);

				var critterIdentity = go.AddOrGet<NetworkIdentity>();
				critterIdentity.RegisterIdentity();

				// The id the cell implies, not whichever one got minted first.
				//
				// The egg branch above already does this and says why: RegisterIdentity
				// leaves an existing id alone, so a host that minted one early keeps it
				// while the other peer computes a different one - and a client will not
				// mint at all, so it keeps nothing.
				//
				// That is exactly what CrabBaby has been doing. The host logs "'CrabBaby'
				// had no identity until a packet needed one", so its id came from whenever
				// something first asked rather than from where the animal is, and the
				// client ends the run holding the same animal with no id: "1 of 79
				// creatures have no NetId". Two spawn hooks and a host-side announcement
				// left it unchanged, because the problem was never when the component was
				// attached - it was which number went on it.
				//
				// Only for animals that arrived here without one. Calling it on every
				// critter at load moved ids that were already correct: converges refused
				// went from 2 to 18 and the client's failed lookups from 1,724 to 2,465
				// in the run that did that. The lazily addressed ones are the case, and
				// they are the ones that had no identity a moment ago.
				if (addressedEarly)
					critterIdentity.ConvergeOnDeterministicId();

				AnnounceIfHostSpawned(go);

				// Critters' vitals are replicated now, like duplicants'.
				//
				// Nothing was sending them. The state comparison put 448 critter amount
				// rows side by side for the first time and 71 to 78 of them differed
				// every run - Calories, Fertility, Age, temperature - which looked like
				// drift and was not: the client had never been told any of these once.
				// Its animals were simply living a separate life, and a hatch that is
				// starving on one peer and fed on the other is not a rounding difference.
				//
				// The same syncer as duplicants, so there is one implementation to be
				// right rather than two. It is Unreliable and once a second, and the
				// packet is idempotent set-last-value, so a dropped one costs a second.
				// The cost is real and bounded: a colony has tens of critters against
				// twenty-odd duplicants, and this is the same order of traffic again on a
				// stream that was already the cheap one.
				//
				// This also gives the comparison a yardstick it did not have. Every
				// applied correction is one sync period of that animal's drift, measured
				// on the peer that receives it, which is what the duplicant rows are
				// already judged against.
				go.AddOrGet<Networking.Synchronization.VitalStatsSyncer>();

				if (AnimSyncEligibility.IsAnimatedCritter(go))
				{
					go.AddOrGet<AnimStateSyncer>();
				}
				else
				{
					// Counted, because this is the case that used to fall through. If
					// CrabBaby appears here the missing controller was the reason; if it
					// does not, the Creature tag is not set this early and the hook has
					// to move rather than widen.
					CrittersWithoutAnim++;
					DebugTools.ThrottledLog.Warn(
						$"[CreatureSpawn] '{go.PrefabID()}' is a critter with no anim " +
						"controller at spawn - given an identity, no anim syncer");
				}
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[CreatureSpawnPatch] {ex}");
			}
		}

		/// <summary>
		/// An egg, by whichever signal exists this early in spawn.
		///
		/// The incubation state machine is what an egg actually has, and checking
		/// for it alone did not work: at KPrefabID.OnSpawn the state machine
		/// instance has not been attached yet, so the check saw nothing and the egg
		/// went on getting its identity lazily - the run after the fix reported
		/// PuftOxyliteEgg exactly as before.
		///
		/// The prefab tag is available immediately, which is the whole reason to
		/// fall back to it. Matching on a name is matching on data and it will miss
		/// a modded egg that is named differently; the state machine check is kept
		/// first so anything reached later is caught properly.
		/// </summary>
		internal static bool IsEgg(UnityEngine.GameObject go)
		{
			if (go == null) return false;
			if (go.GetComponent<IncubationMonitor.Instance>() != null) return true;

			if (go.TryGetComponent<KPrefabID>(out var kpid) && kpid != null)
			{
				string tag = kpid.PrefabTag.Name;
				if (!string.IsNullOrEmpty(tag) && tag.EndsWith("Egg", System.StringComparison.Ordinal))
					return true;
			}

			// The object's own name, for the moment before the tag exists.
			//
			// Attaching at OnPrefabInit did not help, and the reason was the same one
			// that defeated the incubation-monitor check earlier: at that point the
			// prefab tag is not populated either, so IsEgg said no and nothing was
			// attached. The patch applied - the method count went from 296 to 300 -
			// and simply never matched.
			//
			// Unity has named the clone after its prefab by then, which is the only
			// identifying thing an egg has that early. Matching on a name is matching
			// on data and will miss a modded egg named differently, which is why the
			// two stronger checks are tried first.
			string objectName = go.name;
			return !string.IsNullOrEmpty(objectName)
				&& StripInstanceSuffix(objectName).EndsWith("Egg", System.StringComparison.Ordinal);
		}

		/// <summary>Unity clones are named "PacuEgg(Clone)" or "PacuEgg(123456)".</summary>
		private static string StripInstanceSuffix(string name)
		{
			int open = name.IndexOf('(');
			return open < 0 ? name : name.Substring(0, open);
		}
	}

	/// <summary>
	/// Attach an egg's identity when the egg is created, not when it is placed.
	///
	/// The spawn hook was right and the egg test was right; the timing was not. The
	/// two probes settled it by printing the instance id and the timestamp on both
	/// events, for the same object:
	///
	///   23:19:01  'PacuEgg'#-740728 had no NetworkIdentity when
	///             Extensions.GetNetIdentity asked for its id
	///   23:19:45  attached at spawn to 'PacuEgg'#-740728 (netId=-1937518084)
	///
	/// The lazy attach came FIRST, forty-four seconds before OnSpawn ran. A laid egg
	/// exists long before it is placed in the world, and in that gap the host asks
	/// for its id and attaches one itself. The client never asks, so it never
	/// attaches, and the two peers end up naming that egg differently or not at all.
	///
	/// Two earlier attempts changed how an egg is recognised - first the incubation
	/// state machine, then the prefab tag - and both were fixes to something that was
	/// not broken. Nothing about the identification was wrong. Only the moment.
	///
	/// OnPrefabInit is when the object comes into existence, so from here on nothing
	/// can find an egg without an identity. Registration still happens at OnSpawn,
	/// where the egg has its final cell - the id is derived from that cell, so
	/// registering earlier would give the two peers different answers.
	/// </summary>
	[HarmonyPatch(typeof(KPrefabID), nameof(KPrefabID.OnPrefabInit))]
	public static class EggIdentityAtCreationPatch
	{
		public static void Postfix(KPrefabID __instance)
		{
			using var _ = Profiler.Scope();
			try
			{
				if (__instance == null) return;

				var go = __instance.gameObject;
				if (!CreatureSpawnPatch.IsEgg(go)) return;

				// Component only. An id computed here would be computed from a cell
				// the egg has not settled into yet.
				go.AddOrGet<NetworkIdentity>();
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[EggIdentityAtCreationPatch] {ex}");
			}
		}
	}

	/// <summary>
	/// The second hook, for critters whose tags are not ready at KPrefabID.OnSpawn.
	///
	/// Everything that walks has a Navigator, and it spawns after the template has
	/// finished tagging. This is what catches the baby that kept getting its identity
	/// from whichever peer first sent a packet about it - one-sided by construction,
	/// because the peer holding the same animal never asked for one, so everything sent
	/// about it is dropped.
	///
	/// Widening the first hook's gate was tried and measured first: critterNoAnim stayed
	/// at 0 and CrabBaby kept arriving lazily, which says the object is not tagged
	/// Creature that early rather than that it was failing the anim condition.
	/// </summary>
	[HarmonyPatch(typeof(Navigator), nameof(Navigator.OnSpawn))]
	public static class CreatureNavigatorSpawnPatch
	{
		public static void Postfix(Navigator __instance)
		{
			if (__instance.IsNullOrDestroyed()) return;
			CreatureSpawnPatch.Attach(__instance.gameObject);
		}
	}
}
