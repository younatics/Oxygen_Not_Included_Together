using Database;
using Klei.AI;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
    public class ClientReceiver_StatusItems : KMonoBehaviour
    {
        public enum StatusRecieverType
        {
            DUPLICANT,
            CREATURE,
            MISC,
            BUILDING,
            ROBOT
        }
        
        [MyCmpGet] private NetworkIdentity identity;
        [MyCmpGet] private KSelectable selectable;

        public StatusRecieverType recieverType = StatusRecieverType.DUPLICANT;
        
        public float LastApplyTime { get; private set; }

        /// <summary>
        /// One per entity. A status sweep is only ever about this entity, and
        /// Apply clears everything before rebuilding, so a batch applied alone
        /// would wipe the items carried by the rest of its sweep.
        /// </summary>
        private readonly SweepAssembler<StatusItemEntry> _assembler =
            new SweepAssembler<StatusItemEntry>("StatusItems");

        public void AcceptBatch(int sweepId, int batchIndex, int batchCount, List<StatusItemEntry> batch)
        {
            using var _ = Profiler.Scope();

            if (_assembler.Accept(sweepId, batchIndex, batchCount, batch, out var complete))
                Apply(complete);
        }

        public void Apply(List<StatusItemEntry> entries)
        {
            using var _ = Profiler.Scope();
            LastApplyTime = Time.unscaledTime;

            var group = selectable?.GetStatusItemGroup();
            if (group == null) return;

            var toRemove = new List<Guid>();
            foreach (var entry in group)
                toRemove.Add(entry.id);

            foreach (var guid in toRemove)
                group.RemoveStatusItem(guid, immediate: true);

            if (entries == null || entries.Count == 0)
                return;

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var syncedItem = BuildSyncedItem(entry);
                if (syncedItem == null) continue;

                var category = ResolveCategory(entry.CategoryId);
                group.AddStatusItem(syncedItem, null, category);
            }
        }

        private StatusItem BuildSyncedItem(StatusItemEntry entry)
        {
            if (string.IsNullOrEmpty(entry.ItemId)) return null;

            StatusItem original = recieverType switch
            {
                StatusRecieverType.DUPLICANT => Db.Get().DuplicantStatusItems.TryGet(entry.ItemId),
                StatusRecieverType.CREATURE => Db.Get().CreatureStatusItems.TryGet(entry.ItemId),
                StatusRecieverType.MISC => Db.Get().MiscStatusItems.TryGet(entry.ItemId),
                StatusRecieverType.BUILDING => Db.Get().BuildingStatusItems.TryGet(entry.ItemId),
                StatusRecieverType.ROBOT => Db.Get().RobotStatusItems.TryGet(entry.ItemId),
                _ => Db.Get().DuplicantStatusItems.TryGet(entry.ItemId),
            };

            if (original != null)
            {
                var item = new StatusItem(
                    "ONIT_Sync_" + entry.ItemId,
                    entry.DisplayName ?? original.Name,
                    entry.Tooltip ?? original.tooltipText,
                    original.iconName,
                    original.iconType,
                    original.notificationType,
                    false,
                    original.render_overlay,
                    original.status_overlays,
                    false
                );
                item.sprite = original.sprite;
                item.showInHoverCardOnly = original.showInHoverCardOnly;
                return item;
            }

            var effect = Db.Get().effects.TryGet(entry.ItemId);
            if (effect != null)
                return BuildFromEffect(entry, effect);

            return null;
        }

        private static StatusItem BuildFromEffect(StatusItemEntry entry, Effect effect)
        {
            var iconType = StatusItem.IconType.Info;
            var notifType = NotificationType.Neutral;
            var iconName = "dash";

            if (effect.isBad)
            {
                iconType = StatusItem.IconType.Exclamation;
                notifType = NotificationType.Bad;
                iconName = "status_item_exclamation";
            }

            if (!effect.customIcon.IsNullOrWhiteSpace())
            {
                iconType = StatusItem.IconType.Custom;
                iconName = effect.customIcon;
            }

            return new StatusItem(
                "ONIT_Sync_" + entry.ItemId,
                entry.DisplayName ?? effect.Name,
                entry.Tooltip ?? effect.description,
                iconName,
                iconType,
                notifType,
                false,
                OverlayModes.None.ID,
                2,
                false
            );
        }

        private static StatusItemCategory ResolveCategory(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return Db.Get().StatusItemCategories.TryGet(id);
        }
    }
}
