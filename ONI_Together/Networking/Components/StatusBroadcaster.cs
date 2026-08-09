using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
    public class StatusBroadcaster : KMonoBehaviour, IRender200ms
    {
        public static readonly HashSet<int> SubscribedNetIds = new();
        public static readonly HashSet<int> PendingImmediate = new();

        private const float SoftSyncInterval = 0.5f;
        private const float HardSyncInterval = 5f;

        [MyCmpGet] private NetworkIdentity identity;
        [MyCmpGet] private KSelectable selectable;

        private float timeSinceLastSoftSync;
        private float timeSinceHardSync;
        private int _sweepId;

        public override void OnSpawn()
        {
            using var _ = Profiler.Scope();
            base.OnSpawn();
            timeSinceLastSoftSync = 0f;
            timeSinceHardSync = 0f;
        }

        public void Render200ms(float dt)
        {
            using var _ = Profiler.Scope();
            if (!MultiplayerSession.IsHostInSession) return;
            if (identity == null || selectable == null) return;

            timeSinceLastSoftSync += dt;
            timeSinceHardSync += dt;

            bool isSubscribed = SubscribedNetIds.Contains(identity.NetId);
            bool doSoftSync = isSubscribed && timeSinceLastSoftSync >= SoftSyncInterval;
            bool doHardSync = timeSinceHardSync >= HardSyncInterval;
            bool immediate = PendingImmediate.Remove(identity.NetId);

            if (immediate || doSoftSync || doHardSync)
            {
                if (doSoftSync || immediate) timeSinceLastSoftSync = 0f;
                if (doHardSync) timeSinceHardSync = 0f;

                try
                {
                    BroadcastSnapshot();
                }
                catch (Exception ex)
                {
                    DebugConsole.LogError($"[DuplicantStatusBroadcaster] Failed to broadcast for dupe {identity.NetId}: {ex}");
                }
            }
        }

        private void BroadcastSnapshot()
        {
            using var _ = Profiler.Scope();

            int cell = Grid.PosToCell(transform.position);
            if (!WorldStateSyncer.Instance.IsCellVisibleToAnyClientViewport(cell, margin: 4))
                return;

            var group = selectable.GetStatusItemGroup();
            if (group == null) return;

            var entries = new List<StatusItemEntry>();
            foreach (var entry in group)
            {
                if (entries.Count >= StatusItemsPacket.MaxEntries)
                    break;

                entries.Add(new StatusItemEntry
                {
                    ItemId = entry.item?.Id ?? string.Empty,
                    CategoryId = entry.category?.Id,
                    DisplayName = entry.GetName(),
                    Tooltip = entry.item?.GetTooltip(entry.data) ?? string.Empty,
                });
            }

            // Split on bytes. The old cap of 64 entries was counted, and these
            // entries are four strings each with a rendered tooltip among them -
            // "Stress: 42.3% (+0.4%/cycle)" and worse - so four to eleven of
            // them already exceed the payload limit the cap was there to
            // protect. Every duplicant with a few status items was sending an
            // oversize packet twice a second, and because SendChunked always
            // sends Reliable, the transport turned each one into a reliable
            // chunk burst on a path that was deliberately Unreliable.
            var batches = SweepBatcher.Split(entries, StatusItemsPacket.HeaderBytes, e => e.Bytes());

            int sweepId = ++_sweepId;
            for (int i = 0; i < batches.Count; i++)
            {
                PacketSender.SendToAllClients(new StatusItemsPacket
                {
                    DupeNetId = identity.NetId,
                    SweepId = sweepId,
                    BatchIndex = i,
                    BatchCount = batches.Count,
                    Entries = batches[i]
                }, PacketSendMode.Unreliable);
            }
        }
    }
}
