using System.Collections.Generic;
using ONI_Together.DebugTools;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// Removal notices that arrived before the object they refer to.
	///
	/// A client can be told an item was picked up or stored before it has been
	/// told the item exists, so the notice waits for the spawn to catch up. Both
	/// GroundItemPickedUpPacket and StorageItemPacket needed this and each kept
	/// its own unbounded HashSet.
	///
	/// Unbounded was the problem. One live session queued 4135 notices and
	/// consumed 8: spawns are culled to a client's viewport and removal notices
	/// are not, so the great majority name an object this peer was never told
	/// about and never will be. That is not only a leak - NetIds get reused, and
	/// a stale entry destroys the next legitimate object issued the same id, the
	/// instant it spawns.
	/// </summary>
	public class PendingRemovals
	{
		/// <summary>
		/// How long a notice can usefully wait. The window exists to absorb
		/// packet reordering, which is milliseconds; ten seconds is already well
		/// past the point where the spawn is simply never coming.
		/// </summary>
		private const float LifetimeSeconds = 10f;
		private const int MaxEntries = 2048;

		private readonly Dictionary<int, float> _queuedAt = new Dictionary<int, float>();
		private readonly List<int> _scratch = new List<int>();
		private readonly string _name;

		public PendingRemovals(string name) { _name = name; }

		public int Count => _queuedAt.Count;

		public void Queue(int netId)
		{
			float now = Time.unscaledTime;
			_queuedAt[netId] = now;

			if (_queuedAt.Count <= MaxEntries)
				return;

			_scratch.Clear();
			foreach (var kvp in _queuedAt)
			{
				if (now - kvp.Value > LifetimeSeconds)
					_scratch.Add(kvp.Key);
			}
			foreach (int id in _scratch)
				_queuedAt.Remove(id);

			// Still over after dropping everything expired means notices are
			// arriving faster than the window can retire them. Worth saying out
			// loud rather than growing quietly.
			if (_queuedAt.Count > MaxEntries)
			{
				DebugConsole.LogWarning(
					$"[{_name}] {_queuedAt.Count} removals waiting for spawns that are not coming");
				_queuedAt.Clear();
			}
		}

		/// <summary>
		/// True only if this id was queued recently. An expired entry is dropped
		/// rather than honoured: past the window it does not refer to this
		/// object, it refers to a dead one that happened to share its id.
		/// </summary>
		public bool TryConsume(int netId)
		{
			if (!_queuedAt.TryGetValue(netId, out float queuedAt))
				return false;

			_queuedAt.Remove(netId);
			return Time.unscaledTime - queuedAt <= LifetimeSeconds;
		}

		public void Clear()
		{
			int n = _queuedAt.Count;
			_queuedAt.Clear();
			if (n > 0)
				DebugConsole.Log($"[{_name}] cleared count={n}");
		}
	}
}
