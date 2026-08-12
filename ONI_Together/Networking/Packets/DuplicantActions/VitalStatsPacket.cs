using Klei.AI;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;
using UnityEngine;
using static STRINGS.UI.OUTFITS;

namespace ONI_Together.Networking.Packets.DuplicantActions
{
	// Host -> Client only. Vitals are simulated on Host.
	public class VitalStatsPacket : IPacket
	{
		Dictionary<string, float> VitalAmounts = [];
		public byte TargetDiseaseIdx;
		public int TargetDiseaseCount;
		public int NetId;

		public VitalStatsPacket() { }
		public VitalStatsPacket(int netId, Amounts amounts, PrimaryElement element)
		{
			using var _ = Profiler.Scope();

			NetId = netId;
			TargetDiseaseIdx = element.DiseaseIdx;
			TargetDiseaseCount = element.DiseaseCount;
            //	DebugConsole.Log("[VitalStatsPacket] Vital stat packet for " + element.GetProperName());
            foreach (var amountInstance in amounts.ModifierList)
            {
                VitalAmounts[amountInstance.amount.Id] = amountInstance.value;
            }

        }

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(NetId);
			writer.Write(TargetDiseaseIdx);
			writer.Write(TargetDiseaseCount);
			writer.Write(VitalAmounts.Count);
			foreach (var kvp in VitalAmounts)
			{
				//DebugConsole.Log("[VitalStatsPacket] Vital amount: " + kvp.Key+": "+kvp.Value);
				writer.Write(kvp.Key);
				writer.Write(kvp.Value);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			NetId = reader.ReadInt32();
			TargetDiseaseIdx = reader.ReadByte();
			TargetDiseaseCount = reader.ReadInt32();
			int amountsCount = reader.ReadInt32();
			VitalAmounts = new Dictionary<string, float>(amountsCount);
			for (int i = 0; i < amountsCount; i++)
			{
				string key = reader.ReadString();
				float value = reader.ReadSingle();
				VitalAmounts[key] = value;
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			// Only Clients apply this
			if (MultiplayerSession.IsHost) return;
			Apply();
		}

		private void Apply()
		{
			using var _ = Profiler.Scope();

			if (!NetworkIdentityRegistry.TryGet(NetId, out var identity))
			{
				DebugConsole.LogWarning("[VitalStatsPacket] Could not find minion with netid " + NetId);
				return;
			}

			var amounts = identity.GetAmounts();
			if (amounts == null)
			{
				DebugConsole.LogWarning("[VitalStatsPacket] Could not find amounts for minion " + identity.GetProperName());
				return;
			}

			// Amounts.SetValue looks the amount up and writes through the result
			// without checking it, so an amount this object does not have is a
			// NullReferenceException out of the packet handler rather than a
			// skipped field. A live client threw it 39 times in the twenty seconds
			// before the game closed: a duplicant's vitals kept arriving for
			// something that has no Calories.
			//
			// The cause is upstream - the id resolved to the wrong object - and
			// guarding here does not fix that. It stops one peer's bad address from
			// becoming an exception storm on the other, and it names what was hit.
			if (!identity.gameObject.HasTag(GameTags.BaseMinion))
			{
				DebugTools.ThrottledLog.Warn(
					$"[VitalStatsPacket] NetId {NetId} resolved to " +
					$"'{identity.gameObject.PrefabID()}', which is not a duplicant - " +
					"vitals are only sent for duplicants, so this id means something different here");
				return;
			}

			foreach (var kvp in VitalAmounts)
			{
				if (amounts.Get(kvp.Key) == null)
				{
					DebugTools.ThrottledLog.Warn(
						$"[VitalStatsPacket] '{identity.gameObject.PrefabID()}' has no amount " +
						$"'{kvp.Key}'; skipping it rather than throwing");
					continue;
				}
				amounts.SetValue(kvp.Key, kvp.Value);
			}
			if (identity.TryGetComponent<PrimaryElement>(out var element))
			{
				int currentDiseaseCount = element.DiseaseCount;
				int currentDiseaseIdx = element.DiseaseIdx;
				if (currentDiseaseIdx != TargetDiseaseIdx)
				{
					element.AddDisease(TargetDiseaseIdx, TargetDiseaseCount, "MP-Mod.SyncedDisease");
				}
				else if (!Mathf.Approximately(currentDiseaseCount, TargetDiseaseCount))
					element.ModifyDiseaseCount(TargetDiseaseCount - currentDiseaseCount, "MP-Mod.SyncedDisease");
			}
		}
	}
}
