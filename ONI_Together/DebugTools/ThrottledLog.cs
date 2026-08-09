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
		private static float _lastFlush;

		/// <summary>
		/// Record one occurrence of <paramref name="key"/>. Every distinct key is
		/// reported at most once per flush window, carrying however many times it
		/// happened.
		/// </summary>
		public static void Warn(string key)
		{
			_pending.TryGetValue(key, out int n);
			_pending[key] = n + 1;

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
		}

		/// <summary>Dropped on session teardown so counts never span two sessions.</summary>
		public static void Reset()
		{
			_pending.Clear();
			_lastFlush = 0f;
		}
	}
}
