using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World.Plants
{
	[HarmonyPatch]
	internal static class PlantLifecyclePatches
	{
		/// <summary>
		/// Which path a plant that appears mid-session takes, and where it stops.
		///
		/// A plant a duplicant sowed exists on the host and never on the client, and both
		/// attempts to fix that were reverted. Neither could be judged, because the
		/// scenario never sows anything: the run compares 458 plants that were in the save
		/// before the session began, reports plant DIFFERENT 0, and says nothing at all
		/// about the case that is broken. A check that cannot fail is not a check.
		///
		/// Two host paths exist and only one can fire for a given plant - a plot spawns its
		/// occupant, and anything sown in open ground arrives through Growing.OnSpawn with
		/// IsWildPlanted set. If a sown plant is neither, both return early and nothing is
		/// sent, which is the shape the symptom has. These count every arrival and every
		/// reason for declining it, so the next session that sows a plant answers it
		/// outright instead of another round of reasoning about which branch it took.
		/// </summary>
		public static int PlotSpawns { get; private set; }
		public static int WildSpawns { get; private set; }
		public static int GrowingSeen { get; private set; }
		public static int DeclinedNotWild { get; private set; }
		public static int DeclinedNotBroadcasting { get; private set; }

		[HarmonyPatch(typeof(PlantablePlot), nameof(PlantablePlot.SpawnOccupyingObject))]
		private static class PlantablePlot_SpawnOccupyingObject_Patch
		{
			private static void Postfix(PlantablePlot __instance, GameObject __result)
			{
				using var _ = Profiler.Scope();

				if (!MultiplayerSession.IsHostInSession)
					return;
				if (!PlantGrowthSyncer.CanBroadcastLifecycleEvents)
					return;
				if (__result == null || PlantGrowthSyncer.IsApplyingState)
					return;
				if (!__result.TryGetComponent<Growing>(out var growing) || growing == null)
					return;

				PlotSpawns++;
				PlantGrowthSyncer.BroadcastPlantLifecycle(PlantLifecycleOperation.Spawn, growing, __instance);
			}
		}

		[HarmonyPatch(typeof(Growing), nameof(Growing.OnSpawn))]
		private static class Growing_OnSpawn_Patch
		{
			private static void Postfix(Growing __instance)
			{
				using var _ = Profiler.Scope();

				if (!MultiplayerSession.IsHostInSession)
					return;

				// Counted before the broadcast gate rather than after it. Every other
				// counter in this mod sits after a decision, which is what made the
				// arriving-spawn defect invisible for three runs: the number that would
				// have shown it was only reached once the branch had already declined.
				GrowingSeen++;

				if (!PlantGrowthSyncer.CanBroadcastLifecycleEvents)
				{
					DeclinedNotBroadcasting++;
					return;
				}
				if (PlantGrowthSyncer.IsApplyingState || __instance == null)
					return;
				if (!__instance.IsWildPlanted())
				{
					DeclinedNotWild++;
					return;
				}

				WildSpawns++;
				PlantGrowthSyncer.BroadcastPlantLifecycle(PlantLifecycleOperation.Spawn, __instance);
			}
		}

		[HarmonyPatch(typeof(KPrefabID), nameof(KPrefabID.OnCleanUp))]
		private static class KPrefabID_OnCleanUp_Patch
		{
			private static void Prefix(KPrefabID __instance)
			{
				using var _ = Profiler.Scope();

				if (!MultiplayerSession.IsHostInSession)
					return;
				if (!PlantGrowthSyncer.CanBroadcastLifecycleEvents)
					return;
				if (PlantGrowthSyncer.IsApplyingState || __instance == null)
					return;

				var growing = __instance.GetComponent<Growing>();
				if (growing == null)
					return;

				PlantGrowthSyncer.BroadcastPlantLifecycle(PlantLifecycleOperation.Remove, growing);
			}
		}
	}
}
