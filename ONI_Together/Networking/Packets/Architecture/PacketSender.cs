using Epic.OnlineServices.P2P;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.States;
using ONI_Together.Networking.Transport;
using ONI_Together.Networking.Transport.Steam;
using Shared.Interfaces.Networking;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Shared.Profiling;
using UnityEngine;
using ONI_Together.Networking.Components;

namespace ONI_Together.Networking
{
	public static class PacketSender
	{
		private class PacketUpdateRunner
		{
			private readonly float _updateIntervalS;
			private readonly Dictionary<object, float> _lastDispatchTime = [];

			public PacketUpdateRunner(int packetId, uint updateInterval)
			{
				_updateIntervalS = updateInterval / 1000f;
			}

			public bool CanDispatchNext(object connection)
			{
				using var _ = Profiler.Scope();

				if (!_lastDispatchTime.TryGetValue(connection, out var lastDispatchTime))
					return true;

				return Time.unscaledTime - lastDispatchTime >= _updateIntervalS;
			}

			public void RecordDispatch(object connection)
			{
				using var _ = Profiler.Scope();

				_lastDispatchTime[connection] = Time.unscaledTime;
			}
		}

		// Kilobytes
        public static float MAX_PACKET_SIZE_LAN = 0.5f; // 512 bytes (is multipled by 1024)
        public static int MAX_PACKET_SIZE_RELIABLE = 512;
		public static int MAX_PACKET_SIZE_UNRELIABLE = 1024;

		/// <summary>
		/// What SerializePacketForSending prepends: packet type + sequence.
		///
		/// Batch caps subtract this, so it has to be derived rather than
		/// restated. Adding the sequence without updating the callers pushed the
		/// conduit batch from 998 B back over the 1000 B limit - reintroducing
		/// the fragmentation this session had already fixed once.
		/// </summary>
		public const int FramingBytes = 8;

		private static int _sequence;

		/// <summary>Next outgoing sequence number. Wraps; comparisons use the difference.</summary>
		public static int NextSequence() => ++_sequence;

		/// <summary>Reset on session teardown so a new session does not inherit a high water mark.</summary>
		public static void ResetSequence() => _sequence = 0;

		/// <summary>
		/// The bulk queues are keyed by connection object. A peer that goes away
		/// with packets still queued leaves its serialized payloads rooted for
		/// the life of the process, and the sequence counter - which handlers use
		/// to refuse messages older than what they already applied - carried a
		/// high-water mark into a fresh session where the other side starts at
		/// zero, making every comparison between them meaningless.
		/// </summary>
		public static void ResetForNewSession()
		{
			ResetSequence();
			UpdateRunners.Clear();
			WaitingBulkPacketsPerReceiver.Clear();
			WaitingBulkPacketBytes.Clear();
			DragToolBulkPacketIds.Clear();
			_undeliverable.Clear();
			_undeliverableFlushTime = 0f;
		}

        public static byte[] SerializePacketForSending(IPacket packet)
		{
			using var _ = Profiler.Scope();

			using (var ms = new System.IO.MemoryStream())
			using (var writer = new System.IO.BinaryWriter(ms))
			{
				int packet_type = PacketRegistry.GetPacketId(packet);
				writer.Write(packet_type);

				// Monotonic per sender. The header used to be a bare packet type,
				// so a receiver had no way to tell an older message from a newer
				// one - which is why naming an object could be applied out of
				// order and why every attempt to send spawn announcements faster
				// broke id agreement. See PacketHandler.CurrentSequence.
				writer.Write(NextSequence());
				packet.Serialize(writer);
				return ms.ToArray();
			}
		}

