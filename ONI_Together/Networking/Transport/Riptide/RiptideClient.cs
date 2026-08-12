using System;
using System.Net;
using Riptide;
using Riptide.Utils;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using ONI_Together.Misc;
using System.Collections.Concurrent;
using ONI_Together.Menus;
using System.Collections.Generic;
using UnityEngine;
using System.Collections;
using ONI_Together.Networking.States;
using ONI_Together.UI;
using Steamworks;
using static ONI_Together.STRINGS.UI.MP_OVERLAY;

namespace ONI_Together.Networking.Transport.Lan
{
    public class RiptideClient : TransportClient
    {
        private static Client _client;
        public bool IsLoadingReconnect { get; set; } = false;

        public static Client Client
        {
            get { return _client; }
        }

        private static readonly ConcurrentQueue<byte[]> _incomingPackets = new ConcurrentQueue<byte[]>();

        // Network health
        private const int JITTER_SAMPLE_COUNT = 20;
        private readonly Queue<int> _pingSamples = new Queue<int>();

        private ConnectionMetrics Metrics => _client?.Connection?.Metrics;

        public List<ulong> ClientList { get; private set; } = new();
        public static ulong CLIENT_ID { get; private set; }

        public override void Prepare()
        {
            using var _ = Profiler.Scope();

            RiptideLogger.Initialize(DebugConsole.Log, false);
        }

        public override void ConnectToHost(string ip, int port)
        {
            using var _ = Profiler.Scope();

            if (_client != null)
            {
                if (!_client.IsNotConnected)
                {
                    // Silent before. The caller has already moved the client state
                    // to Connecting, so returning without a word leaves it there
                    // with nothing running that will ever change it.
                    DebugConsole.LogWarning(
                        "[LanClient] a connection is already in progress or established; " +
                        "ignoring this connect request. Disconnect first.");
                    return;
                }
            }

            MultiplayerSession.ServerIp = ip;
            MultiplayerSession.ServerPort = port;
            _client = new Client("RiptideClient");
            _client.TimeoutTime = Configuration.Instance.Client.TimeoutSeconds * 1000;

            int timeout = Configuration.Instance.Client.TimeoutSeconds;
            _client.Connected += OnConnectedToServer;
            _client.Disconnected += OnDisconnectedFromServer;
            _client.MessageReceived += OnMessageRecievedFromServer;
            _client.ClientConnected += OnOtherClientConnected;
            _client.ClientDisconnected += OnOtherClientDisconnected;
            DebugConsole.Log($"Connecting to {ip}:{port}");
            CoroutineRunner.RunOne(WaitForConnectionSuccess(timeout));
            _client.Connect($"{ip}:{port}", useMessageHandlers: false);
        }

        private void OnOtherClientDisconnected(object sender, ClientDisconnectedEventArgs e)
        {
            using var _ = Profiler.Scope();

            RemoveClientFromList(e.Id);
            //MultiplayerSession.RemovePlayerCursor(e.Id);
            MultiplayerSession.RefreshAllPlayerCursors();
        }

        private void OnOtherClientConnected(object sender, ClientConnectedEventArgs e)
        {
            using var _ = Profiler.Scope();

            AddClientToList(e.Id);
        }

        private void OnMessageRecievedFromServer(object sender, MessageReceivedEventArgs e)
        {
            using var _ = Profiler.Scope();

            byte[] rawData = e.Message.GetBytes();
            _incomingPackets.Enqueue(rawData);
        }

