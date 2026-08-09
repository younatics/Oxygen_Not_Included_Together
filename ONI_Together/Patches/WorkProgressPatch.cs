using ONI_Together.Networking.Components;
using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;
using System.Collections.Generic;
using Shared.Profiling;
using UnityEngine;

[HarmonyPatch(typeof(Workable), nameof(Workable.WorkTick))]
public static class WorkProgressPatch
{
	private static Dictionary<int, float> nextSendTime = new Dictionary<int, float>();

	private static readonly HashSet<ulong> _viewportScratch = new HashSet<ulong>();
	private const float SEND_INTERVAL = 0.5f;

	public static void Postfix(Workable __instance)
	{
		using var _ = Profiler.Scope();

		if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession)
			return;

		if (__instance.IsNullOrDestroyed())
			return;

		if (ShouldSkip(__instance))
			return;

		float workTime = __instance.GetWorkTime();
		//					this was preventing digging progress being sent
		if (workTime <= 0f /*|| float.IsInfinity(workTime)*/ || float.IsNaN(workTime))
			return;

		float percentComplete = __instance.GetPercentComplete();
		if (float.IsNaN(percentComplete) || float.IsInfinity(percentComplete))
			return;

		int workableNetId = __instance.GetNetId();
		if (workableNetId == 0)
			return;

		string workableType = __instance.GetType().AssemblyQualifiedName;
		if (string.IsNullOrEmpty(workableType))
			return;

		int trackingKey = GetTrackingKey(workableNetId, workableType);
		float now = Time.time;

		if (nextSendTime.TryGetValue(trackingKey, out float next) && now < next)
			return;

		nextSendTime[trackingKey] = now + SEND_INTERVAL;

		// Only to peers that can see the thing being worked on.
		//
		// Spawns are culled to a client's viewport and this was not, so progress
		// kept arriving for objects the client had never been told about and
		// never would be. That is the whole of the remaining resolve failures on
		// a live client - 181 for Pickupables, 76 for Storage - and none of them
		// could ever have succeeded. Sending them costs bandwidth to produce a
		// warning.
		int workableCell = Grid.PosToCell(__instance);
		if (Grid.IsValidCell(workableCell) && WorldStateSyncer.Instance != null)
		{
			_viewportScratch.Clear();
			WorldStateSyncer.Instance.GetClientsViewingCell(workableCell, _viewportScratch, 2);
			if (_viewportScratch.Count == 0)
				return;

			var packet = new WorkableProgressPacket(__instance);
			foreach (var playerId in _viewportScratch)
				PacketSender.SendToPlayer(playerId, packet, PacketSendMode.Unreliable);
			return;
		}

		PacketSender.SendToAllClients(new WorkableProgressPacket(__instance), PacketSendMode.Unreliable);
	}

	public static void ClearTracking()
	{
		using var _ = Profiler.Scope();

		nextSendTime.Clear();
	}

	private static bool ShouldSkip(Workable workable)
	{
		return workable is DefragmentationZone
			|| workable.GetType().Name == "RancherWorkable"
			|| workable is LiquidPumpingStation;
	}

	private static int GetTrackingKey(int workableNetId, string workableType)
	{
		return unchecked((workableNetId * 397) ^ workableType.GetHashCode());
	}
}
