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

            // A second connect while one is already in flight strands the client
            // for good, and nothing stopped it. Measured: the client connected as
            // player 2, asked the host for the save, and the host queued a 2.2 MB
            // TCP transfer and sent TcpTransferStartPacket. One second later a
            // duplicate join arrived. It reset the state to Connecting, so the
            // start packet landed on a client that had just thrown away the
            // request it belonged to. The download never began, the host kept the
            // transfer queued for a client that would never collect it, and the
            // client sat at the main menu forever - in-session, registry empty,
            // absorbing operational-state packets for a world it did not have
            // (2196 lookup failures in under a second).
            //
            // Refuse instead. A join is only meaningful from Disconnected or
            // Error; anything else is either the same join arriving twice (a
            // double-click, a retrying UI, a harness redelivering a command) or a
            // join racing a live session, and both are worse than doing nothing.
            if (State != ClientState.Disconnected && State != ClientState.Error)
            {
                // One exception, and it matters more than the rule.
                //
                // A refusal is only correct while something is actually happening.
                // If the state says Connecting but the attempt died - the LAN
                // timeout path used to leave it there, and the transport can also
                // decline a connect request outright - then refusing every later
                // attempt means "press join, nothing happens", forever, until the
                // game is restarted. That is a worse bug than the one the guard was
                // added for, and it is the one that got reported.
                //
                // So a Connecting state that has outlived the connect timeout is
                // treated as dead rather than trusted. Anything else - Connected,
                // LoadingWorld, InGame - is a live session and still refused.
                bool staleAttempt = IsStaleConnectAttempt(State, Time.unscaledTime - _connectingSince);

                if (!staleAttempt)
                {
                    DebugConsole.LogWarning(
                        $"[GameClient] Ignoring connect request while State={State} " +
                        (MultiplayerSession.InSession ? "(already in a session). " : ". ") +
                        "Disconnect first; accepting this would abandon the save transfer in flight.");
                    return;
                }

                DebugConsole.LogWarning(
                    $"[GameClient] State was still Connecting after " +
                    $"{Time.unscaledTime - _connectingSince:0}s with nothing to show for it; " +
                    "treating the previous attempt as dead and retrying. Something failed a " +
                    "connect without reporting it.");
                SetState(ClientState.Disconnected);
            }

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

			_connectingSince = Time.unscaledTime;
			SetState(ClientState.Connecting);
			NetworkConfig.TransportClient.ConnectToHost(ip, port);
		}

		/// <summary>When the current attempt started, so a dead one can be told from a live one.</summary>
		private static float _connectingSince;

		/// <summary>
		/// Past this, a Connecting state is evidence of a failure that was not
		/// reported rather than of a connection in progress. Comfortably above the
		/// transport's own timeout so a slow join is never mistaken for a dead one.
		/// </summary>
		/// <summary>
		/// Derived from the configured timeout, never a constant.
		///
		/// This was hardcoded to 45 seconds "comfortably above the transport's own
		/// timeout", which was true of the 30 second default and false of the
		/// machine it shipped to, where the client timeout is 120. A threshold below
		/// the timeout judges a join that is still legitimately connecting to be
		/// dead, which lets a second connect through and abandons the save transfer
		/// in flight - the exact failure the refusal was added to prevent.
		///
		/// The suite caught it on the first run after deployment. It is the reason
		/// that test compares against the configuration rather than a number.
		/// </summary>
		internal static float StaleConnectSeconds =>
			Configuration.Instance.Client.TimeoutSeconds + 15f;

		/// <summary>
		/// Whether a connect request should be allowed through despite the state
		/// saying one is already under way.
		///
		/// A pure function so it can be tested. The rule it encodes is the one that
		/// decides between two bad outcomes: accept a duplicate join and strand the
		/// client mid-save-transfer, or refuse every join after a silent failure and
		/// make the button dead until restart. Both have happened, so neither reading
		/// is safe to leave to whoever edits this next.
		/// </summary>
		internal static bool IsStaleConnectAttempt(ClientState state, float secondsInState) =>
			state == ClientState.Connecting && secondsInState > StaleConnectSeconds;

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

			// The host's mod version, which this side never looked at either.
			//
			// The two peers can differ by weeks of code and share a packet registry, and
			// then they disagree about the world in ways that look exactly like the sync
			// defects this mod exists to fix. The host already refuses such a client;
			// without this, a client would still join a host it cannot agree with.
			if (!string.IsNullOrEmpty(packet.ModVersion)
				&& packet.ModVersion != ProtocolCompatibility.ModVersion)
			{
				message = string.Format(STRINGS.UI.PROTOCOL.MOD_VERSION_MISMATCH,
					ProtocolCompatibility.ModVersion, packet.ModVersion);
				return false;
			}

			// Same version, different build - said out loud rather than refused.
			//
			// Two people who each compiled the same release get different build ids, and
			// refusing that would stop anyone testing a local build against a friend on
			// the Workshop. What it must not do is stay silent: a mismatched pair
			// produces symptoms indistinguishable from a replication bug, and the last
			// one took an afternoon to find by comparing file hashes by hand.
			if (!string.IsNullOrEmpty(packet.BuildId)
				&& packet.BuildId != ProtocolCompatibility.BuildId)
			{
				DebugConsole.LogWarning(
					$"[Protocol] both peers report mod version {ProtocolCompatibility.ModVersion} " +
					$"but different builds - this peer {ProtocolCompatibility.BuildId}, host " +
					$"{packet.BuildId}. The session will run and any disagreement about the " +
					"world should be read as a build difference first.");
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
