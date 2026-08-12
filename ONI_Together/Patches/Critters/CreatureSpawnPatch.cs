using HarmonyLib;
using ONI_Together.DebugTools;
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
		public static void Postfix(KPrefabID __instance)
		{
			using var _ = Profiler.Scope();
			try
			{
				if (__instance == null) return;

				var go = __instance.gameObject;

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

				if (!AnimSyncEligibility.IsAnimatedCritter(go))
					return;

				// Adding a component during OnSpawn means Unity will not call the
				// new component's own OnSpawn, so registration is driven by hand -
				// the same reason BuildingSpawnPatch calls RegisterIdentity here.
				go.AddOrGet<EntityPositionHandler>();
				go.AddOrGet<AnimStateSyncer>();
				go.AddOrGet<CreatureMultiplayerInitializer>();
				go.AddOrGet<NetworkIdentity>().RegisterIdentity();
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
}
