using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using System;
using System.Collections.Generic;
using System.IO;

namespace ONI_Together.Networking.Packets.Chores
{
	public class ChoreErrandsPacket : IPacket
	{
		/// <summary>
		/// Sanity bound on a single batch, not the batching rule. Entries carry
		/// three strings each, so a count says nothing about the bytes; the byte
		/// budget the sender splits on is what actually limits a batch. This
		/// only stops a corrupt count from allocating.
		/// </summary>
		public const int MaxEntries = 32;

		public int DupeNetId;
		public List<ErrandEntry> Entries = new();

		/// <summary>
		/// Which sweep this batch belongs to, and where in it. The receiver
		/// replaces the duplicant's whole errand list, so applying one batch of
		/// three would drop the errands carried by the other two.
		/// </summary>
		public int SweepId;
		public int BatchIndex;
		public int BatchCount = 1;

		/// <summary>Net id, count, sweep header, and the sender's framing.</summary>
		public const int HeaderBytes = 20 + Networking.PacketSender.FramingBytes;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			writer.Write(DupeNetId);
			writer.Write(SweepId);
			writer.Write(BatchIndex);
			writer.Write(BatchCount);
			int count = Math.Min(Entries.Count, MaxEntries);
			writer.Write(count);
			for (int i = 0; i < count; i++)
				Entries[i].Serialize(writer);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			DupeNetId = reader.ReadInt32();
			SweepId = reader.ReadInt32();
			BatchIndex = reader.ReadInt32();
			BatchCount = reader.ReadInt32();
			int count = reader.ReadInt32();
			if (count < 0 || count > MaxEntries)
			{
				Entries = new List<ErrandEntry>();
				return;
			}
			Entries = new List<ErrandEntry>(count);
			for (int i = 0; i < count; i++)
				Entries.Add(ErrandEntry.Deserialize(reader));
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			if (!MultiplayerSession.IsClient) 
				return;
			if (!NetworkIdentityRegistry.TryGet(DupeNetId, out var entity)) 
				return;
			// Losing one batch loses the whole sweep: the assembler waits
			// for an index that will never arrive, so every other batch is
			// discarded with it and the duplicant's panel stops updating
			// for good. The receiver is a plain component with no ordering
			// requirement, so attach it rather than drop the sweep.
			var receiver = entity.gameObject.AddOrGet<ClientReceiver_ChoreErrands>();
			if (receiver == null)
			{
				ThrottledLog.Warn("[ClientReceiver_ChoreErrands] could not attach a receiver for " + entity.name);
				return;
			}
			
			receiver.AcceptBatch(SweepId, BatchIndex, BatchCount, Entries);
		}
	}
}