        private void OnConnectedToServer(object sender, EventArgs e)
        {
            using var _ = Profiler.Scope();

            CLIENT_ID = GetClientID();
            AddClientToList(CLIENT_ID);

            var conn = _client.Connection;
            conn.CanQualityDisconnect = false; // prevents auto‑disconnect due to poor delivery
            conn.MaxSendAttempts = 30;         // 15 is default so we'll double it
            conn.MaxAvgSendAttempts = 12;      // Was 5, we'll double it and add a buffer
            conn.AvgSendAttemptsResilience = 128; // was 64, doubled

            OnClientConnected.Invoke();
            MultiplayerSession.SetHost(1); // Host's client is always 1
            MultiplayerSession.InSession = true;
            PacketHandler.readyToProcess = true;

            // The clients MultiplayerSession.ConnectedPlayers should only ever contain the host
            MultiplayerPlayer host = new MultiplayerPlayer(1);
            host.Connection = conn;
            MultiplayerSession.ConnectedPlayers.Add(1, host);

            MultiplayerSession.KnownPlayerNames[CLIENT_ID] = Utils.GetLocalPlayerName();

            DebugConsole.Log($"[Riptide] Connected to server with Client ID: {CLIENT_ID}");

            //CoroutineRunner.RunOne(Handshake());
            NetworkConfig.TransportClient.OnRequestStateOrReturn.Invoke();
        }

        private void OnDisconnectedFromServer(object sender, DisconnectedEventArgs e)
        {
            using var _ = Profiler.Scope();

            // Read before anything is told about the disconnect.
            //
            // OnClientDisconnected sets the client state to Disconnected, so a
            // guard that reads the state after invoking it can never see what the
            // peer was doing when the connection dropped. The first version of the
            // LoadingWorld check below did exactly that and was dead code: the log
            // shows LoadingWorld -> Disconnected, in that order, with the
            // disconnect handler in between.
            var stateWhenDropped = GameClient.State;

            RemoveClientFromList(CLIENT_ID);
            CLIENT_ID = Utils.NilUlong();

            OnClientDisconnected?.Invoke();
            MultiplayerSession.ConnectedPlayers.Clear();

            DisconnectReason disconnectReason = e.Reason;
            var (reason, message) = GetDisconnectInfo(e);

            // A disconnect during a world load is expected and must not send this
            // peer back to the title screen.
            //
            // The Steam path has had this guard all along - SteamworksClient
            // returns early when the state is LoadingWorld, with a comment saying
            // the reconnect happens once the load finishes. Riptide never got it,
            // and it is listed in the project notes as a known trap: on LAN, a
            // disconnect mid-load kicks the client out. Returning to the title
            // screen is not a cosmetic outcome either, because it runs
            // ForceQuitGame - Sim.Shutdown() and Grid.CellCount = 0 - against a
            // world that is still being built.
            if (stateWhenDropped == States.ClientState.LoadingWorld)
            {
                // Ignoring it was only half the job, and the comment said so while
                // the code did not: nothing reconnected. A run ended with the client
                // sitting in its own single-player colony, role=solo, having quietly
                // stopped being in the session at all.
                //
                // The cause is on the other side. The host logs "Could not guarantee
                // delivery of a Welcome message after 15 attempts! Disconnecting..." -
                // fifteen is Riptide's default, and RiptideServer only raises
                // MaxSendAttempts to 30 inside ClientConnected, which fires after the
                // handshake this message belongs to. So the one message whose loss
                // kills a join is sent with the least resilience, and the generous
                // setting applies only once the join has already worked.
                //
                // Meanwhile this peer cannot ack anything: Unity's main thread is
                // inside a 2.5 MB save load. Whether the handshake survives comes down
                // to how long that load takes, which is why it fails on some runs and
                // not others.
                //
                // Retried here rather than fixed there, because the server-side
                // default belongs to a library this project references as a binary.
                // The address is captured first - CleanupRiptide clears the session's
                // idea of where the host is.
                _reconnectIp = MultiplayerSession.ServerIp;
                _reconnectPort = MultiplayerSession.ServerPort;
                _reconnectAttempts = 0;
                _reconnectAfterLoadAt = Time.unscaledTime + ReconnectFirstDelaySeconds;

                DebugConsole.Log(
                    $"[Riptide] disconnected ({reason}) while the world was loading - " +
                    $"will reconnect to {_reconnectIp}:{_reconnectPort} once the load finishes");

                CleanupRiptide();
                return;
            }

            switch (disconnectReason) {
                case DisconnectReason.Disconnected:
                    // Initiated by client do nothing
                    break;
                default:
                    NetworkConfig.TransportClient.OnReturnToMenu.Invoke(reason, message);
                    break;
            }

            CleanupRiptide();
        }

