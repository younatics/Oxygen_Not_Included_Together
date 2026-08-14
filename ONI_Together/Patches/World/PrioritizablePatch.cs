using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;

namespace ONI_Together.Patches.World
{
	[HarmonyPatch(typeof(Prioritizable), "SetMasterPriority")]
	public static class PrioritizablePatch
	{
		public static void Postfix(Prioritizable __instance, PrioritySetting priority)
		{
			using var _ = Profiler.Scope();

			if (PrioritizeStatePacket.IsApplying) return;
			if (!MultiplayerSession.InSession) return;

			// Find NetId
			//
			// This tested the wrong thing. The sentinel for "no identity component"
			// was -1, and the check refused only that - so an object that HAS an
			// identity but has not been given an id yet passed straight through with
			// netId 0 and the packet went out addressed to nothing. A single run
			// measured 179 packets arriving with NetId 0 and 176 of them came from
			// here; the receiver cannot resolve any of them, and each one is a
			// warning on the far side.
			//
			// Zero is not an address. Refuse it, and say which prefab could not be
			// named - the packet is a symptom, the unnamed object is the cause, and
			// dropping it quietly would hide the second one.
			var identity = __instance.gameObject.GetExistingNetIdentity();
			int netId = identity.IsNullOrDestroyed() ? 0 : identity.NetId;

			// An unaddressed object may still be findable by its cell.
			//
			// A dig marker never gets a NetId and never will - it is a mark on a cell,
			// with nothing else to identify it - so every priority a player set on one
			// was dropped here, 7 to 9 a run. The cell is its whole identity, so sending
			// that is not a workaround; it is the right key for this kind of thing.
			//
			// Only for markers on the dig layer. A cell can hold several buildings and a
			// cell does not identify a duplicant, so everything else still travels by
			// address and is counted as before when it has none.
			int cell = Grid.PosToCell(__instance.gameObject);
			bool addressableByCell = netId == 0
				&& Grid.IsValidCell(cell)
				&& Grid.Objects[cell, (int)ObjectLayer.DigPlacer] == __instance.gameObject;

			if (netId == 0 && !addressableByCell)
			{
				_unaddressable++;
				DebugTools.ThrottledLog.Warn(
					$"[Prioritizable] not sending a priority change for " +
					$"'{__instance.gameObject.PrefabID()}': it has no NetId and no cell that " +
					"identifies it, so nothing could apply it");
			}
			else
			{
				var packet = new PrioritizeStatePacket();
				packet.Priorities.Add(new PrioritizeStatePacket.PriorityData
				{
					NetId = netId,
					PriorityClass = (int)priority.priority_class,
					PriorityValue = priority.priority_value,
					Cell = addressableByCell ? cell : Grid.InvalidCell,
				});
				if (addressableByCell) SentByCell++;

				if (MultiplayerSession.IsHost)
					PacketSender.SendToAllClients(packet);
				else
					PacketSender.SendToHost(packet);
			}
		}

		/// <summary>
		/// Priority changes dropped because the object had no id. Exposed so a
		/// silent drop cannot read as "everything replicated".
		/// </summary>
		public static int Unaddressable => _unaddressable;

		/// <summary>
		/// Priority changes sent for a marker identified by its cell instead of an
		/// address. Non-zero beside a zero drop count is this path working.
		/// </summary>
		public static int SentByCell { get; private set; }

		private static int _unaddressable;
	}
}