		static Dictionary<int, PacketUpdateRunner> UpdateRunners = [];
		static Dictionary<object, Dictionary<int, List<byte[]>>> WaitingBulkPacketsPerReceiver = [];
		// Running byte total per (receiver, packetId) so LAN capacity checks stay O(1) per append.
		static Dictionary<object, Dictionary<int, int>> WaitingBulkPacketBytes = [];
		// Packet ids that belong to DragToolPacket subclasses — tagged lazily on first append
		// so the bulk flush site can record SyncStats.DragTool without needing the typed instance.
		static HashSet<int> DragToolBulkPacketIds = new HashSet<int>();
		public static void DispatchPendingBulkPackets()
		{
			using var _ = Profiler.Scope();

			var emptyConnections = new List<object>();
			foreach (var kvp in WaitingBulkPacketsPerReceiver)
			{
				var conn = kvp.Key;
				foreach (var packetId in kvp.Value.Keys.ToList())
				{
					DispatchPendingBulkPacketOfType(conn, packetId, true);
				}

				if (kvp.Value.Count == 0)
					emptyConnections.Add(conn);
			}

			foreach (var conn in emptyConnections)
			{
				WaitingBulkPacketsPerReceiver.Remove(conn);
				WaitingBulkPacketBytes.Remove(conn);
			}
		}

		static void DispatchPendingBulkPacketOfType(object conn, int packetId, bool intervalRun = false)
		{
			using var _ = Profiler.Scope();

			if (!WaitingBulkPacketsPerReceiver.TryGetValue(conn, out var allPendingPackets)
				|| !allPendingPackets.TryGetValue(packetId, out var pendingPackets)
				|| !pendingPackets.Any())
			{
				return;
			}
			if (intervalRun && UpdateRunners.TryGetValue(packetId, out var intervalRunner) && !intervalRunner.CanDispatchNext(conn))
				return;

			int flushCount = pendingPackets.Count;
			int flushBytes = 0;
			WaitingBulkPacketBytes.TryGetValue(conn, out var byteTotals);
			if (byteTotals != null && byteTotals.TryGetValue(packetId, out var bt))
				flushBytes = bt;
			var swFlush = System.Diagnostics.Stopwatch.StartNew();
			SendToConnection(conn, new BulkSenderPacket(packetId, pendingPackets), PacketSendMode.ReliableImmediate);
			swFlush.Stop();
			pendingPackets.Clear();
			allPendingPackets.Remove(packetId);
			if (byteTotals != null)
			{
				byteTotals[packetId] = 0;
				byteTotals.Remove(packetId);
			}
			if (UpdateRunners.TryGetValue(packetId, out var runner))
				runner.RecordDispatch(conn);
			if (DragToolBulkPacketIds.Contains(packetId))
				SyncStats.RecordSync(SyncStats.DragTool, flushCount, flushBytes, (float)swFlush.Elapsed.TotalMilliseconds);
		}
		public static void AppendPendingBulkPacket(object conn, IPacket packet, IBulkablePacket bp)
		{
			using var _ = Profiler.Scope();

			int packetId = PacketRegistry.GetPacketId(packet);
			int maxPacketNumberPerPacket = bp.MaxPackSize;

			if (packet is ONI_Together.Networking.Packets.Tools.DragToolPacket)
				DragToolBulkPacketIds.Add(packetId);

			if (!UpdateRunners.ContainsKey(packetId))
			{
				UpdateRunners[packetId] = new PacketUpdateRunner(packetId, bp.IntervalMs);
			}

			if (!WaitingBulkPacketsPerReceiver.TryGetValue(conn, out var bulkPacketWaitingData))
			{
				WaitingBulkPacketsPerReceiver[conn] = [];
				bulkPacketWaitingData = WaitingBulkPacketsPerReceiver[conn];
			}
			if (!bulkPacketWaitingData.TryGetValue(packetId, out var pendingPackets))
			{
				bulkPacketWaitingData[packetId] = new List<byte[]>(maxPacketNumberPerPacket);
				pendingPackets = bulkPacketWaitingData[packetId];
			}
			var serialized = packet.SerializeToByteArray();
			pendingPackets.Add(serialized);

			if (!WaitingBulkPacketBytes.TryGetValue(conn, out var byteTotals))
			{
				byteTotals = [];
				WaitingBulkPacketBytes[conn] = byteTotals;
			}
			if (!byteTotals.TryGetValue(packetId, out var runningTotal))
				runningTotal = 4; // +4 for the packetId int header
			runningTotal += serialized.Length;
			byteTotals[packetId] = runningTotal;

			bool atCapacity = false;
			if (NetworkConfig.IsLanConfig())
			{
				float maxSize = MAX_PACKET_SIZE_LAN * 1024f;
				if (runningTotal >= maxSize)
				{
					atCapacity = true;
				}
			}

			if (pendingPackets.Count >= maxPacketNumberPerPacket || atCapacity)
			{
				DispatchPendingBulkPacketOfType(conn, packetId);
			}
		}
		public static byte[] SerializeToByteArray(this IPacket packet)
		{
			using var _ = Profiler.Scope();

			using var ms = new System.IO.MemoryStream();
			using var writer = new System.IO.BinaryWriter(ms);
			packet.Serialize(writer);
			return ms.ToArray();
		}

