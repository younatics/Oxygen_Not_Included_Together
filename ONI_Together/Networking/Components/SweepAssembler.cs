using System.Collections.Generic;
using ONI_Together.DebugTools;

namespace ONI_Together.Networking.Components
{
	/// <summary>
	/// Reassembles a full-state sweep that was sent as several packets.
	///
	/// The periodic syncers send a snapshot and the receivers reconcile by
	/// absence - anything local that is not in the snapshot is deleted. That
	/// makes a snapshot indivisible in a way a chunked byte stream is not: cut
	/// it into five packets and each one deletes the entries belonging to the
	/// other four. An earlier attempt at batching did exactly that and had to be
	/// reverted.
	///
	/// So batches carry a sweep id and an index, and nothing is applied until
	/// every batch of that sweep has arrived. A sweep that never completes is
	/// dropped rather than applied in part, which keeps the unreliable design
	/// the syncers were built on: a lost batch costs one sweep, and the next
	/// sweep repairs it. Applying a partial sweep would delete live objects.
	/// </summary>
	public class SweepAssembler<T>
	{
		private readonly string _name;
		private int _sweepId = -1;
		private int _batchCount;
		private readonly HashSet<int> _received = new HashSet<int>();
		private readonly List<T> _entries = new List<T>();

		public SweepAssembler(string name) { _name = name; }

		/// <summary>
		/// Take one batch. Returns true and hands back the whole sweep only once
		/// every batch of it has arrived.
		/// </summary>
		public bool Accept(int sweepId, int batchIndex, int batchCount, List<T> batch, out List<T> complete)
		{
			complete = null;

			if (batchCount < 1 || batchIndex < 0 || batchIndex >= batchCount)
			{
				DebugConsole.LogWarning($"[{_name}] ignoring batch {batchIndex} of {batchCount}");
				return false;
			}

			// A single-batch sweep is the common case; do not pay for state.
			if (batchCount == 1)
			{
				Reset();
				complete = batch;
				return true;
			}

			if (sweepId != _sweepId)
			{
				// A newer sweep supersedes whatever was half-collected. The old
				// one can never complete, and holding it would only delay the
				// next good snapshot.
				if (_sweepId != -1 && _received.Count < _batchCount)
				{
					DebugConsole.LogWarning(
						$"[{_name}] sweep {_sweepId} dropped with {_received.Count}/{_batchCount} batches; " +
						"a batch was lost and a partial snapshot must not be applied");
				}
				Reset();
				_sweepId = sweepId;
				_batchCount = batchCount;
			}

			if (!_received.Add(batchIndex))
				return false;   // duplicate

			_entries.AddRange(batch);

			if (_received.Count < _batchCount)
				return false;

			complete = new List<T>(_entries);
			Reset();
			return true;
		}

		private void Reset()
		{
			_sweepId = -1;
			_batchCount = 0;
			_received.Clear();
			_entries.Clear();
		}
	}
}
