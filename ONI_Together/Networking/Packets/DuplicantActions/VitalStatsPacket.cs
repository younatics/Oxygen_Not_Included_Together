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

		/// <summary>
		/// How fast the value itself moves, per second, as the host reports it.
		///
		/// The correction size was the first yardstick and it is the wrong one for
		/// anything the client simulates identically. Stamina is the case: every
		/// correction this peer applied was exactly zero, so the measured drift was zero
		/// and no bound was emitted at all - while the dump had every duplicant's stamina
		/// nearly a unit apart. Nothing was wrong with the replication. The two peers
		/// take their snapshots about 0.7 seconds apart in simulation time, and stamina
		/// moves in 0.7 seconds.
		///
		/// So this measures the thing that actually decides how far apart two snapshots
		/// can legitimately be: the rate of change, taken from consecutive host values
		/// and the time between them. It subsumes the correction size - a value the
		/// client does not simulate drifts by rate times the sync period, which is what
		/// the correction was - and it also covers the case the correction could not see.
		/// </summary>
		private class Motion
		{
			public float LastValue;
			public float LastTime;
			public bool Seen;

			/// <summary>
			/// The last few observed per-second rates, so the summary can be a median.
			///
			/// The mean was the first attempt and a spawn broke it: a Drecko's calories
			/// came out at 6,724,716 a second, which is not a rate, it is one
			/// discontinuity averaged in with three hundred ordinary samples. A bound
			/// built from that excuses any difference at all, which is worse than the
			/// percentage it replaced - the percentage was at least honest about being
			/// arbitrary.
			///
			/// A median ignores the jump without anyone having to decide what counts as
			/// one. Thirty-two samples is half a minute at one packet a second, recent
			/// enough to track a duplicant who starts eating and long enough that a
			/// single outlier cannot move the middle.
			/// </summary>
			public readonly List<float> Recent = new List<float>();
		}
		private static readonly Dictionary<string, Motion> _motion = new Dictionary<string, Motion>();

		private static void NoteMotion(int subject, string amount, float incoming)
		{
			if (subject == 0 || string.IsNullOrEmpty(amount)) return;

			string key = subject + "|" + amount;
			if (!_motion.TryGetValue(key, out var m)) _motion[key] = m = new Motion();

			float now = UnityEngine.Time.unscaledTime;
			if (m.Seen)
			{
				float dt = now - m.LastTime;
				// A tenth of a second. Below that the division amplifies float noise into
				// a rate large enough to excuse a real difference, which is the failure
				// mode this whole line of work exists to avoid.
				if (dt >= 0.1f)
				{
					float delta = incoming - m.LastValue;
					if (delta < 0) delta = -delta;

					m.Recent.Add(delta / dt);
					if (m.Recent.Count > MotionSamples) m.Recent.RemoveAt(0);
				}
			}

			m.LastValue = incoming;
			m.LastTime = now;
			m.Seen = true;
		}

		/// <summary>
		/// The measured per-second rate of change for one subject's one amount, or 0 if
		/// this peer has not seen it move. Zero is a real answer: a value that does not
		/// move has no excuse for differing.
		/// </summary>
		private const int MotionSamples = 32;

		/// <summary>
		/// Seconds since this subject's amount was last set by an arriving packet, or -1
		/// if it never was.
		///
		/// One duplicant's stamina reads 99.6 on the host and 31.2 on the client - forty
		/// times any bound her own motion can justify - while the guard refuses nothing,
		/// the clamp counter is zero, and a rate was measured for her, so packets did
		/// arrive at some point. What none of that says is whether they are still
		/// arriving. A value that stopped being corrected and a value that is corrected
		/// and then overwritten look identical in a snapshot and need opposite fixes.
		/// </summary>
		public static float SecondsSinceApplied(int subject, string amount)
		{
			if (!_motion.TryGetValue(subject + "|" + amount, out var m) || !m.Seen) return -1f;
			return UnityEngine.Time.unscaledTime - m.LastTime;
		}

		public static float RateOfChange(int subject, string amount)
		{
			if (!_motion.TryGetValue(subject + "|" + amount, out var m) || m.Recent.Count == 0) return 0f;

			// Sorted on a copy. Sorting the live list would reorder it, and the oldest
			// sample is dropped from the front - it has to stay in arrival order.
			var sorted = new List<float>(m.Recent);
			sorted.Sort();
			return sorted[sorted.Count / 2];
		}

		/// <summary>
		/// Keyed by NetId, not by name, and this was wrong until a critter proved it.
		///
		/// Duplicants have unique proper names, so name-keying worked for as long as they
		/// were the only subjects. Critters do not: every Drecko in the colony answers to
		/// the same species string, so their samples all landed in one entry and
		/// consecutive readings came from different animals. The measured rate came out
		/// at 6,737,860 calories a second - and it survived a median, because it was not
		/// an outlier, it was most of the samples.
		///
		/// A bound built from that excuses any difference at all, which is worse than the
		/// arbitrary percentage it replaced.
		/// </summary>
		private static void NoteCorrection(int subject, string amount, float delta)
		{
			if (subject == 0 || string.IsNullOrEmpty(amount)) return;

			string key = subject + "|" + amount;
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
		public static float AverageCorrection(int subject, string amount)
		{
			if (!_drift.TryGetValue(subject + "|" + amount, out var d) || d.Applies == 0) return 0f;
			return (float)(d.Total / d.Applies);
		}

		/// <summary>
		/// Applies and average size per amount, across everybody.
		///
		/// Thirty differing vital rows carried no rate at all - every duplicant's
		/// stamina and nine of their stress values - and a missing rate has two causes
		/// that need opposite fixes. Either the amount is never corrected, in which case
		/// the host is not sending it and the difference is real; or it is corrected
		/// every second with a delta that rounds to nothing, in which case the value is
		/// equal at each apply and drifts between them, and the correction size is the
		/// wrong yardstick for it.
		///
		/// The applies count separates those, and nothing recorded it.
		/// </summary>
		public static string DriftByAmount()
		{
			if (_drift.Count == 0) return "none";

			var byAmount = new Dictionary<string, (int applies, double total)>();
			foreach (var kv in _drift)
			{
				int bar = kv.Key.IndexOf('|');
				if (bar < 0) continue;
				string amount = kv.Key.Substring(bar + 1);
				byAmount.TryGetValue(amount, out var acc);
				byAmount[amount] = (acc.applies + kv.Value.Applies, acc.total + kv.Value.Total);
			}

			var parts = new List<string>();
			foreach (var kv in byAmount.OrderByDescending(kv => kv.Value.applies).Take(8))
			{
				double avg = kv.Value.applies == 0 ? 0 : kv.Value.total / kv.Value.applies;
				parts.Add($"{kv.Key}:{kv.Value.applies}x avg{avg:0.###}");
			}
			return string.Join(" ", parts);
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
						NoteCorrection(NetId, kvp.Key, kvp.Value - before.value);

					// Independent of whether the correction was zero.
					NoteMotion(NetId, kvp.Key, kvp.Value);
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