        public override void Disconnect()
        {
            using var _ = Profiler.Scope();

            if (_client == null)
                return;

            if (_client.IsNotConnected)
                return;

            _client.Disconnect();
        }

        public override void OnMessageRecieved()
        {
            using var _ = Profiler.Scope();

            while (_incomingPackets.TryDequeue(out var rawData))
            {
                int size = rawData.Length;

                int packetType = rawData.Length >= 4
                    ? BitConverter.ToInt32(rawData, 0)
                    : 0;

                var scope = Profiler.Scope();

                try
                {
                    PacketHandler.HandleIncoming(rawData);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LanClient] Failed to handle packet {packetType}: {ex}");
                }

                scope.End(1, size);
            }
        }

        public override void ReconnectToSession()
        {
            using var _ = Profiler.Scope();

            string ip = MultiplayerSession.ServerIp;
            int port = MultiplayerSession.ServerPort;
            Disconnect();
            ConnectToHost(ip, port);
        }

        // Static, and ticked from a component rather than from Update().
        //
        // The first version kept these on the instance and drove them from this
        // class's Update. The flag was set correctly - the log shows the right
        // address, 192.168.45.39:8080, not the 127.0.0.1:7777 this code used to
        // reset to - and the attempt never happened. Whatever pumps the transport
        // stops pumping it once the session is gone, which is exactly when a
        // reconnect is needed, and the instance may be replaced across a cleanup
        // anyway. So the state outlives the instance and something that always
        // ticks does the driving.
        private static string _reconnectIp;
        private static int _reconnectPort;

        /// <summary>Zero means no reconnect is owed.</summary>
        private static float _reconnectAfterLoadAt;
        private static int _reconnectAttempts;

        /// <summary>Long enough for the load to finish and the world to settle.</summary>
        private const float ReconnectFirstDelaySeconds = 8f;
        private const float ReconnectRetrySeconds = 10f;

        /// <summary>
        /// Bounded on purpose. A client that cannot get back in should stop and say
        /// so; retrying forever turns one failed join into a permanent load on the
        /// host and hides the failure from the log.
        /// </summary>
        private const int ReconnectMaxAttempts = 3;

        public override void Update()
        {
            using var _ = Profiler.Scope();

            _client?.Update();
        }

        /// <summary>
        /// Get back into the session that was lost while the world was loading.
        ///
        /// Driven from Update because the transport is already pumped every frame and
        /// this needs no more than that. Guarded so it can only act in the exact
        /// situation it was written for: a reconnect is owed, the world has finished
        /// loading, and this peer is not in a session.
        /// </summary>
        public static void TryReconnectAfterLoad()
        {
            if (_reconnectAfterLoadAt <= 0f) return;

            // Still loading, or not loaded at all. Reconnecting mid-load would
            // reproduce the failure it is meant to recover from.
            if (Game.Instance == null) return;
            if (GameClient.State == States.ClientState.LoadingWorld) return;

            // Something else got us back in - a manual rejoin, or the load flow's own
            // connect. Nothing owed.
            if (MultiplayerSession.InSession || GameClient.State == States.ClientState.Connected)
            {
                DebugConsole.Log("[Riptide] back in the session already; dropping the pending reconnect");
                _reconnectAfterLoadAt = 0f;
                return;
            }

            if (Time.unscaledTime < _reconnectAfterLoadAt) return;

            if (_reconnectAttempts >= ReconnectMaxAttempts)
            {
                DebugConsole.LogWarning(
                    $"[Riptide] gave up reconnecting after {_reconnectAttempts} attempts - " +
                    "this peer loaded the world and is not in the session");
                _reconnectAfterLoadAt = 0f;
                return;
            }

            if (string.IsNullOrEmpty(_reconnectIp) || _reconnectPort <= 0)
            {
                DebugConsole.LogWarning(
                    $"[Riptide] cannot reconnect: no host address was captured ('{_reconnectIp}':{_reconnectPort})");
                _reconnectAfterLoadAt = 0f;
                return;
            }

            _reconnectAttempts++;
            _reconnectAfterLoadAt = Time.unscaledTime + ReconnectRetrySeconds;

            DebugConsole.Log(
                $"[Riptide] reconnecting after the load, attempt {_reconnectAttempts} of " +
                $"{ReconnectMaxAttempts}, to {_reconnectIp}:{_reconnectPort}");

            // Through the session's current transport, not through this instance:
            // the one that was dropped may already have been replaced.
            NetworkConfig.TransportClient?.ConnectToHost(_reconnectIp, _reconnectPort);
        }

