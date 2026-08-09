using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets.World;
using UnityEngine;
using static STRINGS.UI.METERS;

namespace ONI_Together.Networking.Components.StructureStateSyncers
{
    public class ToiletSyncer : StructureSyncerBase
    {
        private FlushToilet flushToilet;
        private Toilet outhouseToilet;
        private Storage storage;
        private ConduitConsumer conduitConsumer;
        private KPrefabID prefabID;
        private BuildingHP buildingHP;

        /// <summary>Last host hit-point value reported as differing, so a standing disagreement is logged once.</summary>
        private int _lastReportedHpMismatch = int.MinValue;

        protected override void Initialize()
        {
            flushToilet = GetComponent<FlushToilet>();
            outhouseToilet = GetComponent<Toilet>();
            storage = GetComponent<Storage>();
            conduitConsumer = GetComponent<ConduitConsumer>();
            prefabID = GetComponent<KPrefabID>();
            buildingHP = GetComponent<BuildingHP>();
        }

        protected override void SampleState(out Variant value, out bool active, out Dictionary<string, Variant> optionalValues)
        {
            active = false;
            optionalValues = new Dictionary<string, Variant>();
            BuildingUtils.EncodeStorageContents(storage, optionalValues);
            optionalValues["is_operational"] = operational?.IsOperational ?? true;

            // Damage is not replicated anywhere in the mod - no code touches
            // BuildingHP, Damaged or Repairable - so each peer decided for
            // itself whether a building was broken, and they disagreed. A
            // toilet showed as needing repair on the client while the host saw
            // it working. is_operational is a different thing and does not
            // cover it.
            if (buildingHP != null)
                optionalValues["hit_points"] = buildingHP.HitPoints;
            if (flushToilet != null)
            {
                value = storage?.MassStored() ?? 0f;
                GetTotalWater(out float totalWater, out float totalWaste, out float totalGunk);
                optionalValues["total_water"] = totalWater;
                optionalValues["total_waste"] = totalWaste;
                optionalValues["total_gunk"] = totalGunk;
            }
            else if (outhouseToilet != null)
            {
                value = outhouseToilet.FlushesUsed;
            }
            else
            {
                value = 0f;
            }
        }

        protected override void ApplyState(StructureStatePacket packet)
        {
            if (storage == null) return;
            BuildingUtils.RebuildStorageFromData(storage, packet.OptionalValues);
            SyncToilet(packet);

            // Reported, not corrected. BuildingHP.HitPoints has no setter, so
            // forcing the client to match needs the game's own damage/repair
            // call rather than an assignment - and which one is right has to be
            // seen in a session before it is guessed at. The measurement is
            // what was missing: nothing in the mod touched BuildingHP at all,
            // so a toilet reading as broken on one peer and fine on the other
            // left no trace anywhere.
            if (buildingHP != null && packet.OptionalValues.TryGetValue("hit_points", out var hp))
            {
                int hostHp = (int)hp.Float;
                // Once per change, not once per packet. State arrives twice a
                // second per structure and a disagreement persists by
                // definition, so reporting every packet buried the log: 9005
                // copies of this one line, 27% of a crashed client's log, in
                // the session that was supposed to explain the crash.
                if (buildingHP.HitPoints != hostHp)
                {
                    if (_lastReportedHpMismatch != hostHp)
                    {
                        _lastReportedHpMismatch = hostHp;
                        DebugConsole.LogWarning(
                            $"[StructureState] {gameObject.GetProperName()} hit points differ: " +
                            $"host {hostHp}, here {buildingHP.HitPoints} - damage is not replicated");
                    }
                }
                else
                {
                    _lastReportedHpMismatch = int.MinValue;
                }
            }

            if (packet.OptionalValues.TryGetValue("is_operational", out var isOp))
            {
                if (isOp.Boolean)
                    prefabID.AddTag(GameTags.Operational);
                else
                    prefabID.RemoveTag(GameTags.Operational);
            }
        }

        private void SyncToilet(StructureStatePacket packet)
        {
            if (flushToilet != null)
                SyncFlushToilet(packet);
            else if (outhouseToilet != null)
                SyncOuthouse(packet);
        }

        private void GetTotalWater(out float totalWater, out float totalWaste, out float totalGunk)
        {
            totalWater = 0f;
            totalWaste = 0f; 
            totalGunk = 0f;

            foreach (var item in storage.items)
            {
                if (item == null) continue;
                var pe = item.GetComponent<PrimaryElement>();
                if (pe == null) continue;
                if (pe.ElementID == SimHashes.Water) totalWater += pe.Mass;
                else if (pe.ElementID == SimHashes.DirtyWater) totalWaste += pe.Mass;
                else if (pe.ElementID == GunkMonitor.GunkElement) totalGunk += pe.Mass;
            }
        }

        private void SyncFlushToilet(StructureStatePacket packet)
        {
            var opt = packet.OptionalValues;
            float totalWater = 0f, totalWaste = 0f, totalGunk = 0f;
            if (opt.TryGetValue("total_water", out var w)) totalWater = w.Float;
            if (opt.TryGetValue("total_waste", out var wa)) totalWaste = wa.Float;
            if (opt.TryGetValue("total_gunk", out var g)) totalGunk = g.Float;
            bool full = totalWater >= flushToilet.massConsumedPerUse;

            if (conduitConsumer != null)
                conduitConsumer.enabled = !full;

            float fillPct = Mathf.Clamp01(totalWater / flushToilet.massConsumedPerUse);
            float wastePct = Mathf.Clamp01(totalWaste / flushToilet.massEmittedPerUse);
            float gunkPct = Mathf.Clamp01(totalGunk / flushToilet.massEmittedPerUse);

            flushToilet.fillMeter?.SetPositionPercent(fillPct);
            flushToilet.contaminationMeter?.SetPositionPercent(wastePct);
            flushToilet.gunkMeter?.SetPositionPercent(gunkPct);
        }

        private void SyncOuthouse(StructureStatePacket packet)
        {
            outhouseToilet.FlushesUsed = packet.Value.Int;
            outhouseToilet.meter?.SetPositionPercent(Mathf.Clamp01((float)outhouseToilet.FlushesUsed / outhouseToilet.maxFlushes));
        }

        protected override bool ShouldForceSync()
        {
            return false;
        }
    }
}
