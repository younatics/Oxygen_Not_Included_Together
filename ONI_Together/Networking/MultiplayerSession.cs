using ONI_Together.DebugTools;
using ONI_Together.Misc;
using System.Collections.Generic;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking
{
	public static class MultiplayerSession
	{

		public static bool ShouldHostAfterLoad = false;

        /// <summary>
        /// HOST ONLY - Returns a list of connected players
		/// <para>For clients use NetworkConfig.GetConnectedClients() instead</para>
        /// </summary>
        public static readonly Dictionary<ulong, MultiplayerPlayer> ConnectedPlayers = new Dictionary<ulong, MultiplayerPlayer>();

		public static ulong LocalUserID => NetworkConfig.GetLocalID();

		[System.Obsolete] //Keep for api compatibility
		public static ulong LocalSteamID => LocalUserID;
		[System.Obsolete] //Keep for api compatibility
		public static ulong HostSteamID => HostUserID;

		public static ulong HostUserID { get; set; } = Utils.NilUlong();

		public static string ServerIp { get; set; } = "127.0.0.1";
		public static int ServerPort { get; set; } = 7777;

		public static bool InSession = false;
		public static bool SessionHasPlayers => InSession && ConnectedPlayers.Count > 1;
		public static bool NotInSession => !InSession;

		public static bool IsHost { get; set; } //HostUserID == LocalUserID;

		public static bool IsClient => InSession && !IsHost;

		public static bool IsHostInSession => IsHost && InSession;

		/// <summary>
		/// A client, including the gap in the middle of a reconnect.
		///
		/// IsClient is false while a client is between sessions, and several guards read
		/// that as "playing alone, so do the work locally". A reconnect is not playing
		/// alone. During that window a client hatched its own eggs and ended a run
		/// holding 81 critters against the host's 77 - the host's babies arrived by
		/// announcement and the client had already made its own, nine milliseconds apart
		/// in the log.
		///
		/// The same gap reaches further than eggs. The guards that mark a drawn object as
		/// a preview, that stop a client walking to a free id, and that stop a client
		/// fabricator making its own products are all keyed the same way, and all three
		/// leave something permanent behind: an object the host never named, or an id it
		/// never issued.
		///
		/// A cached connection is what tells the two cases apart, and it is already what
		/// the reconnect path uses to decide whether to rejoin once the world has
		/// loaded. A peer holding one is coming back; a peer without one is on its own.
		/// </summary>
		public static bool IsClientOrReconnecting =>
			IsClient || (!IsHost && GameClient.HasCachedConnection());

		public static readonly Dictionary<ulong, PlayerCursor> PlayerCursors = new Dictionary<ulong, PlayerCursor>();

		public static readonly Dictionary<ulong, string> KnownPlayerNames = new Dictionary<ulong, string>();

		public static void Clear()
		{
			using var _ = Profiler.Scope();

			ConnectedPlayers.Clear();
			KnownPlayerNames.Clear();
			HostUserID = Utils.NilUlong();
			WorkProgressPatch.ClearTracking();
			RemoteProgressRegistry.ClearAll();
			// So a count never spans two sessions and reads as one long retry
			// loop when it was two short ones.
			ThrottledLog.Reset();
			// Everything else that must not outlive a session. A session boundary
			// is not a process boundary here - players rejoin and the host hard
			// syncs several times an evening - and state that survives it
			// produces bugs that only appear on the second or third session.
			SessionTeardown.ClearAll();
			DebugConsole.Log("[MultiplayerSession] Session cleared.");
		}

		public static void SetHost(ulong host)
		{
			using var _ = Profiler.Scope();

			HostUserID = host;
			DebugConsole.Log($"[MultiplayerSession] Host set to: {host}");
		}

        /// <summary>
        /// HOST ONLY - Get the multiplayer instance of the player with the given ID. Returns null if not found
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public static MultiplayerPlayer GetPlayer(ulong id)
		{
			using var _ = Profiler.Scope();

			return ConnectedPlayers.TryGetValue(id, out var player) ? player : null;
		}

		public static MultiplayerPlayer LocalPlayer => GetPlayer(LocalUserID);

		public static IEnumerable<MultiplayerPlayer> AllPlayers => ConnectedPlayers.Values;

		// New player cursors are created automatically if one doesn't exist
		public static void CreateNewPlayerCursor(ulong steamID)
		{
			using var _ = Profiler.Scope();

			// Nobody is player zero, and nothing can ever clean up a cursor for them.
			//
			// The host built one every session from a client's cursor packet sent
			// before that client's id existed, and it outlived every peer because no
			// disconnect ever matches id 0. Twenty soak runs, twenty failures of the
			// ghost-cursor check. The sender no longer sends it; this is the second
			// layer, and it is the one that holds if any other sender is added.
			if (steamID == 0 || steamID.Equals(Utils.NilUlong()))
			{
				DebugConsole.LogWarning(
					"[MultiplayerSession] refusing to create a cursor for player 0 - " +
					"no player has that id, and nothing would ever remove it");
				return;
			}

			if (PlayerCursors.ContainsKey(steamID))
				return;

			var canvasGO = GameScreenManager.Instance.ssCameraCanvas;
			if (canvasGO == null)
			{
				DebugConsole.LogError("[MultiplayerSession] ssCameraCanvas is null, cannot create cursor.");
				return;
			}

			var cursorGO = new GameObject($"Cursor_{steamID}");
			cursorGO.transform.SetParent(canvasGO.transform, false);
			cursorGO.layer = LayerMask.NameToLayer("UI");

			var playerCursor = cursorGO.AddComponent<PlayerCursor>();

			playerCursor.AssignPlayer(steamID);
			playerCursor.Init();

			PlayerCursors[steamID] = playerCursor;
			DebugConsole.Log($"[MultiplayerSession] Created new cursor for {steamID}");
		}

		public static void CreateConnectedPlayerCursors()
		{
			using var _ = Profiler.Scope();

            var members = NetworkConfig.GetConnectedClients();
			ulong localId = LocalUserID;

            foreach (var playerId in members)
			{
				if (playerId == localId)
					continue;

				if (!PlayerCursors.ContainsKey(playerId))
				{
					CreateNewPlayerCursor(playerId);
				}
			}

			// Then take away the ones that should not be there.
			//
			// Creating from the member list is not enough, because who "we" are is not
			// known at a fixed moment. On a host LocalUserID resolves through
			// RiptideServer.CLIENT_ID, and that is filled in only once the host's own
			// loopback client has finished connecting. Run this before then and the
			// host fails to recognise its own id in the list, builds a cursor for
			// itself, and nothing ever takes it away: ten consecutive soak runs each
			// reported "2 player cursors for 1 remote peer(s)".
			//
			// Pruning makes the outcome depend on the facts as they are now rather than
			// on the order they arrived in, which is the only way to be right about a
			// value that is populated late. It is also self-healing - the disconnect
			// path's cursor removal is commented out, so this is what cleans up after
			// a peer leaves.
			if (localId != 0 && !localId.Equals(Utils.NilUlong()) && members.Count > 0)
			{
				List<ulong> stale = null;
				foreach (var kvp in PlayerCursors)
				{
					if (kvp.Key == localId || !members.Contains(kvp.Key))
						(stale ??= new List<ulong>()).Add(kvp.Key);
				}

				if (stale != null)
				{
					foreach (var id in stale)
					{
						DebugConsole.Log(
							$"[MultiplayerSession] removing cursor {id}: " +
							(id == localId ? "that is us" : "not a connected client"));
						RemovePlayerCursor(id);
					}
				}
			}
		}

		public static void RemovePlayerCursor(ulong playerId)
		{
			using var _ = Profiler.Scope();

			if (!PlayerCursors.TryGetValue(playerId, out var cursor))
				return;

			if (cursor != null && cursor.gameObject != null)
			{
				cursor.RemoveBuildingVisualizer();
				cursor.StopAllCoroutines();
				Object.Destroy(cursor.gameObject);
			}

			PlayerCursors.Remove(playerId);
			DebugConsole.Log($"[MultiplayerSession] Removed player cursor for {playerId}");
		}

		public static void RemoveAllPlayerCursors()
		{
			using var _ = Profiler.Scope();

			foreach (var kvp in PlayerCursors)
			{
				var cursor = kvp.Value;
				if (cursor != null && cursor.gameObject != null)
				{
					cursor.RemoveBuildingVisualizer(); // Remove the building visualizer if there is one
					cursor.StopAllCoroutines();
					Object.Destroy(cursor.gameObject);
				}
			}

			PlayerCursors.Clear();
			DebugConsole.Log("[MultiplayerSession] Removed all player cursors.");
		}

		public static void RefreshAllPlayerCursors()
		{
			using var _ = Profiler.Scope();
			if(Utils.IsInGame())
			{
				RemoveAllPlayerCursors();
				CreateConnectedPlayerCursors();
			}
		}

		public static bool TryGetCursorObject(ulong steamID, out PlayerCursor cursorGO)
		{
			using var _ = Profiler.Scope();

			if (PlayerCursors.TryGetValue(steamID, out var cursor) && cursor != null)
			{
				cursorGO = cursor;
				return true;
			}

			cursorGO = null;
			return false;
		}


	}
}
