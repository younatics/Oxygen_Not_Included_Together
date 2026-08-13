using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// Syncs the active tech's research points from host to clients.
	///
	/// This used to carry one number - the fraction of total cost paid - and the client
	/// rebuilt each research type's points as cost * fraction. That round trip cannot
	/// work, and the failure is deterministic rather than occasional: across six runs
	/// with settle times of 120, 150 and 180 seconds, the host read 0.67 and the client
	/// read 0.46, identical to the digit every time. A lag varies with the run. This did
	/// not.
	///
	/// The reason is that two different averages are involved. The host was sending
	/// sum(min(points, cost)) / sum(cost), which weights by cost, while
	/// TechInstance.GetTotalPercentageComplete - what the game's own progress bar and
	/// the state dump both read - walks progressInventory.PointsByTypeID and averages
	/// the per-type percentages, which does not. When a tech's research types have
	/// different costs and different completion, one scalar cannot carry the vector, and
	/// spreading it back out uniformly produces a number that is wrong in a fixed
	/// direction forever.
	///
	/// So it carries the points themselves. The client's inventory then matches the
	/// host's entry for entry and every number derived from it agrees, whichever average
	/// the reader happens to use.
	/// </summary>
	public class ResearchProgressPacket : IPacket
	{

		public string TechId;

		/// <summary>
		/// Points paid per research type, exactly as the host holds them. Empty from a
		/// peer built before this carried them, which is what Progress is still here for.
		/// </summary>
		public Dictionary<string, float> PointsByType = new Dictionary<string, float>();

		/// <summary>
		/// The old cost-weighted fraction. Still sent and still read, but only when the
		/// per-type points are absent - a mixed pair degrades to the previous behaviour
		/// rather than losing the progress bar entirely.
		/// </summary>
		public float Progress; // 0.0 to 1.0

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(TechId ?? string.Empty);
			writer.Write(Progress);

			writer.Write(PointsByType?.Count ?? 0);
			if (PointsByType != null)
			{
				foreach (var kvp in PointsByType)
				{
					writer.Write(kvp.Key ?? string.Empty);
					writer.Write(kvp.Value);
				}
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			TechId = reader.ReadString();
			Progress = reader.ReadSingle();

			int count = reader.ReadInt32();
			PointsByType = new Dictionary<string, float>(count);
			for (int i = 0; i < count; i++)
			{
				string key = reader.ReadString();
				PointsByType[key] = reader.ReadSingle();
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost) return;
			if (Research.Instance == null) return;
			if (string.IsNullOrEmpty(TechId)) return;

			var tech = Db.Get().Techs.TryGet(TechId);
			if (tech == null) return;

			var techInstance = Research.Instance.Get(tech);
			if (techInstance == null) return;

			// Set the progress on each research type via reflection
			try
			{
				var pointsDict = techInstance.progressInventory.PointsByTypeID;

				if (pointsDict != null && PointsByType != null && PointsByType.Count > 0)
				{
					// The host's points, copied across. No reconstruction, so nothing to
					// be lossy about.
					foreach (var kvp in PointsByType)
						pointsDict[kvp.Key] = Mathf.RoundToInt(kvp.Value);
				}
				else if (pointsDict != null)
				{
					// A peer that predates the per-type points. This is the old
					// reconstruction and it is known to be wrong when a tech's research
					// types differ in cost - it read 0.46 against the host's 0.67 in six
					// runs out of six - but a wrong progress bar beats none, and a mixed
					// pair should degrade rather than break.
					foreach (var researchType in tech.costsByResearchTypeID.Keys)
					{
						float cost = tech.costsByResearchTypeID[researchType];
						float newPoints = cost * Progress;

						pointsDict[researchType] = Mathf.RoundToInt(newPoints);
					}
				}

				// Refresh the research screen if open
				try
				{
					object researchScreen = null;
					if (ManagementMenu.Instance != null)
					{
						researchScreen = ManagementMenu.Instance.researchScreen;
					}

					if (researchScreen != null)
					{
						HarmonyLib.Traverse.Create(researchScreen)
							.Method("UpdateProgressBars")
							.GetValue();
					}
				}
				catch (System.Exception ex) { DebugConsole.LogError($"[ResearchProgressPacket] Error refreshing research screen: {ex}"); }
			}
			catch (System.Exception ex)
			{
				DebugConsole.LogWarning($"[ResearchProgressPacket] Failed to set progress: {ex}");
			}
		}
	}
}

