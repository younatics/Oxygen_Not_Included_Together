using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Chores;
using Shared.Profiling;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	public class DuplicantChoreBroadcaster : KMonoBehaviour, IRender200ms
	{
		public static readonly HashSet<int> SubscribedNetIds = new();
		public static readonly HashSet<int> PendingImmediate = new();

		/// <summary>Same hazard as the status subscriptions: an id left over from
		/// a session belongs to a different duplicant in the next one.</summary>
		public static void ResetForNewSession()
		{
			SubscribedNetIds.Clear();
			PendingImmediate.Clear();
		}

		private const float BroadcastIntervalSeconds = 0.5f;

		[MyCmpGet] private NetworkIdentity identity;
		[MyCmpGet] private ChoreConsumer consumer;
		[MyCmpGet] private KSelectable selectable;

		private readonly ChoreConsumer.PreconditionSnapshot _scratchSnapshot = new();
		private float timeSinceLastBroadcast;
		private int _sweepId;

		public override void OnSpawn()
		{
			using var _ = Profiler.Scope();
			base.OnSpawn();
			timeSinceLastBroadcast = 0f;
		}

		public void Render200ms(float dt)
		{
			using var _ = Profiler.Scope();
			if (!MultiplayerSession.IsHostInSession) return;
			if (identity == null || consumer == null || selectable == null) return;
			if (!SubscribedNetIds.Contains(identity.NetId)) return;

			timeSinceLastBroadcast += dt;
			bool immediate = PendingImmediate.Remove(identity.NetId);
			if (!immediate && timeSinceLastBroadcast < BroadcastIntervalSeconds) return;
			timeSinceLastBroadcast = 0f;

            try
            {
                BroadcastSnapshot();
            }
            catch (Exception ex)
            {
                DebugConsole.LogError($"[DuplicantChoreBroadcaster] Failed to broadcast for dupe {identity.NetId}: {ex}");
            }
        }

		private void BroadcastSnapshot()
		{
			using var _ = Profiler.Scope();

			var providers = consumer.providers;
			if (providers == null) return;

			bool wasSelected = selectable.IsSelected;
			selectable.selected = true;

			try
			{
				_scratchSnapshot.succeededContexts.Clear();
				_scratchSnapshot.failedContexts.Clear();
				consumer.consumerState.Refresh();

				for (int i = 0; i < providers.Count; i++)
					providers[i].CollectChores(consumer.consumerState, _scratchSnapshot.succeededContexts, _scratchSnapshot.failedContexts);

				_scratchSnapshot.succeededContexts.Sort();
				_scratchSnapshot.failedContexts.Sort();
			}
			finally
			{
				selectable.selected = wasSelected;
            }

			int listIndex = 0;
			var packet = new ChoreErrandsPacket { DupeNetId = identity.NetId };
			AppendCurrentChore(packet, ref listIndex);

			var lastContext = default(Chore.Precondition.Context);
			bool hasLastContext = false;
			AppendEntriesMerged(packet, _scratchSnapshot.succeededContexts, ref lastContext, ref hasLastContext, ref listIndex);
			AppendEntriesMerged(packet, _scratchSnapshot.failedContexts, ref lastContext, ref hasLastContext, ref listIndex);

			// Split on bytes. The cap of 32 entries counted rows, and a row here
			// carries three strings - a chore type id, a target label like
			// "Deliver Coal to Coal Generator", and an icon name - so nine to
			// thirteen of them already exceed the payload limit the cap was
			// meant to protect. SendChunked always sends Reliable, so going
			// over turned a twice-a-second unreliable broadcast per duplicant
			// into a reliable chunk burst.
			var batches = SweepBatcher.Split(packet.Entries, ChoreErrandsPacket.HeaderBytes, e => e.Bytes());

			int sweepId = ++_sweepId;
			for (int i = 0; i < batches.Count; i++)
			{
				PacketSender.SendToAllClients(new ChoreErrandsPacket
				{
					DupeNetId = identity.NetId,
					SweepId = sweepId,
					BatchIndex = i,
					BatchCount = batches.Count,
					Entries = batches[i]
				}, PacketSendMode.Unreliable);
			}
		}

		private void AppendCurrentChore(ChoreErrandsPacket packet, ref int listIndex)
		{
			var currentDriver = consumer.choreDriver;
			if (currentDriver == null) return;
			var current = currentDriver.GetCurrentChore();
			if (current == null || current.target.isNull) return;
			var targetGO = current.target.gameObject;
			if (targetGO == null) return;

			var entry = BuildEntry(current, targetGO, isCurrent: true);
            entry.ListIndex = listIndex++;

            packet.Entries.Add(entry);
		}

		private void AppendEntriesMerged(ChoreErrandsPacket packet, List<Chore.Precondition.Context> contexts,
			ref Chore.Precondition.Context lastContext, ref bool hasLastContext, ref int listIndex)
		{
			var currentDriver = consumer.choreDriver;
			for (int i = contexts.Count - 1; i >= 0 && packet.Entries.Count < ChoreErrandsPacket.MaxEntries; i--)
			{
				var ctx = contexts[i];
				if (ctx.chore == null || ctx.chore.target.isNull) continue;
				if (!ctx.IsPotentialSuccess()) continue;
				if (ctx.chore.driver == currentDriver) continue;

				var targetGO = ctx.chore.target.gameObject;
				if (targetGO == null) continue;

				if (hasLastContext && GameUtil.AreChoresUIMergeable(ctx, lastContext))
				{
					var last = packet.Entries[packet.Entries.Count - 1];
					last.MoreAmount++;
					packet.Entries[packet.Entries.Count - 1] = last;
					continue;
				}

				var entry = BuildEntry(ctx.chore, targetGO, isCurrent: false);
                entry.ListIndex = listIndex++;

                packet.Entries.Add(entry);

				lastContext = ctx;
				hasLastContext = true;
			}
		}

		private ErrandEntry BuildEntry(Chore chore, GameObject targetGO, bool isCurrent)
		{
			string targetLabel;
			if (targetGO == gameObject)
				targetLabel = global::STRINGS.UI.UISIDESCREENS.MINIONTODOSIDESCREEN.SELF_LABEL.text;
			else
				targetLabel = targetGO.GetProperName();

			return new ErrandEntry
			{
				ChoreTypeId = chore.choreType?.Id ?? string.Empty,
				TargetCell = Grid.PosToCell(targetGO),
				TargetLabel = targetLabel,
				PriorityClass = (int)chore.masterPriority.priority_class,
				Priority = chore.masterPriority.priority_value,
				PersonalPriority = consumer.GetPersonalPriority(chore.choreType),
				IsCurrent = isCurrent,
				IconSpriteName = ResolveIconSprite(chore.choreType),
				ListIndex = 0, // Default to 0
			};
		}

		private string ResolveIconSprite(ChoreType choreType)
		{
			if (choreType == null || choreType.groups == null || choreType.groups.Length == 0)
				return string.Empty;
			var best = choreType.groups[0];
			for (int i = 1; i < choreType.groups.Length; i++)
			{
				if (consumer.GetPersonalPriority(best) < consumer.GetPersonalPriority(choreType.groups[i]))
					best = choreType.groups[i];
			}
			return best?.sprite ?? string.Empty;
		}
	}
}
