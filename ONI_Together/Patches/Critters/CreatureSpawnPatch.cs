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
	}
}
