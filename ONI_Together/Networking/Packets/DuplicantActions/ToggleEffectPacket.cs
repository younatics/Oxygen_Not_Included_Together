using Klei.AI;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Patches.Duplicant;
using Shared.Interfaces.Networking;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.DuplicantActions
{
	internal class ToggleEffectPacket : IPacket, IBulkablePacket
	{
		public int MinionNetId;
		public string EffectId;
		public bool IsAdding;
		public bool ShouldSave;

		public int MaxPackSize => 500;

		public uint IntervalMs => 50;

		public ToggleEffectPacket() { }
		public ToggleEffectPacket(Effects instance, HashedString toRemove)
		{
			using var _ = Profiler.Scope();

			MinionNetId = instance.gameObject.AddOrGet<NetworkIdentity>().NetId;
			IsAdding = false;
			EffectId = toRemove.ToString();
		}
		public ToggleEffectPacket(Effects instance, Effect toAdd, bool shouldSave)
		{
			using var _ = Profiler.Scope();

			MinionNetId = instance.gameObject.AddOrGet<NetworkIdentity>().NetId;
			IsAdding = true;
			EffectId = toAdd.Id;
			ShouldSave = shouldSave;
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			MinionNetId = reader.ReadInt32();
			EffectId = reader.ReadString();
			IsAdding = reader.ReadBoolean();
			ShouldSave = reader.ReadBoolean();
		}
		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(MinionNetId);
			writer.Write(EffectId ?? string.Empty);
			writer.Write(IsAdding);
			writer.Write(ShouldSave);
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost)
				return;

			// Both of these reported the failure and then carried on into it.
			// A minion the client has not registered yet is routine - it arrives
			// a moment later - but the code fell straight through to
			// minionId.TryGetComponent on a null, so every miss became a
			// NullReferenceException escaping through the dispatch path. A live
			// session logged 103 of them in twenty seconds.
			if (!NetworkIdentityRegistry.TryGet(MinionNetId, out var minionId) || minionId == null)
			{
				DebugConsole.LogWarning("Could not find minion with net id " + MinionNetId + " to toggle effect " + EffectId + " to " + (IsAdding ? "on" : "off"));
				return;
			}
			if (!minionId.TryGetComponent<Effects>(out var minionEffects) || minionEffects == null)
			{
				DebugConsole.LogWarning("Could not find effects instance on minion " + minionId.gameObject.GetProperName());
				return;
			}
			if (IsAdding)
			{
				EffectsPatch.AddEffect(minionEffects, EffectId, ShouldSave);
			}
			else
			{
				EffectsPatch.RemoveEffect(minionEffects, EffectId);
			}
		}

	}
}
