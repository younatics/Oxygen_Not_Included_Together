using System;
using System.Collections.Generic;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World
{
	// Adds NetworkIdentity to buildings that need it for BuildingConfigPacket or other interactions
	// Adds NetworkIdentity to buildings that need it
	[HarmonyPatch(typeof(Building), "OnSpawn")]
	public static class BuildingSpawnPatch
	{
        private static readonly List<Type> IdentityRequiredComponents = new()
        {
            typeof(LogicSwitch),
            typeof(Valve),
            typeof(IThresholdSwitch),
            typeof(IActivationRangeTarget),
            typeof(ISliderControl),
            typeof(ISingleSliderControl),
            typeof(ICheckboxControl),
            typeof(IUserControlledCapacity),
            typeof(ISidescreenButtonControl),
            typeof(Door),
            typeof(LimitValve),
            typeof(Compost),
            typeof(StorageLocker),
            typeof(Refrigerator),
            typeof(RationBox)
        };

        public static void Postfix(Building __instance)
		{
			using var _ = Profiler.Scope();
			try
			{
                HandlePostfix(__instance);
            }
			catch (System.Exception ex)
			{
				DebugConsole.LogError($"[BuildingSpawnPatch] {ex}");
			}
		}

        private static void HandlePostfix(Building building)
        {
            var go = building.gameObject;

            // A building site needs an address as much as a finished building.
            //
            // Work progress on a Constructable is reported by NetId, and the
            // host gets one for free: sending the packet calls GetNetId(), which
            // attaches a NetworkIdentity on demand. The client never sends
            // progress, so it never asked, so its own copy of the same building
            // site had no identity and no registry entry - and every progress
            // packet for it was dropped. One live client failed to resolve 221
            // distinct Constructables, 2523 times, and neither peer had ever
            // registered a single one.
            //
            // Both peers create the site at the same cell from the same prefab,
            // so GetDeterministicBuildingId gives them the same answer without
            // any exchange.
            if (building is BuildingUnderConstruction)
            {
                go.AddOrGet<NetworkIdentity>().RegisterIdentity();
                return;
            }

            if (building is not BuildingComplete)
                return;

            if (!RequiresNetworkIdentity(go))
                return;

            go.AddOrGet<NetworkIdentity>().RegisterIdentity();
        }

        private static bool RequiresNetworkIdentity(GameObject go)
        {
            if (AnimSyncEligibility.IsAnimatedBuilding(go))
                return true;

            foreach (var type in IdentityRequiredComponents)
            {
                if (go.GetComponent(type) != null)
                    return true;
            }

            return false;
        }
    }
}
