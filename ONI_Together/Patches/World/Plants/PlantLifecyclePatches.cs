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

		/// <summary>
		/// The plot branch, gate by gate. PlotSeen sits above all of them, so a zero in
		/// PlotSpawns can be read as "never reached" or "reached and declined here" rather
		/// than being one number that means both.
		/// </summary>
		public static int PlotSeen { get; private set; }
		public static int PlotDeclinedNotHost { get; private set; }
		public static int PlotDeclinedNotBroadcasting { get; private set; }
		public static int PlotDeclinedNoResult { get; private set; }
		public static int PlotDeclinedNotPlanted { get; private set; }
		public static int WildSpawns { get; private set; }
		public static int GrowingSeen { get; private set; }
		public static int DeclinedNotWild { get; private set; }
		public static int DeclinedNotBroadcasting { get; private set; }

		[HarmonyPatch(typeof(PlantablePlot), nameof(PlantablePlot.SpawnOccupyingObject))]
		private static class PlantablePlot_SpawnOccupyingObject_Patch
		{
			private static void Postfix(PlantablePlot __instance, GameObject depositedEntity, GameObject __result)
			{
				using var _ = Profiler.Scope();

				// Counted before every gate, for the reason the file already gives about
				// the other branch: a run widened this check from Growing to GameTags.Plant
				// and PlotSpawns still read 0, which cannot tell "the postfix never ran"
				// from "it ran and one of four conditions turned it down". Four conditions
				// and no way to name which is the same position the plant work has been
				// stuck in twice.
				PlotSeen++;

				if (!MultiplayerSession.IsHostInSession)
				{
					PlotDeclinedNotHost++;
					return;
				}
				if (!PlantGrowthSyncer.CanBroadcastLifecycleEvents)
				{
					PlotDeclinedNotBroadcasting++;
					return;
				}
				if (__result == null || PlantGrowthSyncer.IsApplyingState)
				{
					PlotDeclinedNoResult++;
					return;
				}

				// The plot's own distinction, not a marker component and not a tag.
				//
				// Two guesses failed here and the counters named both. Growing excluded the
				// Wheezewort, which is the one plant that diverges. GameTags.Plant looked
				// like the game's own answer - ExtendEntityToBasicPlant adds it to every
				// plant it builds - but ColdBreatherConfig never calls that helper: it goes
				// through CreatePlacedEntity and assembles the rest by hand, so it carries
				// no such tag. plotSeen=1 with plotNoTag=1 is what said so, in one run,
				// instead of another round of reasoning about which condition fired.
				//
				// SpawnOccupyingObject answers the question itself. Read from the body: if
				// the deposited entity is a PlantableSeed it instantiates the plant and
				// returns that; otherwise it sets destroyEntityOnDeposit false and returns
				// the deposited object unchanged. So a new object means something was
				// planted, and the same object back means it was not. That holds for every
				// species without naming any of them.
				if (ReferenceEquals(__result, depositedEntity))
				{
					PlotDeclinedNotPlanted++;
					return;
				}

				PlotSpawns++;
				PlantGrowthSyncer.BroadcastPlantLifecycle(PlantLifecycleOperation.Spawn, __result, __instance);
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
