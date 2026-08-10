using ONI_Together.DebugTools;
using ONI_Together.Menus;
using ONI_Together.Misc;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Handshake;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;
using ONI_Together.Networking.States;
using ONI_Together.Networking.Transport.Steamworks;
using ONI_Together.Patches.ToolPatches;
using Shared;
using Shared.Helpers;
using Steamworks;
using System;
using System.Collections;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ONI_Together.Networking
{
	public static class GameClient
	{

		private static ClientState _state = ClientState.Disconnected;
		public static ClientState State => _state;

		private static bool _pollingPaused = false;

		private static CachedConnectionInfo? _cachedConnectionInfo = null;

		public static bool IsHardSyncInProgress = false;

		/// <summary>
		/// Bumped every time a game state is requested, so a timeout armed by an
		/// earlier attempt can tell that it has been superseded. Reconnecting
		/// during a load arms this twice.
		/// </summary>
		private static int _stateRequestGeneration;

		/// <summary>
		/// How long to wait for the host to answer a state request before giving up
		/// and going back to the title screen.
		///
		/// It was ten seconds and unconditional, which is shorter than a save
		/// transfer for a large colony, so the guard fired on healthy joins. The
		/// action now checks whether the world arrived, which is what makes a
		/// generous value safe: this only has to catch a host that never replies.
		/// </summary>
		private const int StateRequestTimeoutSeconds = 45;
		private static bool _modVerificationSent = false;

		// Auto-reconnect state
		private static bool _autoReconnecting = false;
		private static int _reconnectAttempt = 0;
		private const int MAX_RECONNECT_ATTEMPTS = 5;
		private const float RECONNECT_BASE_DELAY = 1f;


		private struct CachedConnectionInfo
		{
			public ulong HostSteamID;
			public string ServerIp;
			public int ServerPort;

			public CachedConnectionInfo(ulong id)
			{
				HostSteamID = id;
			}

			public CachedConnectionInfo(string ip, int port)
            {
                ServerIp = ip;
                ServerPort = port;
            }

        }

		/// <summary>
		/// Returns true if we have cached connection info from a previous session
		/// (used to determine if we need to reconnect after world load)
		/// </summary>
		public static bool HasCachedConnection()
		{
			using var _ = Profiler.Scope();

			return _cachedConnectionInfo.HasValue;
		}

		/// <summary>
		/// Clears the cached connection info after successful reconnection or on error
		/// </summary>
		public static void ClearCachedConnection()
		{
			using var _ = Profiler.Scope();

			_cachedConnectionInfo = null;
		}

		public static void SetState(ClientState newState)
		{
			using var _ = Profiler.Scope();

			if (_state != newState)
			{
				_state = newState;
				DebugConsole.Log($"[GameClient] State changed to: {_state}");
			}
		}

		public static void Init()
		{
			using var _ = Profiler.Scope();

			// I fucking hate this, maybe replace this with hashes?
			NetworkConfig.TransportClient.OnClientDisconnected = () => SetState(ClientState.Disconnected);
			NetworkConfig.TransportClient.OnClientConnected = () => SetState(ClientState.Connected);
			NetworkConfig.TransportClient.OnContinueConnectionFlow = () => ContinueConnectionFlow();
			NetworkConfig.TransportClient.OnReturnToMenu = (reason, message) => CoroutineRunner.RunOne(ShowMessageAndReturnToTitle(reason, message));
			NetworkConfig.TransportClient.OnRequestStateOrReturn = () =>
			{
                PacketSender.SendToHost(GameStateRequestPacket.CreateClientRequest(MultiplayerSession.LocalUserID));

                // A timeout that can be called off, and that knows the difference
                // between "the host never answered" and "the host answered and I
                // am busy doing what it said".
                //
                // This used to arm an unconditional ten second return-to-title on
                // every connect, with nothing anywhere to cancel it. Ten seconds
                // does not cover a save transfer and a world load - a 6800 object
                // colony takes far longer - so the timer fired in the middle of
                // loading, and ShowMessageAndReturnToTitle calls ForceQuitGame,
                // which is Sim.Shutdown() and Grid.CellCount = 0 on a world that
                // is still being built. Twice the client stopped dead a minute
                // into a session with the process still listed, no exception, no
                // shutdown sequence and no Windows event, and both logs show the
                // same four seconds: disconnect, then a world spawning while the
                // client state still reads Disconnected, then silence.
                //
                // MP_Timer holds one action and one deadline with no way to
                // withdraw either, so the check goes in the action.
                int generation = ++_stateRequestGeneration;
                MP_Timer.Instance.StartDelayedAction(StateRequestTimeoutSeconds, () =>
                {
                    // A later request replaced this one - reconnecting during a
                    // load arms it twice, which is how a stale timer from the
                    // first attempt used to land in the middle of the second.
                    if (generation != _stateRequestGeneration)
                        return;

                    // Any of these means the host answered.
                    if (Utils.IsInGame() || State == ClientState.LoadingWorld || IsHardSyncInProgress)
                    {
                        DebugConsole.Log(
                            "[GameClient] state request timed out but the world is here or on its way " +
                            $"(state={State}, inGame={Utils.IsInGame()}, hardSync={IsHardSyncInProgress}) - staying");
                        return;
                    }

                    DebugConsole.LogWarning(
                        $"[GameClient] no game state from the host after {StateRequestTimeoutSeconds}s - returning to the title screen");
                    CoroutineRunner.RunOne(ShowMessageAndReturnToTitle());
                });
            };
            NetworkConfig.TransportClient.Prepare();
            CursorManager.Instance.AssignColor();
        }

		public static void ConnectToHost(bool showLoadingScreen = true, string ip = "", int port = 7777)
		{
			using var _ = Profiler.Scope();

            Init();

            // Reset mod verification for new connection attempts
            _modVerificationSent = false;

			if (showLoadingScreen)
			{
				string hostName = "uknown host";
				if (NetworkConfig.IsSteamConfig())
				{
					hostName = SteamFriends.GetFriendPersonaName(MultiplayerSession.HostUserID.AsCSteamID());
                }
				else if (NetworkConfig.IsLanConfig())
				{
					hostName = $"{ip}:{port}";
                }
					MultiplayerOverlay.Show(string.Format(STRINGS.UI.MP_OVERLAY.CLIENT.CONNECTING_TO_HOST, hostName));
			}

			SetState(ClientState.Connecting);
			NetworkConfig.TransportClient.ConnectToHost(ip, port);
		}

		public static void Disconnect()
		{
			using var _ = Profiler.Scope();

			NetworkConfig.TransportClient.Disconnect();
		}

		public static void ReconnectToSession()
		{
			using var _ = Profiler.Scope();

			NetworkConfig.TransportClient.ReconnectToSession();
		}

		public static void Poll()
		{
			using var _ = Profiler.Scope();

			if (_pollingPaused)
				return;

			NetworkConfig.TransportClient.Update();

			switch (State)
			{
				case ClientState.Connected:
				case ClientState.InGame:
					NetworkConfig.TransportClient.OnMessageRecieved();
					break;
				case ClientState.Connecting:
				case ClientState.Disconnected:
				case ClientState.Error:
				default:
					break;
			}
		}

		public static void OnHostResponseReceived(GameStateRequestPacket packet)
		{
			using var _ = Profiler.Scope();

			DebugConsole.Log("Gamestate packet received");
			MP_Timer.Instance.Abort();
			if (!TryValidateHostProtocol(packet, out string protocolReason, out string protocolMessage))
			{
				DebugConsole.LogWarning($"[GameClient] Host protocol validation failed: {protocolReason} | {protocolMessage}");
				Disconnect();
				NetworkConfig.TransportClient.OnReturnToMenu.Invoke(protocolReason, protocolMessage);
				return;
			}

			if (MultiplayerSession.GetPlayer(MultiplayerSession.HostUserID) is MultiplayerPlayer host)
			{
				host.ProtocolVerified = true;
			}

			if (!SaveHelper.SavegameDlcListValid(packet.ActiveDlcIds, out var errorMsg))
			{
				DebugConsole.Log("invalid dlc config detected");
				SaveHelper.ShowMessageAndReturnToMainMenu(errorMsg);
				return;
			}

			if (!SaveHelper.SteamModListSynced(packet.ActiveModIds, out var notEnabled, out var notDisabled, out var missingMods))
			{
				string text = STRINGS.UI.MP_OVERLAY.SYNC.MODSYNC.TEXT + "\n\n";
				if (notEnabled.Any())
					text += string.Format(STRINGS.UI.MP_OVERLAY.SYNC.MODSYNC.TOENABLE, notEnabled.Count) +"\n";
				if (notDisabled.Any())
					text += string.Format(STRINGS.UI.MP_OVERLAY.SYNC.MODSYNC.TODISABLE, notDisabled.Count) + "\n";
				if (missingMods.Any())
					text += string.Format(STRINGS.UI.MP_OVERLAY.SYNC.MODSYNC.MISSING, missingMods.Count) + "\n";

				// Ignore this if we're in game already
				if (Utils.IsInMenu())
				{
					DialogUtil.CreateConfirmDialogFrontend(STRINGS.UI.MP_OVERLAY.SYNC.MODSYNC.TITLE, text,
		   STRINGS.UI.MP_OVERLAY.SYNC.MODSYNC.CONFIRM_SYNC,
					() => { SaveHelper.SyncModsAndRestart(notEnabled, notDisabled, missingMods); },
					STRINGS.UI.MP_OVERLAY.SYNC.MODSYNC.CANCEL,
					BackToMainMenu,
					STRINGS.UI.MP_OVERLAY.SYNC.MODSYNC.DENY_SYNC,
					ContinueConnectionFlow);
					DebugConsole.Log("mods not synced!");
				}
				return;
			}

			ContinueConnectionFlow();
		}

		private static bool TryValidateHostProtocol(GameStateRequestPacket packet, out string reason, out string message)
		{
			using var _ = Profiler.Scope();

			if(Configuration.Instance.BypassProtocolCompatibilityChecks)
			{
				reason = string.Empty;
				message = string.Empty;
				return true;
			}

			reason = STRINGS.UI.PROTOCOL.VALIDATION.TITLE;
			message = string.Empty;

			if (!packet.HasProtocolMetadata)
			{
				message = STRINGS.UI.PROTOCOL.VALIDATION.NO_METADATA;
				return false;
			}

			if (!packet.ProtocolAccepted)
			{
				message = string.IsNullOrEmpty(packet.ProtocolFailureReason)
					? STRINGS.UI.PROTOCOL.VALIDATION.REJECTED
					: packet.ProtocolFailureReason;
				return false;
			}

			if (packet.ProtocolVersion != ProtocolCompatibility.CurrentProtocolVersion)
			{
				message = string.Format(STRINGS.UI.PROTOCOL.VALIDATION.PROTOCOL_MISMATCH, packet.ProtocolVersion, ProtocolCompatibility.CurrentProtocolVersion);
				return false;
			}

			if (packet.PacketRegistryFingerprint != ProtocolCompatibility.PacketFingerprint)
			{
				message = string.Format(STRINGS.UI.PROTOCOL.VALIDATION.FINGERPRINT_MISMATCH, packet.PacketRegistryFingerprint, ProtocolCompatibility.PacketFingerprint);
				return false;
			}

			return true;
		}
		static void BackToMainMenu()
		{
			using var _ = Profiler.Scope();

			MultiplayerOverlay.Close();
			NetworkIdentityRegistry.Clear();
			NetworkConfig.Stop();
			App.LoadScene("frontend");
		}

        private static void ContinueConnectionFlow()
		{
			using var _ = Profiler.Scope();

			// CRITICAL: Only execute on client, never on server
			if (MultiplayerSession.IsHost)
			{
				DebugConsole.Log("[GameClient] ContinueConnectionFlow called on host - ignoring");
				return;
			}

			DebugConsole.Log($"[GameClient] ContinueConnectionFlow - IsInMenu: {Utils.IsInMenu()}, IsInGame: {Utils.IsInGame()}, HardSyncInProgress: {IsHardSyncInProgress}");

			ReadyManager.SendReadyStatusPacket(ClientReadyState.Unready);

			if (Utils.IsInMenu())
			{
				DebugConsole.Log("[GameClient] Client is in menu - requesting save file or sending ready status");

				// CRITICAL: Enable packet processing BEFORE requesting save file
				// Otherwise, host packets will be discarded!
				PacketHandler.readyToProcess = true;
				DebugConsole.Log("[GameClient] PacketHandler.readyToProcess = true (menu)");

				// Show overlay with localized message
				MultiplayerOverlay.Show(string.Format(STRINGS.UI.MP_OVERLAY.CLIENT.WAITING_FOR_PLAYER, SteamFriends.GetFriendPersonaName(MultiplayerSession.HostUserID.AsCSteamID())));
				if (!IsHardSyncInProgress)
				{
					DebugConsole.Log("[GameClient] Requesting save file from host");
					var packet = new SaveFileRequestPacket
					{
						Requester = MultiplayerSession.LocalUserID
					};
					PacketSender.SendToHost(packet);
				}
				else
				{
					// Loading, not Ready - this branch runs in the main menu.
					//
					// It used to report Ready from here, which is the opposite of
					// true: the client is sitting in the menu waiting for a hard
					// sync and has no world, no grid and an empty identity
					// registry. Nothing structural depended on the value - the
					// host only counts Ready players for the lobby text - but two
					// things read it and were misled.
					//
					// The send gate for NetId-addressed packets opens on anything
					// that is not Loading, so it opened here and world state went
					// to a peer that could not apply a word of it. And the damage
					// syncer re-asserts when a peer becomes ready: it fired twice
					// per session, once against this phantom Ready and once
					// against the real one, and the first batch of 36 packets was
					// the "unresolved=36" that appeared in every run and that I
					// spent four iterations failing to explain.
					DebugConsole.Log("[GameClient] Hard sync in progress, reporting Loading");
					ReadyManager.SendReadyStatusPacket(ClientReadyState.Loading);
				}
			}
			else if (Utils.IsInGame())
			{
				DebugConsole.Log("[GameClient] Client is in game - treating as reconnection");

				// We're in game already. Consider this a reconnection
				SetState(ClientState.InGame);

				// CRÍTICO: Habilitar processamento de pacotes
				PacketHandler.readyToProcess = true;
				DebugConsole.Log("[GameClient] PacketHandler.readyToProcess = true");

				if (IsHardSyncInProgress)
				{
					IsHardSyncInProgress = false;
					DebugConsole.Log("[GameClient] Cleared HardSyncInProgress flag");
				}

				Game.Instance?.Trigger(MP_HASHES.GameClient_OnConnectedInGame);
                ReadyManager.SendReadyStatusPacket(ClientReadyState.Ready);
				MultiplayerSession.CreateConnectedPlayerCursors();

				//CursorManager.Instance.AssignColor();
				SelectToolPatch.UpdateColor();

				// Fechar overlay se reconectou com sucesso
				MultiplayerOverlay.Close();

				// Reset reconnect state on successful connection
				ResetReconnectState();

				DebugConsole.Log("[GameClient] Reconnection setup complete");
			}
			else
			{
				DebugConsole.LogWarning("[GameClient] Client is neither in menu nor in game - unexpected state");
			}
		}

		private static IEnumerator AutoReconnectCoroutine()
		{
			if (_autoReconnecting) yield break;
			_autoReconnecting = true;
			_reconnectAttempt++;

			float delay = Mathf.Min(RECONNECT_BASE_DELAY * Mathf.Pow(2, _reconnectAttempt - 1), 30f);
			DebugConsole.Log($"[GameClient] Auto-reconnect attempt {_reconnectAttempt}/{MAX_RECONNECT_ATTEMPTS} in {delay}s");
			MultiplayerOverlay.Show($"Reconnecting... attempt {_reconnectAttempt}/{MAX_RECONNECT_ATTEMPTS}");

			yield return new WaitForSecondsRealtime(delay);

			if (!Utils.IsInGame())
			{
				DebugConsole.Log("[GameClient] No longer in game, aborting reconnect");
				_autoReconnecting = false;
				_reconnectAttempt = 0;
				yield break;
			}

			try
			{
				ReconnectToSession();
			}
			catch (Exception ex)
			{
				DebugConsole.LogError($"[GameClient] Reconnect attempt {_reconnectAttempt} failed: {ex}");
			}

			_autoReconnecting = false;
		}

		public static void ResetReconnectState()
		{
			_autoReconnecting = false;
			_reconnectAttempt = 0;
		}

		private static IEnumerator ShowMessageAndReturnToTitle(string reason = "", string message = "")
		{
			// Auto-reconnect if still in game and under max attempts
			//if (Utils.IsInGame() && _reconnectAttempt < MAX_RECONNECT_ATTEMPTS)
			//{
			//	CoroutineRunner.RunOne(AutoReconnectCoroutine());
			//	yield break;
			//}

			// Reset on final failure
			_reconnectAttempt = 0;
			_autoReconnecting = false;

            MultiplayerOverlay.Show(string.Format(STRINGS.UI.MP_OVERLAY.CLIENT.LOST_CONNECTION, reason, message));
			//SaveHelper.CaptureWorldSnapshot();
			yield return new WaitForSecondsRealtime(3f);
			//PauseScreen.TriggerQuitGame(); // Force exit to frontend, getting a crash here
			if (Utils.IsInGame())
			{
				Utils.ForceQuitGame();
			}
			App.LoadScene("frontend");

			MultiplayerOverlay.Close();
			NetworkIdentityRegistry.Clear();
			NetworkConfig.Stop();
		}

		public static void CacheCurrentServer()
		{
			using var _ = Profiler.Scope();

			if(NetworkConfig.IsSteamConfig())
			{
                if (MultiplayerSession.HostUserID != Utils.NilUlong())
                {
                    _cachedConnectionInfo = new CachedConnectionInfo(
                            MultiplayerSession.HostUserID
                    );
                }
            }
			else if(NetworkConfig.IsLanConfig())
			{
				_cachedConnectionInfo = new CachedConnectionInfo(
                    MultiplayerSession.ServerIp,
                    MultiplayerSession.ServerPort
                );
            }
		}

		public static void ReconnectFromCache()
		{
			using var _ = Profiler.Scope();

			if (_cachedConnectionInfo.HasValue)
			{
				if(NetworkConfig.IsSteamConfig())
				{
                    DebugConsole.Log($"[GameClient] Reconnecting to cached server: {_cachedConnectionInfo.Value.HostSteamID}");
                    var hostId = _cachedConnectionInfo.Value.HostSteamID;
                    _cachedConnectionInfo = null; // Clear cache to prevent re-triggering
                    MultiplayerSession.HostUserID = hostId;
                    ConnectToHost(false);
                }
				else if(NetworkConfig.IsLanConfig())
				{
                    // Printed the port twice, so this read "8080:8080" and looked like a
                    // corrupted address while the connection itself was fine. A log
                    // that lies costs a session; this one nearly did.
                    DebugConsole.Log($"[GameClient] Reconnecting to cached server: {_cachedConnectionInfo.Value.ServerIp}:{_cachedConnectionInfo.Value.ServerPort}");
                    var ip = _cachedConnectionInfo.Value.ServerIp;
                    var port = _cachedConnectionInfo.Value.ServerPort;
                    _cachedConnectionInfo = null; // Clear cache to prevent re-triggering
                    ConnectToHost(false, ip, port);
                }
			}
		}
	}
}