        private ulong GetClientID()
        {
            using var _ = Profiler.Scope();

            if (_client == null || _client.IsNotConnected)
                return Utils.NilUlong();

            return _client.Id;
        }

        public void AddClientToList(ulong id)
        {
            using var _ = Profiler.Scope();

            if (ClientList.Contains(id))
                return;

            ClientList.Add(id);

            Game.Instance?.Trigger(MP_HASHES.OnPlayerJoined);
        }

        public void RemoveClientFromList(ulong id)
        {
            using var _ = Profiler.Scope();

            if (!ClientList.Contains(id))
                return;

            ClientList.Remove(id);

            if (id == CLIENT_ID && GameClient.State == ClientState.LoadingWorld)
            {
                IsLoadingReconnect = true;
            }
            else
            {
                string name = MultiplayerSession.KnownPlayerNames.TryGetValue(id, out var cached) ? cached : $"Player {id}";
                ChatScreen.PendingMessage pending = ChatScreen.GeneratePendingMessage(string.Format(STRINGS.UI.MP_CHATWINDOW.CHAT_CLIENT_LEFT, name));
                ChatScreen.QueueMessage(pending);
                Utils.PauseSimOnPlayerLeft();
            }
            Game.Instance?.Trigger(MP_HASHES.OnPlayerLeft);
        }

        public override NetworkIndicatorsScreen.NetworkState GetJitterState()
        {
            using var _ = Profiler.Scope();

            if (_client == null || !_client.IsConnected)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            int ping = _client.RTT;
            if (ping <= 0)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            _pingSamples.Enqueue(ping);
            while (_pingSamples.Count > JITTER_SAMPLE_COUNT)
                _pingSamples.Dequeue();

            if (_pingSamples.Count < 5)
                return NetworkIndicatorsScreen.NetworkState.DEGRADED;

            float mean = 0f;
            foreach (var p in _pingSamples)
                mean += p;
            mean /= _pingSamples.Count;

            float variance = 0f;
            foreach (var p in _pingSamples)
            {
                float diff = p - mean;
                variance += diff * diff;
            }

            float jitter = Mathf.Sqrt(variance / _pingSamples.Count);

            if (jitter <= 10f)
                return NetworkIndicatorsScreen.NetworkState.GOOD;

            if (jitter <= 30f)
                return NetworkIndicatorsScreen.NetworkState.DEGRADED;

            return NetworkIndicatorsScreen.NetworkState.BAD;
        }

        public override NetworkIndicatorsScreen.NetworkState GetLatencyState()
        {
            using var _ = Profiler.Scope();

            if (_client == null || !_client.IsConnected)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            int ping = _client.SmoothRTT;
            if (ping <= 0)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            if (ping <= NetworkConfig.PingRanges.DEGRADED)
                return NetworkIndicatorsScreen.NetworkState.GOOD;

            if (ping <= NetworkConfig.PingRanges.BAD)
                return NetworkIndicatorsScreen.NetworkState.DEGRADED;

            return NetworkIndicatorsScreen.NetworkState.BAD;
        }

