using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools.Build;
using System.Linq;
using Shared.Profiling;
using ONI_Together.Misc;

[HarmonyPatch(typeof(Constructable), nameof(Constructable.FinishConstruction))]
public static class ConstructablePatch
{
	/// <summary>
	/// Completed builds not announced because the cell or the building name was unusable.
	/// Non-zero means a peer is missing a finished building it will never be told about.
	/// </summary>
	public static int BuildSendsRefused { get; private set; }

	public static void Prefix(Constructable __instance, WorkerBase workerForGameplayEvent)
	{
		using var _ = Profiler.Scope();

		if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession)
			return;

		var building = __instance.GetComponent<Building>();
		if (building == null || building.Def == null)
			return;

		int cell = Grid.PosToCell(__instance.transform.position);
		var def = building.Def;

		// A cell and a name the receiver can act on, or nothing.
		//
		// Grid.PosToCell returns -1 for a position off the grid and this shipped it: the
		// client logs "[BuildCompletePacket] Invalid cell: -1" once a run, which is a
		// completed building that peer will never be told about. An empty PrefabID is the
		// same shape - "[BuildPacket] Unknown building def: " with nothing after the
		// colon - and both are the defect this project already paid for once, where six
		// packets read a value behind a null check and sent the unset default anyway.
		//
		// There is no valid default for a cell or a building name, so the send is
		// dropped rather than corrected, and counted so the drop is not silent. If the
		// client keeps reporting empty defs after this, the value is being lost on the
		// wire rather than at the sender, which is worth knowing and is not currently
		// distinguishable.
		if (!Grid.IsValidCell(cell) || string.IsNullOrEmpty(def.PrefabID))
		{
			BuildSendsRefused++;
			ThrottledLog.Warn(
				$"[ConstructablePatch] not announcing a completed build: cell={cell}, " +
				$"prefab='{def.PrefabID}' - the receiver could not have applied it");
			return;
		}

		var materialTags = __instance.SelectedElementsTags?.Select(tag => tag.ToString()).ToList() ?? new System.Collections.Generic.List<string>();

		float temp = __instance.GetComponent<PrimaryElement>()?.Temperature ?? def.Temperature;

		var rotatable = __instance.GetComponent<Rotatable>();
		var orientation = rotatable != null ? rotatable.GetOrientation() : Orientation.Neutral;

		var facade = __instance.GetComponent<BuildingFacade>()?.CurrentFacade ?? "DEFAULT_FACADE";

        // Handle utility connections
        UtilityConnections utilityConnectionFlags = (UtilityConnections)0;
        // Capture connection directions for wires/pipes
        var tileVis = __instance.GetComponent<KAnimGraphTileVisualizer>();
		if (tileVis != null)
		{
			utilityConnectionFlags = tileVis.Connections;
		}

		/*
        IHaveUtilityNetworkMgr mgr = def.BuildingComplete.GetComponent<IHaveUtilityNetworkMgr>();
        if (mgr != null)
		{
			var networkManager = mgr.GetNetworkManager();
			if(networkManager != null)
			{
                utilityConnectionFlags = networkManager.GetConnections(cell, false);
            }
		}*/

		int workerId = workerForGameplayEvent.GetNetId();
		var packet = new BuildCompletePacket
		{
			Cell = cell,
			PrefabID = def.PrefabID,
			Orientation = orientation,
			MaterialTags = materialTags,
			Temperature = temp,
			FacadeID = facade,
			UtilityConnectionFlags = utilityConnectionFlags,
			ObjectLayer = def.ObjectLayer,
			WorkerNetId = workerId
		};

		PacketSender.SendToAllClients(packet);
		DebugConsole.Log($"[Host] Sent BuildCompletePacket for {def.PrefabID} at cell {cell}");
	}
}