		/// <summary>
		/// Send to one connection by HSteamNetConnection handle.
		/// </summary>
		///

		public static bool SendToConnection(object conn, IPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate)
		{
			using var _ = Profiler.Scope();

			if (packet is IBulkablePacket bp)
			{
				AppendPendingBulkPacket(conn, packet, bp);
				return true;
			}

			return NetworkConfig.TransportPacketSender.SendToConnection(conn, packet, sendType);
		}

		/// <summary>
		/// Send a packet to a player by their SteamID.
		/// </summary>
		public static bool SendToPlayer(ulong steamID, IPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate)
		{
			using var _ = Profiler.Scope();

			// Prevent host from sending packets to itself (can cause loops and errors)
			if (MultiplayerSession.IsHost && steamID == MultiplayerSession.HostUserID)
			{
				DebugConsole.LogWarning($"[PacketSender] Host attempted to send packet {packet.GetType().Name} to itself - blocked");
				return false;
			}

			if (!MultiplayerSession.ConnectedPlayers.TryGetValue(steamID, out var player) || player.Connection == null)
			{
				// Named and counted rather than repeated. A live session produced
				// 5436 of these in 26 minutes, all for id 0, and the message said
				// nothing about which packet - so there was no way to tell which
				// of the request-reply paths was handing over a bad requester id,
				// and every reply on that path was being dropped in silence.
				ReportUndeliverable(steamID, packet.GetType().Name);
				return false;
			}

			// Also here, not only in the broadcast loops. The periodic syncers
			// that produced almost all of these failures send per-player, after
			// doing their own viewport culling - WorkableProgressPacket alone
			// accounted for 826 of a client's 904 failed lookups - so a gate that
			// only covered SendToAll would have missed the traffic it was for.
			if (NotReadyFor(player, packet))
				return false;

			return SendToConnection(player.Connection, packet, sendType);
		}

		/// <summary>
		/// Undeliverable sends, grouped by (recipient, packet) and summarised
		/// every 10 s. The count matters as much as the name: a path that drops
		/// one reply is a race, and a path that drops eight a second is broken.
		/// </summary>
		private static readonly Dictionary<string, int> _undeliverable = new Dictionary<string, int>();
		private static float _undeliverableFlushTime;

		private static void ReportUndeliverable(ulong recipient, string packetName)
		{
			string key = recipient + "/" + packetName;
			_undeliverable.TryGetValue(key, out int n);
			_undeliverable[key] = n + 1;

			float now = UnityEngine.Time.unscaledTime;
			if (_undeliverableFlushTime == 0f) { _undeliverableFlushTime = now; return; }
			if (now - _undeliverableFlushTime < 10f) return;
			_undeliverableFlushTime = now;

			foreach (var kvp in _undeliverable)
			{
				int slash = kvp.Key.IndexOf('/');
				DebugConsole.LogWarning(
					$"[PacketSender] undeliverable x{kvp.Value}: {kvp.Key.Substring(slash + 1)} " +
					$"-> player {kvp.Key.Substring(0, slash)} (not a connected player)");
			}
			_undeliverable.Clear();
		}

