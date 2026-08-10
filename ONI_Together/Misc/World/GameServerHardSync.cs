using ONI_Together.Networking;
using ONI_Together.DebugTools;
using ONI_Together.Menus;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.World;
using System.Collections;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking
{
	public static class GameServerHardSync
	{
		public static bool hardSyncDoneThisCycle = false;
		private static bool hardSyncInProgress = false;
		private static int numberOfClientsAtTimeOfSync = 0;

		public static bool IsHardSyncInProgress
		{

			get
			{
				return hardSyncInProgress;
			}
			set
			{
				hardSyncInProgress = value;
			}
		}

		public static void PerformHardSync(bool consumeDailyUse = false)
		{
			using var _ = Profiler.Scope();

			if (hardSyncInProgress)
			{
				DebugConsole.Log("[HardSync] A hard sync is already in progress.");
				return;
			}

			SpeedControlScreen.Instance?.Pause(false); // Pause the game
			MultiplayerOverlay.Show(STRINGS.UI.MP_OVERLAY.SYNC.HARDSYNC_INPROGRESS);

            numberOfClientsAtTimeOfSync = MultiplayerSession.ConnectedPlayers.Count;
			var packet = new HardSyncPacket();
			PacketSender.SendToAllClients(packet);

			// Hide other player cursors as they are in hard sync and it'll reappear when they start sending packets again
			foreach (PlayerCursor cursor in MultiplayerSession.PlayerCursors.Values)
			{
				cursor.SetVisibility(false);
			}

			DebugConsole.Log($"[HardSync] Starting hard sync for {numberOfClientsAtTimeOfSync} client(s)...");
			CoroutineRunner.RunOne(HardSyncCoroutine(consumeDailyUse));
		}

		private static IEnumerator HardSyncCoroutine(bool consumeDailyUse = false)
		{
			using var _ = Profiler.Scope();

			hardSyncInProgress = true;

            ReadyManager.MarkAllAsUnready();
            SaveFileRequestPacket.SendSaveFileToAll();
            ReadyManager.RefreshScreen(); // Bring up ready screen for host

            // Wait for the clients to say they are done, not for a guess at how
			// long they should take.
			//
			// This used to sleep for an estimate - chunk count times a per-chunk
			// delay times the number of clients - and then declare the sync over
			// whatever had actually happened. A client that took longer than the
			// estimate had the host resume and start sending world state at a
			// peer that was still loading, which is precisely the state where
			// spawn handlers run against an empty Grid. The signal was already
			// there and unused: the sync marks everyone unready, and a client
			// reports itself ready once it is back in the world.
			//
			// The estimate survives as a ceiling, so one client that never
			// reports cannot hold the host paused forever.
            int fileSize = SaveHelper.GetWorldSave().Length;
			int chunkSize = SaveHelper.SAVEFILE_CHUNKSIZE_KB * 1024;
			int chunkCount = Mathf.CeilToInt(fileSize / (float)chunkSize);
			float estimatedTransferDuration = chunkCount * SaveFileRequestPacket.SAVE_DATA_SEND_DELAY;
			float ceiling = Mathf.Max(30f, estimatedTransferDuration * numberOfClientsAtTimeOfSync * 4f);

			float waited = 0f;
			while (waited < ceiling && !ReadyManager.IsEveryoneReady())
			{
				yield return new WaitForSecondsRealtime(0.25f);
				waited += 0.25f;
			}

			if (ReadyManager.IsEveryoneReady())
			{
				DebugConsole.Log($"[HardSync] all {numberOfClientsAtTimeOfSync} client(s) back after {waited:0.0}s");
			}
			else
			{
				DebugConsole.LogWarning(
					$"[HardSync] giving up waiting after {waited:0.0}s - a client never reported ready. " +
					"Resuming anyway; that peer will be sent state it may not be able to apply yet.");
			}

			hardSyncDoneThisCycle = consumeDailyUse;
            hardSyncInProgress = false;
			// With the ready state I do not think this is needed anymore
			//SpeedControlScreen.Instance?.Unpause(false);
			//MultiplayerOverlay.Close();
		}
	}
}
