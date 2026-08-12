using System;
using System.Collections.Generic;
using System.Text;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets.World;
using UnityEngine;

namespace ONI_Together.Networking.Components.StructureStateSyncers
{
    /// <summary>
    /// Replicates every container on a building, not the first one.
    ///
    /// GetComponent&lt;Storage&gt; returns one, and a ComplexFabricator has three - the
    /// ingredients waiting to be used, the products waiting to be collected, and the
    /// materials it was built from. Only whichever came first in the prefab was
    /// replicated, so the measurement kept naming fabricators after everything else had
    /// converged: a MicrobeMusher with 75 kg of dirt on the host and exactly 150 on the
    /// client, a MetalRefinery at 310 kg of water against 400.
    ///
    /// The direction is the giveaway. The client holds more, because the client is
    /// blocked from producing - by design, so the host owns production - and so its
    /// ingredients pile up while the host consumes them. The correction that would have
    /// fixed it was being sent for the wrong container.
    ///
    /// Keyed by index. EncodeStorageContents already took a key prefix for exactly this
    /// and nothing ever passed one. Both peers instantiate the same prefab, so the
    /// component order is the same on both, which is what makes an index meaningful
    /// across the wire.
    /// </summary>
    public class StorageStateSyncer : StructureSyncerBase
    {
        private Storage[] storages = new Storage[0];

        /// <summary>The first one, for the value and temperature checks that want a single number.</summary>
        private Storage storage;

        /// <summary>Buildings found holding more than one container.</summary>
        public static int MultiStorageBuildings { get; private set; }

        private float temperatureThreshold = 0.3f;
        private float lastStorageTemperature;

        public struct StorageData
        {
            public int PrefabTagHash;
            public float Mass;
            public float Units;
            public float Temperature;
            public byte DiseaseIdx;
            public int DiseaseCount;
        }

        protected override void Initialize()
        {
            storages = GetComponents<Storage>();
            storage = storages.Length > 0 ? storages[0] : null;
            if (storages.Length > 1) MultiStorageBuildings++;
            checkOptionalsValuesForChanges = false; // Skip checking optionals for changes since we check temperature in ShouldForceSync and mass is checked via value
        }


        protected override void SampleState(out Variant value, out bool active, out Dictionary<string, Variant> optionalValues)
        {
            active = false;
            optionalValues = new Dictionary<string, Variant>();

            // The total across every container, so a change in any of them registers.
            //
            // This used to be the first container's mass alone, and with the optional
            // comparison switched off that was the only trigger there was - so a
            // fabricator whose ingredients changed while its product storage held steady
            // was not considered changed at all.
            float total = 0f;
            for (int i = 0; i < storages.Length; i++)
            {
                // Sampled twice a second on a building that can be deconstructed
                // between ticks, and ?. does not protect against a destroyed one.
                if (storages[i].IsNullOrDestroyed()) continue;
                total += storages[i].MassStored();
                BuildingUtils.EncodeStorageContents(storages[i], optionalValues, StorageKey(i));
            }
            value = total;
        }

        protected override void ApplyState(StructureStatePacket packet)
        {
            if (packet.OptionalValues.Count < 1) return;

            for (int i = 0; i < storages.Length; i++)
            {
                if (storages[i].IsNullOrDestroyed()) continue;

                // Only containers the sender described. A building whose prefab changed
                // between versions would otherwise have a container cleared by a packet
                // that says nothing about it.
                if (!packet.OptionalValues.ContainsKey(StorageKey(i) + "stor")) continue;

                BuildingUtils.RebuildStorageFromData(storages[i], packet.OptionalValues, StorageKey(i));
            }
        }

        /// <summary>
        /// The key a container's contents travel under.
        ///
        /// Empty for the first one, so a host on this build and a client on the previous
        /// one still agree about the container that was already being replicated. Index
        /// order is stable because both peers instantiate the same prefab.
        /// </summary>
        private static string StorageKey(int index) => index == 0 ? string.Empty : "s" + index;

        protected override bool ShouldForceSync()
        {
            if (storage == null) return false;

            // Across every container, for the same reason the mass total is: a
            // fabricator's output heating up while its input holds steady is a change.
            float currentTemp = 0f;
            for (int i = 0; i < storages.Length; i++)
            {
                if (storages[i].IsNullOrDestroyed()) continue;
                float t = GetMaxStorageTemperature(storages[i]);
                if (t > currentTemp) currentTemp = t;
            }

            if (Mathf.Abs(currentTemp - lastStorageTemperature) > temperatureThreshold)
            {
                lastStorageTemperature = currentTemp;
                return true;
            }
            return false;
        }

        // TODO: Does not scale well in the late game
        private float GetMaxStorageTemperature(Storage storage)
        {
            float max = 0f;
            for (int i = 0; i < storage.items.Count; i++)
            {
                var pe = storage.items[i]?.GetComponent<PrimaryElement>();
                if (pe != null && pe.Temperature > max)
                    max = pe.Temperature;
            }

            return max;
        }
    }
}
