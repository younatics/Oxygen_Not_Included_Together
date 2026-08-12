using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.DebugTools
{
	/// <summary>
	/// Says a repeated thing once, with a count, instead of every time.
	///
	/// A standing disagreement produces one warning per packet, and packets
	/// arrive many times a second forever. One live client logged the same
	/// missing entity 5881 times, and 26 distinct ids between them accounted for
	/// 27909 lines of a 12.7 MB log - which is not just noise. It buries the
	/// next finding, and in a DEBUG build every one of those lines is string
	/// formatting done on the network path.
	///
	/// The count is the part worth keeping. "x5881" says a retry loop; "x2" says
	/// a race. A single line says neither.
	/// </summary>
	public static class ThrottledLog
	{
		private const float FlushSeconds = 10f;

		private static readonly Dictionary<string, int> _pending = new Dictionary<string, int>();
		private static readonly Dictionary<string, int> _pendingInfo = new Dictionary<string, int>();
		private static float _lastFlush;

		/// <summary>
		/// Record one occurrence of <paramref name="key"/>. Every distinct key is
		/// reported at most once per flush window, carrying however many times it
		/// happened.
		/// </summary>
		public static void Warn(string key) => Record(key, warn: true);

		/// <summary>
		/// The same collapsing for something that is not a fault. Used where a
		/// line is worth having but arrives at packet rate - a path applied to a
		/// duplicant, say, which on a busy colony is several a second per
		/// duplicant and buries everything else while costing string formatting
		/// on the network path.
		///
		/// Pass a constant key. A key built from the id and the step count is a
		/// distinct key every time, which collapses nothing and grows the table.
		/// </summary>
		public static void Info(string key) => Record(key, warn: false);

		private static void Record(string key, bool warn)
		{
			var table = warn ? _pending : _pendingInfo;
			table.TryGetValue(key, out int n);
			table[key] = n + 1;

			float now = Time.unscaledTime;
			if (_lastFlush == 0f)
			{
				_lastFlush = now;
				return;
			}
			if (now - _lastFlush < FlushSeconds)
				return;

			_lastFlush = now;
			foreach (var kvp in _pending)
			{
				DebugConsole.LogWarning(kvp.Value > 1
					? $"{kvp.Key} (x{kvp.Value} in the last {FlushSeconds:0}s)"
					: kvp.Key);
			}
			_pending.Clear();

			foreach (var kvp in _pendingInfo)
			{
				DebugConsole.Log(kvp.Value > 1
					? $"{kvp.Key} (x{kvp.Value} in the last {FlushSeconds:0}s)"
					: kvp.Key);
			}
			_pendingInfo.Clear();
		}

		/// <summary>Dropped on session teardown so counts never span two sessions.</summary>
		public static void Reset()
		{
			_pending.Clear();
			_pendingInfo.Clear();
			_lastFlush = 0f;
		}
	}
}
