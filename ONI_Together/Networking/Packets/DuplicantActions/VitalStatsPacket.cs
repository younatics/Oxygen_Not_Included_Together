using Klei.AI;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

		/// <summary>
		/// The prefab the sender sampled, so the receiver can tell that this id points at
		/// the same kind of thing on both peers. Zero from a peer that predates it.
		/// </summary>
		public int PrefabHash;

		/// <summary>
		/// Packets whose id resolved to a different prefab here than the sender sampled.
		/// Non-zero means the two peers disagree about what an id names, which no amount
		/// of resending fixes.
		/// </summary>
		public static int PrefabMismatched { get; private set; }

		/// <summary>
		/// Times a duplicant vital arrived above or below what this peer allows and was
		/// trimmed to fit. Non-zero means the peers disagree about a limit, which no
		/// amount of resending can fix.
		/// </summary>
		public static int AmountsClamped { get; private set; }

		private class Drift { public int Applies; public double Total; public float Worst; }
		private static readonly Dictionary<string, Drift> _drift = new Dictionary<string, Drift>();

		private static void NoteCorrection(string who, string amount, float delta)
		{
			if (string.IsNullOrEmpty(who) || string.IsNullOrEmpty(amount)) return;

			string key = who + "|" + amount;
			if (!_drift.TryGetValue(key, out var d)) _drift[key] = d = new Drift();

			d.Applies++;
			float size = delta < 0 ? -delta : delta;
			d.Total += size;
			if (size > d.Worst) d.Worst = size;
		}

		/// <summary>
		/// Per duplicant: how many corrections landed and how big the average one was.
		/// A peer that is behind by a fixed lag shows the same average as everyone else
		/// multiplied by how fast that duplicant is burning; a peer that is missing
		/// packets shows fewer applies.
		/// </summary>
		/// <summary>
		/// The average correction for one duplicant's one amount, or 0 if this peer has
		/// not measured it. That is one sync period of drift, which is the bound a
		/// snapshot comparison can reasonably ask for.
		/// </summary>
		public static float AverageCorrection(string who, string amount)
		{
			if (!_drift.TryGetValue(who + "|" + amount, out var d) || d.Applies == 0) return 0f;
			return (float)(d.Total / d.Applies);
		}

		public static string DriftBreakdown()
		{
			if (_drift.Count == 0) return "none";
			var parts = new List<string>();
			foreach (var kv in _drift.OrderByDescending(kv => kv.Value.Total / (kv.Value.Applies == 0 ? 1 : kv.Value.Applies)).Take(4))
			{
				int n = kv.Value.Applies;
				double avg = n == 0 ? 0 : kv.Value.Total / n;
				parts.Add($"{kv.Key}:{n}x avg{avg:0}");
			}
			return string.Join(" ", parts);
		}

		public VitalStatsPacket() { }
		public VitalStatsPacket(int netId, Amounts amounts, PrimaryElement element)
		{
			using var _ = Profiler.Scope();

			NetId = netId;
			PrefabHash = element != null && element.TryGetComponent<KPrefabID>(out var kpid)
				? kpid.PrefabTag.GetHashCode()
				: 0;
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
			writer.Write(PrefabHash);
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
			PrefabHash = reader.ReadInt32();
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
			// Check what the sender was looking at, not what species it belongs to.
			//
			// This used to require GameTags.BaseMinion, which caught the real problem -
			// an id resolving to a different object here than there - by a proxy that
			// only worked while duplicants were the only thing sending vitals. Critters
			// send them now, and the proxy would have refused every one.
			//
			// The prefab the sender sampled is a better test than the proxy ever was: it
			// rejects a duplicant's packet landing on a critter AND a hatch's landing on
			// a drecko, which the tag check could not have seen. Zero means the sender is
			// an older build, and then the old rule applies rather than nothing.
			if (PrefabHash != 0)
			{
				int localHash = identity.gameObject.TryGetComponent<KPrefabID>(out var kpid)
					? kpid.PrefabTag.GetHashCode()
					: 0;
				if (localHash != PrefabHash)
				{
					PrefabMismatched++;
					DebugTools.ThrottledLog.Warn(
						$"[VitalStatsPacket] NetId {NetId} resolved to " +
						$"'{identity.gameObject.PrefabID()}', which is not what the sender " +
						"sampled - this id means something different on the two peers");
					return;
				}
			}
			else if (!identity.gameObject.HasTag(GameTags.BaseMinion))
			{
				DebugTools.ThrottledLog.Warn(
					$"[VitalStatsPacket] NetId {NetId} resolved to " +
					$"'{identity.gameObject.PrefabID()}', which is not a duplicant, and the " +
					"sender did not say what it sampled");
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
				// SetValue clamps, and says nothing when it does.
				//
				// Its IL is Max(GetMin(), Min(GetMax(), value)), and GetMax reads an
				// attribute - maxAttribute.GetTotalValue() - so the ceiling is not a
				// constant. It moves with traits and effects. If a modifier is on the
				// host and not on this peer, the host's value is above this peer's
				// ceiling and every packet is silently trimmed to fit: a divergence that
				// resending cannot close, because the send is not what is failing.
				//
				// That is the shape of the one duplicant that does not fit the rest of
				// the measurement. Every other duplicant's calories sit a uniform 4,335
				// behind the host - one and a half seconds of consumption, the sync
				// period, and it does not grow with a longer run. Humphrey is 60,166
				// behind with the host reading 4,016,666, which is above the 4,000,000
				// this peer clamps to.
				//
				// Counted rather than assumed. Whether Humphrey is clamping is a fact
				// the next run can state, and guessing at it is how three earlier
				// diagnoses in this project went wrong.
				// How far this peer had drifted before the correction landed.
				//
				// One duplicant sits ten to twenty times further from the host than the
				// rest - 36,000 calories against a uniform 4,335 - and the clamp theory
				// is dead, vitalClamped having read 0 in every row since it was added.
				// The two live explanations are that his packets arrive less often and
				// that his calories simply move faster, and both peers report him at
				// maximum stress, which in this game means binge eating.
				//
				// The correction size per apply separates them. If he is fed the same
				// number of packets and each one moves him ten times further, the lag is
				// the same 1.5 seconds everyone has and the rate is what differs, which
				// is not a defect. If his packets are rarer, it is.
				// Every amount, not only calories.
				//
				// The correction size is one sync period of that duplicant's drift,
				// measured rather than assumed, and it is the only honest yardstick for
				// judging whether the two peers disagree about a continuously consumed
				// value. Stamina and stress move at their own rates and were being judged
				// against a flat percentage chosen for calories.
				{
					var before = amounts.Get(kvp.Key);
					if (before != null)
						NoteCorrection(identity.GetProperName(), kvp.Key, kvp.Value - before.value);
				}

				// Read back rather than take a return value: Amounts.SetValue on the
				// collection returns void. It is AmountInstance.SetValue underneath it
				// that returns the clamped number, and that is the one whose IL shows
				// the clamp.
				amounts.SetValue(kvp.Key, kvp.Value);
				float applied = amounts.Get(kvp.Key).value;
				if (!Mathf.Approximately(applied, kvp.Value))
				{
					AmountsClamped++;
					if (AmountsClamped <= 5)
					{
						DebugTools.ThrottledLog.Warn(
							$"[VitalStatsPacket] '{identity.GetProperName()}' {kvp.Key}: " +
							$"host says {kvp.Value}, this peer clamped it to {applied} - " +
							"the two peers disagree about the limit, not about the value");
					}
				}
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