        public override NetworkIndicatorsScreen.NetworkState GetPacketlossState()
        {
            using var _ = Profiler.Scope();

            var metrics = Metrics;
            if (metrics == null)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            float lossRate = metrics.RollingNotifyLossRate; // 0–1
            float quality = 1f - lossRate;

            if (quality >= 0.95f)
                return NetworkIndicatorsScreen.NetworkState.GOOD;

            if (quality >= 0.85f)
                return NetworkIndicatorsScreen.NetworkState.DEGRADED;

            return NetworkIndicatorsScreen.NetworkState.BAD;
        }

        public override NetworkIndicatorsScreen.NetworkState GetServerPerformanceState()
        {
            using var _ = Profiler.Scope();

            // Until this is improved later just assume good.
            return NetworkIndicatorsScreen.NetworkState.GOOD;

            if (_client == null || !_client.IsConnected)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            var metrics = Metrics;
            if (metrics == null)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            var reliableSends = metrics.RollingReliableSends;

            double meanResends = reliableSends.Mean;
            double resendStdDev = reliableSends.StandardDev;

            float lossRate = metrics.RollingNotifyLossRate;
            float remoteQuality = 1f - lossRate;


            DebugConsole.Log(
                $"[NET] Resends(mean={meanResends:F2}, std={resendStdDev:F2}) | " +
                $"Loss={lossRate:P2} | Quality={remoteQuality:P2}"
            );

            if (meanResends >= 2.0 ||           // On average needs 2+ sends per reliable
                resendStdDev >= 1.0 ||          // Highly unstable resend behavior
                remoteQuality <= 0.85f)         // Bad server quality
            {
                return NetworkIndicatorsScreen.NetworkState.BAD;
            }

            if (meanResends >= 1.2 ||            // Frequent retransmits
                resendStdDev >= 0.5 ||           // Congestion spikes
                remoteQuality <= 0.95f)          // Degraded server quality
            {
                return NetworkIndicatorsScreen.NetworkState.DEGRADED;
            }

            return NetworkIndicatorsScreen.NetworkState.GOOD;
        }

        IEnumerator WaitForConnectionSuccess(int timeout)
        {
            using var _ = Profiler.Scope();

            float timer = 0f;

            bool wasSuccessful = false;
            while (timer < timeout)
            {
                _client?.Update(); // Update needs to happen during this process so that the client can acknowledge the connection and trigger the Connected event
                if (_client != null && _client.IsConnected)
                {
                    DebugConsole.Log("[LanClient] Connection successful");
                    MultiplayerOverlay.Close();
                    wasSuccessful = true;
                    yield break;
                }

                timer += Time.deltaTime;
                yield return null;
            }

            if (!wasSuccessful)
            {
                CleanupRiptide();

                MultiplayerOverlay.Show(STRINGS.UI.MP_OVERLAY.CLIENT.CONNECTION_FAILED);
                yield return new WaitForSeconds(3f);
                MultiplayerOverlay.Close();
            } else
            {
                yield return null;
            }
        }

        void CleanupRiptide()
        {
            using var _ = Profiler.Scope();

            // Timeout reached — double check we didn't connect at the last frame
            if (_client != null && !_client.IsConnected)
            {
                DebugConsole.LogWarning("[LanClient] Connection timed out");

                /*
                if (MultiplayerSession.IsClient)
                {
                    // Display lost connection to host and return to the main menu
                    NetworkConfig.TransportClient.OnReturnToMenu.Invoke("Connection lost.", "Timed out");
                }
                */

                try
                {
                    _client.Disconnect();

                    _client.Connected -= OnConnectedToServer;
                    _client.Disconnected -= OnDisconnectedFromServer;
                    _client.MessageReceived -= OnMessageRecievedFromServer;
                    _client.ClientConnected -= OnOtherClientConnected;
                    _client.ClientDisconnected -= OnOtherClientDisconnected;

                    // The server address is deliberately left alone. This used to
                    // reset it to 127.0.0.1:7777 on every disconnect, and
                    // ReconnectToSession reads exactly these fields - so every
                    // reconnect dialled localhost on a port nothing listens on
                    // and could never succeed. The address of the server we were
                    // just talking to is the one piece of state a reconnect
                    // needs; cleaning it up is what broke reconnect.
                    //
                    // 7777 is not the LAN default either; LanSettings.Port is
                    // 8080, so even a local host would have been the wrong port.
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LanClient] Error during timeout cleanup: {ex}");
                }

                _client = null;
            }

