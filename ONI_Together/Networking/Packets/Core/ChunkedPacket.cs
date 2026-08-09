using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Core
{
	internal class ChunkedPacket : IPacket
	{
		/// <summary>
		/// Who split this payload. Sets used to be keyed by SequenceId alone,
		/// and _nextSequenceId starts at 0 on every peer, so two clients sending
		/// to the host both produced sequence 0, 1, 2 ... and landed in the same
		/// buffer. One overwrote the other's chunk, the set then read as complete,
		/// and a payload spliced from two different messages went to
		/// PacketHandler as if it were one.
		/// </summary>
		public ulong SenderId;

		public int SequenceId;
		public int ChunkIndex;
		public int TotalChunks;
		public byte[] ChunkData;

		/// <summary>
		/// Ceiling on a wire-supplied chunk count. WorldDataPacket and
		/// InstantiationsPacket already bound their equivalents; this path never
		/// got the same guard, so a corrupt or hostile header sized an array for
		/// us. 8192 chunks is ~8 MB of payload, far past anything legitimate.
		/// </summary>
		internal const int MaxChunks = 65536;

		/// <summary>
		/// SenderId 8 + SequenceId 4 + ChunkIndex 4 + TotalChunks 4 + length 4,
		/// plus the 4-byte packet type the sender frames every packet with.
		/// </summary>
		internal const int HeaderOverheadBytes = 28;

		/// <summary>Bounded so a lost chunk cannot cost a buffer for the rest of the session.</summary>
		private const int MaxPendingSets = 32;

		private const float PendingTimeoutSeconds = 30f;

		private sealed class PendingSet
		{
			public byte[][] Chunks;
			public int Received;
			public float LastTouch;
		}

		private static readonly Dictionary<(ulong sender, int sequence), PendingSet> _pendingChunks = new();
		private static int _nextSequenceId = 0;

		public ChunkedPacket() { }

		public void Serialize(BinaryWriter writer)
		{
			writer.Write(SenderId);
			writer.Write(SequenceId);
			writer.Write(ChunkIndex);
			writer.Write(TotalChunks);
			writer.Write(ChunkData.Length);
			writer.Write(ChunkData);
		}

		public void Deserialize(BinaryReader reader)
		{
			SenderId = reader.ReadUInt64();
			SequenceId = reader.ReadInt32();
			ChunkIndex = reader.ReadInt32();
			TotalChunks = reader.ReadInt32();
			int len = reader.ReadInt32();
			ChunkData = len > 0 ? reader.ReadBytes(len) : System.Array.Empty<byte>();
		}

		public void OnDispatched()
		{
			// Everything below arrived over the wire, so none of it is trusted.
			if (TotalChunks < 1 || TotalChunks > MaxChunks)
			{
				DebugConsole.LogWarning($"[Chunked] refusing header claiming {TotalChunks} chunks from {SenderId}");
				return;
			}
			if (ChunkIndex < 0 || ChunkIndex >= TotalChunks)
			{
				DebugConsole.LogWarning($"[Chunked] refusing chunk {ChunkIndex} of {TotalChunks} from {SenderId}");
				return;
			}
			if (ChunkData == null)
				return;

			EvictStale();

			var key = (SenderId, SequenceId);
			if (!_pendingChunks.TryGetValue(key, out var set))
			{
				if (_pendingChunks.Count >= MaxPendingSets)
					DropOldest();

				set = new PendingSet { Chunks = new byte[TotalChunks][] };
				_pendingChunks[key] = set;
			}
			else if (set.Chunks.Length != TotalChunks)
			{
				// Same sender reusing a sequence with a different shape: the old
				// set can never complete, so replace it rather than index into it.
				DebugConsole.LogWarning(
					$"[Chunked] sequence {SequenceId} from {SenderId} reused with {TotalChunks} chunks " +
					$"(was {set.Chunks.Length}); discarding the incomplete set");
				set = new PendingSet { Chunks = new byte[TotalChunks][] };
				_pendingChunks[key] = set;
			}

			set.LastTouch = Time.unscaledTime;

			// A retransmit must not count twice.
			if (set.Chunks[ChunkIndex] == null)
				set.Received++;
			set.Chunks[ChunkIndex] = ChunkData;

			if (set.Received < TotalChunks)
				return;

			_pendingChunks.Remove(key);

			int totalSize = 0;
			foreach (var chunk in set.Chunks)
				totalSize += chunk.Length;

			byte[] fullData = new byte[totalSize];
			int offset = 0;
			foreach (var chunk in set.Chunks)
			{
				System.Array.Copy(chunk, 0, fullData, offset, chunk.Length);
				offset += chunk.Length;
			}

			PacketHandler.HandleIncoming(fullData);
		}

		private static void EvictStale()
		{
			if (_pendingChunks.Count == 0) return;

			float now = Time.unscaledTime;
			List<(ulong, int)> dead = null;
			foreach (var kvp in _pendingChunks)
			{
				if (now - kvp.Value.LastTouch > PendingTimeoutSeconds)
					(dead ??= new List<(ulong, int)>()).Add(kvp.Key);
			}
			if (dead == null) return;

			foreach (var key in dead)
				_pendingChunks.Remove(key);
			DebugConsole.LogWarning($"[Chunked] dropped {dead.Count} incomplete set(s) after {PendingTimeoutSeconds}s");
		}

		private static void DropOldest()
		{
			(ulong, int) oldestKey = default;
			float oldest = float.MaxValue;
			bool found = false;
			foreach (var kvp in _pendingChunks)
			{
				if (kvp.Value.LastTouch < oldest)
				{
					oldest = kvp.Value.LastTouch;
					oldestKey = kvp.Key;
					found = true;
				}
			}
			if (found)
			{
				_pendingChunks.Remove(oldestKey);
				DebugConsole.LogWarning($"[Chunked] pending set limit {MaxPendingSets} reached; dropped the oldest");
			}
		}

		/// <summary>
		/// Identifies this process as a chunk sender. Generated once at load
		/// rather than taken from the session, because chunks can be in flight
		/// before a session identity exists and must still be attributable. Two
		/// peers only need to differ, not to be recognisable.
		/// </summary>
		public static readonly ulong LocalSenderId = MakeSenderId();

		private static ulong MakeSenderId()
		{
			var bytes = System.Guid.NewGuid().ToByteArray();
			return System.BitConverter.ToUInt64(bytes, 0);
		}

		public static int GetNextSequenceId()
		{
			return _nextSequenceId++;
		}

		/// <summary>
		/// How many partially received sets are being held. Nothing else can see
		/// this, so a test cannot otherwise tell "still waiting for a chunk" from
		/// "gave up" from "spliced two senders together".
		/// </summary>
		internal static int PendingSetCount => _pendingChunks.Count;

		/// <summary>
		/// Drop all partial state. Used by tests to isolate cases, and on session
		/// teardown so a stale set cannot collide with a new session's sequences.
		/// </summary>
		internal static void ResetPending()
		{
			_pendingChunks.Clear();
		}
	}
}