		/// <summary>
		/// True when this packet cannot be applied by <paramref name="player"/> yet
		/// because that peer is still loading its world.
		///
		/// Fails open, deliberately. A client that reports Loading and never
		/// reports Ready would otherwise be cut off from world state for the rest
		/// of the session, which is far worse than the warnings this avoids - so
		/// after the host's own timeout the gate lifts on its own.
		/// </summary>
		private static bool NotReadyFor(MultiplayerPlayer player, IPacket packet)
		{
			if (!(packet is IRequiresLoadedWorld)) return false;
			if (player.readyState != ClientReadyState.Loading) return false;

			float waited = Time.unscaledTime - player.LoadingSince;
			if (waited > Configuration.Instance.Host.TimeoutSeconds)
			{
				// Treat it as ready rather than starve it forever, and say so
				// once - a client stuck in Loading is its own bug and should not
				// be hidden by this one silently working around it.
				DebugConsole.LogWarning(
					$"[PacketSender] player {player.PlayerId} has been Loading for {waited:0}s - " +
					"resuming world traffic to it anyway");
				player.readyState = ClientReadyState.Ready;
				return false;
			}

			return true;
		}

		private static bool CanBroadcastTo(MultiplayerPlayer player)
		{
			using var _ = Profiler.Scope();

			if (player == null || player.Connection == null)
			{
				return false;
			}

			if (!MultiplayerSession.IsHost || player.PlayerId == MultiplayerSession.HostUserID)
			{
				return true;
			}

			return player.ProtocolVerified;
		}

		public static void SendToHost(IPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.HostUserID.IsValid())
			{
				DebugConsole.LogWarning($"[PacketSender] Failed to send to host. Host is invalid.");
				return;
			}
			SendToPlayer(MultiplayerSession.HostUserID, packet, sendType);
		}

		// Throttle counter for per-connection send failures so a transport storm
		// does not flood the log. First 5 errors are logged in full, then 1/100 after.
		private static long _sendErrorCount;

		private static void TrySendToConnection(MultiplayerPlayer player, IPacket packet, PacketSendMode sendType)
		{
			try
			{
				SendToConnection(player.Connection, packet, sendType);
			}
			catch (Exception ex)
			{
				// A throw from the transport layer (e.g. Riptide pendingMessages key collision)
				// must not skip remaining connections in the broadcast. Log-and-continue.
				long n = ++_sendErrorCount;
				if (n <= 5 || n % 100 == 0)
				{
					DebugConsole.LogError($"[PacketSender] Send to player {player.PlayerId} failed (packet={packet.GetType().Name}, #{n}): {ex}");
				}
			}
		}

		/// <summary>
		/// True when this packet asks to be culled AND the world can answer the
		/// question. A negative cell is the interface's opt-out; see
		/// <see cref="IViewportCullable"/>.
		/// </summary>
		private static bool WantsCulling(IPacket packet, out int cell)
		{
			cell = -1;
			if (WorldStateSyncer.Instance == null) return false;
			if (!(packet is IViewportCullable vp)) return false;
			cell = vp.GetViewportCell();
			return cell >= 0;
		}

		/// Original single-exclude overload.
		/// <returns>How many peers it actually went to.</returns>
		public static int SendToAll(IPacket packet, ulong? exclude = null, PacketSendMode sendType = PacketSendMode.Reliable)
		{
			using var _ = Profiler.Scope();

            // Only send this packet if its being observed by a someone
            bool cull = WantsCulling(packet, out int cell);
            int sent = 0;

            foreach (var player in MultiplayerSession.ConnectedPlayers.Values)
			{
				if (exclude.HasValue && player.PlayerId == exclude.Value)
					continue;

				if (!CanBroadcastTo(player))
					continue;

				if (NotReadyFor(player, packet))
					continue;

				if (cull && !WorldStateSyncer.Instance.IsCellInPlayerViewport(player.PlayerId, cell))
					continue;

				TrySendToConnection(player, packet, sendType);
				sent++;
			}

			return sent;
		}

