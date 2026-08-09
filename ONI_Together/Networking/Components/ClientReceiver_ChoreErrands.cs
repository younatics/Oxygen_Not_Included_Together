using ONI_Together.Networking.Packets.Chores;
using Shared.Profiling;
using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	public class ClientReceiver_ChoreErrands : KMonoBehaviour
	{
		[MyCmpGet] private NetworkIdentity identity;

		public ErrandEntry? Current { get; private set; }
		public List<ErrandEntry> Upcoming { get; private set; } = new();

		public float LastApplyTime { get; private set; }
		public float SecondsUntilRefresh => Mathf.Max(0, 0.2f - (Time.time - LastApplyTime)); // 0.2 because they broadcast every 200ms
		
		/// <summary>
		/// One per duplicant. Apply replaces the whole errand list, so a batch
		/// applied alone would drop the errands carried by the rest of its sweep.
		/// </summary>
		private readonly SweepAssembler<ErrandEntry> _assembler = new SweepAssembler<ErrandEntry>("ChoreErrands");

		public void AcceptBatch(int sweepId, int batchIndex, int batchCount, List<ErrandEntry> batch)
		{
			using var _ = Profiler.Scope();

			if (_assembler.Accept(sweepId, batchIndex, batchCount, batch, out var complete))
				Apply(complete);
		}

		public void Apply(List<ErrandEntry> entries)
		{
			using var _ = Profiler.Scope();
			LastApplyTime = Time.time;
			
			Current = null;
			if (entries == null)
			{
				Upcoming = new List<ErrandEntry>();
				return;
			}
			var upcoming = new List<ErrandEntry>(entries.Count);
			for (int i = 0; i < entries.Count; i++)
			{
				var entry = entries[i];
				if (entry.IsCurrent && !Current.HasValue)
					Current = entry;
				else
					upcoming.Add(entry);
			}
			Upcoming = upcoming;
		}
	}
}