            MultiplayerSession.HostUserID = Utils.NilUlong();
            MultiplayerSession.InSession = false;

            // Say so, or the client is left believing a connection is still in
            // progress forever.
            //
            // GameClient goes to Connecting before calling into the transport, and
            // the only two things that move it off Connecting are the transport's
            // connected and disconnected callbacks. Neither fires here: this path
            // unsubscribes both handlers a few lines up, and Riptide does not raise
            // Disconnected for an attempt that never connected. So a failed join
            // left the state at Connecting permanently.
            //
            // That was survivable while a repeat join simply tried again. It is not
            // now - a duplicate join is refused, because accepting one mid-transfer
            // stranded the client - so without this, one failed join means every
            // later click does nothing until the game is restarted. Which is the
            // symptom that was reported: "sometimes pressing join does nothing".
            if (GameClient.State != States.ClientState.Disconnected)
                GameClient.SetState(States.ClientState.Disconnected);
        }

        /*IEnumerator Handshake()
        {
            // Recycle the handshake packet
            HandshakePacket handshake = new HandshakePacket();
            while (_client != null && _client.IsConnected)
            {
                if (_client.Connection == null)
                {
                    Debug.Log("[Handshake] Connection is null, waiting...");
                    yield return null;
                    continue;
                }

                Debug.Log("[Handshake] Sending handshake packet...");
                NetworkConfig.TransportPacketSender.SendToConnection(_client.Connection, handshake, SteamNetworkingSend.Reliable);

                yield return new WaitForSeconds(1f);

                if (_client.IsNotConnected)
                {
                    Debug.Log("[Handshake] Client disconnected, stopping handshake coroutine.");
                    break;
                }
            }
            yield return null;
        }*/

        private (string reason, string message) GetDisconnectInfo(DisconnectedEventArgs e)
        {
            switch (e.Reason)
            {
                case DisconnectReason.NeverConnected:
                    return (
                        CLIENT.RIPTIDE.CONNECTION_FAILED,
                        CLIENT.RIPTIDE.CONNECTION_FAILED_DESC
                    );

                case DisconnectReason.ConnectionRejected:
                    return (
                        CLIENT.RIPTIDE.CONNECTION_REJECTED,
                        CLIENT.RIPTIDE.CONNECTION_REJECTED_DESC
                    );

                case DisconnectReason.TransportError:
                    return (
                        CLIENT.RIPTIDE.NETWORK_ERROR,
                        CLIENT.RIPTIDE.NETWORK_ERROR_DESC
                    );

                case DisconnectReason.TimedOut:
                    return (
                        CLIENT.RIPTIDE.CONNECTION_TIMED_OUT,
                        CLIENT.RIPTIDE.CONNECTION_TIMED_OUT_DESC
                    );

                case DisconnectReason.Kicked:
                    return (
                        CLIENT.RIPTIDE.KICKED,
                        CLIENT.RIPTIDE.KICKED_DESC
                    );

                case DisconnectReason.ServerStopped:
                    return (
                        CLIENT.RIPTIDE.SERVER_CLOSED,
                        CLIENT.RIPTIDE.SERVER_CLOSED_DESC
                    );

                case DisconnectReason.PoorConnection:
                    return (
                        CLIENT.RIPTIDE.CONNECTION_UNSTABLE,
                        CLIENT.RIPTIDE.CONNECTION_UNSTABLE_DESC
                    );

                case DisconnectReason.Disconnected:
                    return ("", ""); // client initiated

                default:
                    return (
                        CLIENT.RIPTIDE.UNKNOWN,
                        CLIENT.RIPTIDE.UNKNOWN_DESC
                    );
            }
        }

        public override int GetPing()
        {
            using var _ = Profiler.Scope();

            if (_client == null || !_client.IsConnected)
                return -1;

            return _client.SmoothRTT;
        }
    }
}
