using System;
using System.Collections.Generic;
using ONI_Together.Networking.Transport;

namespace ONI_Together.Networking.Components
{
	/// <summary>
	/// Cuts a full-state snapshot into batches that each fit one indivisible
	/// payload.
	///
	/// The counterpart to <see cref="SweepAssembler{T}"/>: this splits, that
	/// rejoins. It exists as one function because the arithmetic is where the
	/// mistakes are, and every syncer that was getting it wrong was getting it
	/// wrong in its own way - a count cap instead of a byte budget, a header that
	/// forgot the sender's framing, an entry size that assumed a fixed width for
	/// a field that carries a string.
	///
	/// Closing on accumulated bytes rather than on a count is the whole point. A
	/// cap of "64 entries" is a guess about what an entry costs, and it is wrong
	/// for any packet that carries a name or a tooltip.
	/// </summary>
	public static class SweepBatcher
	{
		/// <summary>
		/// Split <paramref name="items"/> so that headerBytes plus the entries of
		/// each batch stay within the strictest transport's unfragmented payload.
		///
		/// Always returns at least one batch, even for an empty snapshot: an
		/// empty sweep is meaningful to a receiver that reconciles by absence,
		/// and dropping it would leave the last snapshot standing forever.
		/// </summary>
		public static List<List<T>> Split<T>(IEnumerable<T> items, int headerBytes, Func<T, int> entryBytes)
		{
			int limit = TransportPacketSender.StrictestUnfragmentedPayloadBytes;

			var batches = new List<List<T>>();
			var current = new List<T>();
			int bytes = headerBytes;

			foreach (var item in items)
			{
				int cost = entryBytes(item);

				// current.Count > 0 so a single entry too large to ever fit still
				// goes out alone rather than looping forever on an empty batch.
				// It will be fragmented by the transport, which is bad, but it is
				// the sender's problem to report, not a reason to drop the entry.
				if (current.Count > 0 && bytes + cost > limit)
				{
					batches.Add(current);
					current = new List<T>();
					bytes = headerBytes;
				}

				current.Add(item);
				bytes += cost;
			}

			batches.Add(current);
			return batches;
		}
	}
}
