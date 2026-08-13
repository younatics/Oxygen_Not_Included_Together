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
		/// <summary>
		/// Largest payload each packet type has produced, and how often it went
		/// past what a transport carries in one piece.
		///
		/// A packet over the limit is not refused, it is fragmented - so the only
		/// symptom is a silent change of delivery path, and the project notes name
		/// that as the origin of several of the worst bugs here. Some syncers batch
		/// against the byte budget and some do not, and which is which was a
		/// question nobody could answer from the code alone: the batching lives in
		/// the sender, the sizes depend on the colony, and a comment claiming a
		/// 1200 byte MTU sat above a 1100 byte packet for a long time.
		///
		/// So the sizes are recorded where every packet becomes bytes, and the
		/// answer comes from a live session rather than from reading.
		/// </summary>
		private static readonly Dictionary<string, int> _largestPayload = new Dictionary<string, int>();
		private static readonly Dictionary<string, int> _oversizeCount = new Dictionary<string, int>();

		public static IReadOnlyDictionary<string, int> LargestPayloadByPacket => _largestPayload;
		public static IReadOnlyDictionary<string, int> OversizeCountByPacket => _oversizeCount;

		public static void ResetPayloadSizes()
		{
			_largestPayload.Clear();
			_oversizeCount.Clear();
		}

		/// <summary>The worst offenders, largest first, for a diagnostic line.</summary>
		public static string DescribePayloadSizes(int top = 5)
		{
			if (_largestPayload.Count == 0) return "nothing serialized yet";

			var worst = _largestPayload.OrderByDescending(kv => kv.Value).Take(top)
				.Select(kv =>
				{
					_oversizeCount.TryGetValue(kv.Key, out int over);
					return over > 0 ? $"{kv.Key}:{kv.Value}B(over x{over})" : $"{kv.Key}:{kv.Value}B";
				});
			return string.Join(" ", worst);
		}

		public static byte[] SerializeToByteArray(this IPacket packet)
		{
			using var _ = Profiler.Scope();

			using var ms = new System.IO.MemoryStream();
			using var writer = new System.IO.BinaryWriter(ms);
			packet.Serialize(writer);
			var bytes = ms.ToArray();

			string name = packet.GetType().Name;
			_largestPayload.TryGetValue(name, out int previous);
			if (bytes.Length > previous)
				_largestPayload[name] = bytes.Length;

			if (bytes.Length > Transport.TransportPacketSender.StrictestUnfragmentedPayloadBytes)
			{
				_oversizeCount.TryGetValue(name, out int over);
				_oversizeCount[name] = over + 1;

				// Named and counted rather than one line per send. A syncer that
				// oversizes does it every tick, and a line each would bury the
				// finding the way per-cell logging once froze a host.
				ThrottledLog.Warn(
					$"[PacketSender] {name} serialized to {bytes.Length} bytes, past the " +
					$"{Transport.TransportPacketSender.StrictestUnfragmentedPayloadBytes} a transport carries " +
					"in one piece - it will be fragmented");
			}

			return bytes;
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

			// Here too, not only in the broadcasts. The viewport-culled senders reach
			// clients through this path, and WorkableProgressPacket - one of the two
			// packets measured arriving with no id - is one of them.
			if (Unaddressed(packet)) return false;

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
		/// <summary>
		/// Packets dropped because the object they are about has no address, grouped by
		/// packet name.
		///
		/// Counted, not silent. Blocking a send does not remove a failure by itself - it
		/// usually moves it to an earlier counter, and this project has already read one
		/// such move as an improvement. The prediction this makes is checkable in one
		/// run: the client's "packets arrived carrying NetId 0" should fall to zero and
		/// this should rise by about the same amount. If it does not, the reasoning was
		/// wrong somewhere else.
		/// </summary>
		public static int SendsSkippedNoId { get; private set; }

		private static readonly Dictionary<string, int> _skippedNoIdByPacket =
			new Dictionary<string, int>();

		/// <summary>Which packets were about objects with no address, worst first.</summary>
		public static string SkippedNoIdBreakdown()
		{
			if (_skippedNoIdByPacket.Count == 0) return "none";
			var parts = new List<string>();
			foreach (var kv in _skippedNoIdByPacket.OrderByDescending(kv => kv.Value).Take(6))
				parts.Add($"{kv.Key}:{kv.Value}");
			return string.Join(" ", parts);
		}

		/// <summary>
		/// True when the packet is about one object and that object has no NetId.
		///
		/// See IAddressedPacket. The receiver cannot act on such a packet - it logs
		/// "Could not resolve workable 0" and drops it - so the only thing sending it
		/// buys is a warning at the far end and bandwidth spent during a hard sync.
		/// </summary>
		private static bool Unaddressed(IPacket packet)
		{
			if (!(packet is IAddressedPacket addressed) || addressed.IsAddressable)
				return false;

			SendsSkippedNoId++;
			string name = packet.GetType().Name;
			_skippedNoIdByPacket.TryGetValue(name, out int n);
			_skippedNoIdByPacket[name] = n + 1;
			return true;
		}

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

			// Before the loop, so one unaddressable send counts once rather than once
			// per recipient - a count that scales with player numbers cannot be
			// compared between runs.
			if (Unaddressed(packet)) return 0;

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

			// Counted once per send, as in SendToAll.
			if (Unaddressed(packet)) return 0;

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
