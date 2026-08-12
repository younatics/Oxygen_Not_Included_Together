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
			if (Game.Instance == null || GameClient.State == ClientState.LoadingWorld)
				return false;

			return true;
		}
	}
}