		/// <returns>How many clients it actually went to.</returns>
		public static int SendToAllClients(IPacket packet, PacketSendMode sendType = PacketSendMode.Reliable)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.IsHost)
			{
				DebugConsole.LogWarning("[PacketSender] Only the host can send to all clients. Tried sending: " + packet.GetType());
				return 0;
			}
			return SendToAll(packet, MultiplayerSession.HostUserID, sendType);
		}

		/// <returns>How many peers it actually went to.</returns>
		public static int SendToAllExcluding(IPacket packet, HashSet<ulong> excludedIds, PacketSendMode sendType = PacketSendMode.Reliable)
		{
			using var _ = Profiler.Scope();

            bool cull = WantsCulling(packet, out int cell);
            int sent = 0;

            foreach (var player in MultiplayerSession.ConnectedPlayers.Values)
			{
				if (excludedIds != null && excludedIds.Contains(player.PlayerId))
					continue;

				if (!CanBroadcastTo(player))
					continue;

				if (NotReadyFor(player, packet))
					continue;

				if (cull && !WorldStateSyncer.Instance.IsCellInPlayerViewport(player.PlayerId, cell))
					continue;

				TrySendToConnection(player, packet, sendType);
				sent++;
			}

			return sent;
		}

		/// <summary>
		/// Sends a packet to all other players.
		/// Forces the packet origin to be on the host itself
		/// if sent from the host, it goes to all clients.
		/// otherwise it is wrapped in a HostBroadcastPacket and sent to the host for rebroadcasting.
		///
		/// </summary>
		/// <param name="packet"></param>
		public static void SendToAllOtherPeersFromHost(IPacket packet)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession)
			{
				DebugConsole.LogWarning("[PacketSender] Not in a multiplayer session, cannot send to other peers");
				return;
			}
			//DebugConsole.Log("[PacketSender] Sending packet to all other peers: " + packet.GetType().Name + " from host");

			if (MultiplayerSession.IsHost)
				SendToAllClients(packet);
			else
				SendToHost(new HostBroadcastPacket(packet, Utils.NilUlong()));
		}

		public static void SendToAllOtherPeersFromHost_API(object api_packet)
		{
			using var _ = Profiler.Scope();

			var type = api_packet.GetType();
			if (!PacketRegistry.HasRegisteredPacket(type))
			{
				DebugConsole.LogError($"[PacketSender] Attempted to send unregistered packet type: {type.Name}");
				return;
			}
			if (!API_Helper.WrapApiPacket(api_packet, out var packet))
			{
				DebugConsole.LogError($"[PacketSender] Failed to wrap API packet of type: {type.Name}");
				return;
			}
			SendToAllOtherPeersFromHost(packet);
		}


		/// <summary>
		/// Sends a packet to all other players.
		/// if sent from the host, it goes to all clients.
		/// otherwise it is wrapped in a HostBroadcastPacket and sent to the host for rebroadcasting.
		/// </summary>
		/// <param name="packet"></param>
		public static void SendToAllOtherPeers(IPacket packet)
		{
			using var _ = Profiler.Scope();

			if (!MultiplayerSession.InSession)
			{
				DebugConsole.LogWarning("[PacketSender] Not in a multiplayer session, cannot send to other peers");
				return;
			}
			//DebugConsole.Log("[PacketSender] Sending packet to all other peers: " + packet.GetType().Name);

			if (MultiplayerSession.IsHost)
				SendToAllClients(packet);
			else if (packet is IBulkablePacket && packet is not IClientRelayable)
				SendToHost(packet);
			else
				SendToHost(new HostBroadcastPacket(packet, MultiplayerSession.LocalUserID));
		}

		public static void SendToAllOtherPeers_API(object api_packet)
		{
			using var _ = Profiler.Scope();

			var type = api_packet.GetType();
			if (!PacketRegistry.HasRegisteredPacket(type))
			{
				DebugConsole.LogError($"[PacketSender] Attempted to send unregistered packet type: {type.Name}");
				return;
			}
			if (!API_Helper.WrapApiPacket(api_packet, out var packet))
			{
				DebugConsole.LogError($"[PacketSender] Failed to wrap API packet of type: {type.Name}");
				return;
			}
			SendToAllOtherPeers(packet);
		}

		/// <summary>
		/// custom types, interfaces and enums are not directly usable across assembly boundaries
		/// </summary>
		/// <param name="api_packet">data object of the packet class that got registered with a ModApiPacket wrapper earlier</param>
		/// <param name="exclude"></param>
		/// <param name="sendType"></param>
		public static void SendToAll_API(object api_packet, ulong? exclude = null, int sendType = (int)PacketSendMode.Reliable)
		{
			using var _ = Profiler.Scope();

			var type = api_packet.GetType();
			if (!PacketRegistry.HasRegisteredPacket(type))
			{
				DebugConsole.LogError($"[PacketSender] Attempted to send unregistered packet type: {type.Name}");
				return;
			}
			if (!API_Helper.WrapApiPacket(api_packet, out var packet))
			{
				DebugConsole.LogError($"[PacketSender] Failed to wrap API packet of type: {type.Name}");
				return;
			}
			SendToAll(packet, exclude, (PacketSendMode)sendType);
		}

		public static void SendToAllClients_API(object api_packet, int sendType = (int)PacketSendMode.Reliable)
		{
			using var _ = Profiler.Scope();

			var type = api_packet.GetType();
			if (!PacketRegistry.HasRegisteredPacket(type))
			{
				DebugConsole.LogError($"[PacketSender] Attempted to send unregistered packet type: {type.Name}");
				return;
			}

			if (!API_Helper.WrapApiPacket(api_packet, out var packet))
			{
				DebugConsole.LogError($"[PacketSender] Failed to wrap API packet of type: {type.Name}");
				return;
			}
			SendToAllClients(packet, (PacketSendMode)sendType);
		}

		public static void SendToAllExcluding_API(object api_packet, HashSet<ulong> excludedIds, int sendType = (int)PacketSendMode.Reliable)
		{
			using var _ = Profiler.Scope();

			var type = api_packet.GetType();
			if (!PacketRegistry.HasRegisteredPacket(type))
			{
				DebugConsole.LogError($"[PacketSender] Attempted to send unregistered packet type: {type.Name}");
				return;
			}

			if (!API_Helper.WrapApiPacket(api_packet, out var packet))
			{
				DebugConsole.LogError($"[PacketSender] Failed to wrap API packet of type: {type.Name}");
				return;
			}
			SendToAllExcluding(packet, excludedIds, (PacketSendMode)sendType);
		}

		public static void SendToPlayer_API(ulong steamID, object api_packet, int sendType = (int)PacketSendMode.ReliableImmediate)
		{
			using var _ = Profiler.Scope();

			var type = api_packet.GetType();
			if (!PacketRegistry.HasRegisteredPacket(type))
			{
				DebugConsole.LogError($"[PacketSender] Attempted to send unregistered packet type: {type.Name}");
				return;
			}

			if (!API_Helper.WrapApiPacket(api_packet, out var packet))
			{
				DebugConsole.LogError($"[PacketSender] Failed to wrap API packet of type: {type.Name}");
				return;
			}
			SendToPlayer(steamID, packet, (PacketSendMode)sendType);
		}

		public static void SendToHost_API(object api_packet, int sendType = (int)PacketSendMode.ReliableImmediate)
		{
			using var _ = Profiler.Scope();

			var type = api_packet.GetType();
			if (!PacketRegistry.HasRegisteredPacket(type))
			{
				DebugConsole.LogError($"[PacketSender] Attempted to send unregistered packet type: {type.Name}");
				return;
			}

			if (!API_Helper.WrapApiPacket(api_packet, out var packet))
			{
				DebugConsole.LogError($"[PacketSender] Failed to wrap API packet of type: {type.Name}");
				return;
			}
			SendToHost(packet, (PacketSendMode)sendType);
		}

	}
}
