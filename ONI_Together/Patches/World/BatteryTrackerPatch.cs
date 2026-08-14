using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.States;
using Shared.Profiling;

namespace ONI_Together.Patches.World
{
	[HarmonyPatch(typeof(BatteryTracker), "UpdateData")]
	public static class BatteryTrackerPatch
	{
		private sealed class ClientRefreshScope : System.IDisposable
		{
			public void Dispose()
			{
				_allowedClientRefreshDepth = System.Math.Max(0, _allowedClientRefreshDepth - 1);
			}
		}

		private static int _allowedClientRefreshDepth;

		internal static System.IDisposable AllowClientRefresh()
		{
			_allowedClientRefreshDepth++;
			return new ClientRefreshScope();
		}

		public static bool Prefix(BatteryTracker __instance)
		{
			using var _ = Profiler.Scope();

			// Original client-block existed to avoid hard-sync crashes. IsHardSyncInProgress
			// now covers that case directly, so let BatteryTracker.UpdateData run on clients
			// otherwise — blocking it leaves batteries unregistered in the local CircuitManager,
			// making every powered building render as "no power" until the next joules delta.
			if (GameClient.IsHardSyncInProgress)
				return false;

			// A world being loaded is the other transition, and it was not covered.
			//
			// Measured on a client three milliseconds after "Loaded <save>": a
			// NullReferenceException inside UpdateData, from TrackerTool.Update. The
			// tracker runs while the world is half built and reads something that is not
			// there yet.
			//
			// It matters for the same reason the block above was narrowed. An exception
			// out of UpdateData leaves the batteries unregistered in the local
			// CircuitManager, which is exactly the "every powered building shows no
			// power" state this patch exists to avoid - so throwing here costs what
			// blocking here used to.
			//
			// Skipped rather than caught: one skipped tracker update is invisible, and the
			// next one runs a fraction of a second later with a finished world.
			//
			// Testing for LoadingWorld specifically was not enough, and the log says why
			// in three consecutive lines: "State changed to: LoadingWorld", then
			// "Disconnected from server", then "State changed to: Disconnected" - all in
			// the same millisecond, because the save transfer finishes and the transport
			// drops while the world loads. The throw came five milliseconds after
			// "Loaded <save>", by which time the state this guard was watching for had
			// already been replaced by another one.
			//
			// So the condition is stated positively: run when this peer is actually
			// playing. Every other state is a transition, and during a transition a
			// skipped tracker update costs nothing while an exception costs the thing
			// this patch exists to protect - batteries left unregistered in the local
			// CircuitManager, and every powered building rendering as no power.
			//
			// A host is never in a ClientState at all, so it is admitted by the first
			// test and this cannot change its behaviour.
			if (Game.Instance == null || Grid.WidthInCells == 0)
				return false;

			if (MultiplayerSession.IsClient && GameClient.State != ClientState.InGame)
				return false;

			return true;
		}
	}
}
