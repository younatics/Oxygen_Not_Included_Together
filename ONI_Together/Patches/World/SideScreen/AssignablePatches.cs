using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;

namespace ONI_Together.Patches.World.SideScreen
{
    /// <summary>
    /// Patches for Assignable synchronization (Outhouse, Lavatory, Triage Cot, etc.)
    /// </summary>
    [HarmonyPatch(typeof(Assignable), nameof(Assignable.OnSpawn))]
    public static class Assignable_OnSpawn_Patch
    {
        public static void Postfix(Assignable __instance)
        {
	        using var _ = Profiler.Scope();

            var buildingIdentity = __instance.gameObject.AddOrGet<NetworkIdentity>();
            buildingIdentity.RegisterIdentity();
        }
    }

    [HarmonyPatch(typeof(Assignable), nameof(Assignable.Assign), typeof(IAssignableIdentity))]
	public static class Assignable_Assign_Patch
	{
		public static void Postfix(Assignable __instance, IAssignableIdentity new_assignee)
		{
			using var _ = Profiler.Scope();

			if (AssignmentPacket.IsApplying) return;
			if (!MultiplayerSession.InSession) return;
			if (__instance == null || __instance.gameObject == null) return;
            if (__instance.IsNullOrDestroyed()) return;

            var buildingIdentity = __instance.gameObject.GetComponent<NetworkIdentity>();
			if (!buildingIdentity)
                return;

            int assigneeNetId = -1;
			string groupId = "";

			if (new_assignee == null)
			{
				assigneeNetId = -1;
            }
            else if (new_assignee is AssignmentGroup group)
			{
				groupId = group.id;
            }
            else if (new_assignee is MinionAssignablesProxy proxy)
			{
                var targetGO = proxy.GetTargetGameObject();
				if (targetGO != null)
				{
                    var minionNetId = targetGO.GetComponent<NetworkIdentity>();
					if (minionNetId != null)
					{
                        assigneeNetId = minionNetId.NetId;
					}
				}
			}
			else if (new_assignee is KMonoBehaviour mb)
			{
                var minionNetId = mb.gameObject.GetComponent<NetworkIdentity>();
				if (minionNetId != null)
				{
                    minionNetId.RegisterIdentity();
					assigneeNetId = minionNetId.NetId;
                }
            }

            var packet = new AssignmentPacket
			{
				BuildingNetId = buildingIdentity.NetId,
				Cell = Grid.PosToCell(__instance.gameObject),
				AssigneeNetId = assigneeNetId,
				GroupId = groupId,
				// What is being assigned, so the receiver's cell fallback can tell
				// whether the object standing at that cell is this one. An assignable is
				// not always a building - a duplicant is assigned to an atmo suit - and
				// the fallback renamed a locker with a suit's id for want of this.
				PrefabHash = __instance.gameObject.TryGetComponent<KPrefabID>(out var kpid)
					? kpid.PrefabTag.GetHashCode()
					: 0
			};

            if (MultiplayerSession.IsHost) PacketSender.SendToAllClients(packet);
			else PacketSender.SendToHost(packet);
		}
	}

    [HarmonyPatch(typeof(Assignable), nameof(Assignable.Unassign))]
	public static class Assignable_Unassign_Patch
	{
		public static void Postfix(Assignable __instance)
		{
			using var _ = Profiler.Scope();

			if (AssignmentPacket.IsApplying) return;
			if (!MultiplayerSession.InSession) return;
			if (__instance.IsNullOrDestroyed()) return;

			var buildingIdentity = __instance.gameObject.GetComponent<NetworkIdentity>();
			if (!buildingIdentity)
				return;

			var packet = new AssignmentPacket
			{
				BuildingNetId = buildingIdentity.NetId,
				Cell = Grid.PosToCell(__instance.gameObject),
				AssigneeNetId = -1,
				GroupId = "",
				PrefabHash = __instance.gameObject.TryGetComponent<KPrefabID>(out var unassignKpid)
					? unassignKpid.PrefabTag.GetHashCode()
					: 0
			};

			if (MultiplayerSession.IsHost) PacketSender.SendToAllClients(packet);
			else PacketSender.SendToHost(packet);

			DebugConsole.Log($"[Assignable_Unassign_Patch] Unassigned {__instance.name}");
		}
	}
}
