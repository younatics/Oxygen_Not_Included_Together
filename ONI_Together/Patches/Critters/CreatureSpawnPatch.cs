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
					go.AddOrGet<NetworkIdentity>().RegisterIdentity();
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
		private static bool IsEgg(UnityEngine.GameObject go)
		{
			if (go == null) return false;
			if (go.GetComponent<IncubationMonitor.Instance>() != null) return true;

			if (!go.TryGetComponent<KPrefabID>(out var kpid) || kpid == null) return false;

			string name = kpid.PrefabTag.Name;
			return !string.IsNullOrEmpty(name)
				&& name.EndsWith("Egg", System.StringComparison.Ordinal);
		}
	}
}
