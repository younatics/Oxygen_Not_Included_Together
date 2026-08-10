using ONI_Together.Networking.Components;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Animation;
using ONI_Together.Networking.Packets.World;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Shared.Profiling;
using static RancherChore;
using static WorkerBase;
using static ClusterTelescope;

namespace ONI_Together.Patches.DuplicantActions
{
	internal class StandardWorker_Patches
	{

		[HarmonyPatch(typeof(StandardWorker), nameof(StandardWorker.StartWork))]
		public class StandardWorker_StartWork_Patch
		{
			// SKIP WORKABLE
			private static Type[] workablesToSkip =
			{
                typeof(DefragmentationZone),
                typeof(RancherWorkable),
                typeof(LiquidPumpingStation),
				typeof(IceKettleWorkable),
				typeof(Sleepable),
				typeof(Bottler),
				typeof(ClusterTelescopeIdentifyMeteorWorkable)
            };

			private static readonly HashSet<ulong> _viewportScratch = new HashSet<ulong>();

			public static void Postfix(StandardWorker __instance, StartWorkInfo start_work_info)
			{
				using var _ = Profiler.Scope();

				if (__instance.IsNullOrDestroyed())
					return;

				if (start_work_info.IsNullOrDestroyed())
					return;

				if (!Utils.IsHostMinion(__instance))
					return;

				foreach (Type workableType in workablesToSkip)
				{
                    if (start_work_info.workable.GetType() == workableType)
                        return;
                }

                // Only to peers that hold the workable. Spawns are culled to a
                // client's viewport and this was not, so a duplicant starting
                // work on something the client was never told about produced a
                // packet that could only fail - 1176 unresolvable workables on
                // one client, against none on the host. WorkableProgressPacket
                // is culled the same way.
                var packet = new StandardWorker_WorkingState_Packet(__instance, start_work_info.workable, true);
                int workCell = Grid.PosToCell(start_work_info.workable);
                if (Grid.IsValidCell(workCell) && WorldStateSyncer.Instance != null)
                {
                    _viewportScratch.Clear();
                    WorldStateSyncer.Instance.GetClientsViewingCell(workCell, _viewportScratch, 2);
                    if (_viewportScratch.Count == 0)
                        return;

                    foreach (var playerId in _viewportScratch)
                        PacketSender.SendToPlayer(playerId, packet);
                    return;
                }

                PacketSender.SendToAllClients(packet);
			}
		}

		[HarmonyPatch(typeof(StandardWorker), nameof(StandardWorker.StopWork))]
		public class StandardWorker_StopWork_Patch
		{
			public static void Prefix(StandardWorker __instance)
			{
				using var _ = Profiler.Scope();

				if (__instance.IsNullOrDestroyed())
					return;

				if (!Utils.IsHostMinion(__instance))
					return;

				var workable = __instance.GetWorkable();
				if (workable == null || workable.IsNullOrDestroyed())
					return;

				PacketSender.SendToAllClients(WorkableProgressPacket.CreateHidden(workable), PacketSendMode.ReliableImmediate);

				if (workable.TryGetComponent<ComplexFabricator>(out var fabricator) && fabricator != null && !fabricator.IsNullOrDestroyed())
				{
					PacketSender.SendToAllClients(WorkableProgressPacket.CreateComplexFabricator(fabricator, showProgressBar: false), PacketSendMode.ReliableImmediate);
				}
			}

			public static void Postfix(StandardWorker __instance)
			{
				using var _ = Profiler.Scope();

				if (__instance.IsNullOrDestroyed())
					return;

				if (!Utils.IsHostMinion(__instance))
					return;

				PacketSender.SendToAllClients(new StandardWorker_WorkingState_Packet(__instance,null, false));
			}
		}
	}
}
