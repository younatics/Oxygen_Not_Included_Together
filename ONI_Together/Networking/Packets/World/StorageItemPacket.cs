using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.Collections.Generic;
using System.IO;
using Shared.Profiling;
using UnityEngine;
using static Storage;
using Klei;
using Shared.Interfaces.Networking;
using System.ComponentModel;

namespace ONI_Together.Networking.Packets.World
{
    /// <summary>
    /// Modified version of GroundItemPickedUpPacket
    /// </summary>
    public class StorageItemPacket : IPacket, IBulkablePacket, IRequiresLoadedWorld
    {
        private static readonly PendingRemovals Pending = new PendingRemovals("PendingStorage");

        public int NetId;
        public int StorageNetId;
        public FXPrefix FxPrefix;
        public bool DoDiseaseTransfer;

        public int ConsumedPrefabHash;
        public float ConsumedAmount; // Mass / Units

        public int MaxPackSize => 500;

        public uint IntervalMs => 250;

        public static bool TryConsumePending(int netId) => Pending.TryConsume(netId);

        public static void ClearPending() => Pending.Clear();

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();
            writer.Write(NetId);
            writer.Write(StorageNetId);
            writer.Write((int)FxPrefix);
            writer.Write(DoDiseaseTransfer);

            writer.Write(ConsumedPrefabHash);
            writer.Write(ConsumedAmount);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();
            NetId = reader.ReadInt32();
            StorageNetId = reader.ReadInt32();
            FxPrefix = (FXPrefix)reader.ReadInt32();
            DoDiseaseTransfer = reader.ReadBoolean();

            ConsumedPrefabHash = reader.ReadInt32();
            ConsumedAmount = reader.ReadSingle();
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            // FX Only
            if (NetId == 0)
            {
                if (!NetworkIdentityRegistry.TryGetComponent<Storage>(StorageNetId, out var container))
                    return;

                if (PopFXManager.Instance != null && ConsumedPrefabHash != 0)
                {
                    DisplayFX(null, container); // FX Only
                }
                return;
            }

            if (!NetworkIdentityRegistry.TryGetComponent<Pickupable>(NetId, out var pickupable))
            {
                Pending.Queue(NetId);
                ThrottledLog.Warn("[StoreItemPacket] store arrived for an item this peer does not have");
                return;
            }

            if (!NetworkIdentityRegistry.TryGetComponent<Storage>(StorageNetId, out var storage))
            {
                DebugConsole.LogWarning($"[StoreItemPacket] No storage found with NetID: {StorageNetId}");
                Util.KDestroyGameObject(pickupable.gameObject); // Still destroy the pickupable
                return;
            }

            DisplayFX(pickupable.gameObject, storage);
            HandleDiseaseTransfer(pickupable.gameObject, storage);
            Util.KDestroyGameObject(pickupable.gameObject);
        }

        public void DisplayFX(GameObject pickupable, Storage storage)
        {
            Tag prefabTag = new Tag(ConsumedPrefabHash);
            GameObject prefab = Assets.GetPrefab(prefabTag);
            if (prefab != null)
            {
                LocString locString;
                Sprite sprite;
                if (FxPrefix == FXPrefix.Delivered)
                {
                    // Added too storage
                    locString = global::STRINGS.UI.DELIVERED;
                    sprite = PopFXManager.Instance.sprite_Plus;
                }
                else
                {
                    // Taken from storage
                    locString = global::STRINGS.UI.PICKEDUP;
                    sprite = PopFXManager.Instance.sprite_Negative;
                }

                int amount = (int)ConsumedAmount;
                // The GameObject was null-checked and the component fetched
                // from it was not. PickupItemPacket does the same fetch and
                // checks it.
                if (pickupable != null && pickupable.TryGetComponent<PrimaryElement>(out var component))
                {
                    amount = (int)component.Units;
                }

                string text = Assets.IsTagCountable(prefabTag)
                    ? string.Format(locString, amount, prefab.GetProperName())
                    : string.Format(locString, GameUtil.GetFormattedMass(amount), prefab.GetProperName());

                PopFXManager.Instance.SpawnFX(
                    Def.GetUISprite(prefab).first, sprite,
                    text, storage.transform, Vector3.zero);
            }
        }

        public void HandleDiseaseTransfer(GameObject go, Storage storage)
        {
            if(!DoDiseaseTransfer) return;
            if (go == null || storage == null) return;

            // Both of these can be absent. The storage's own PrimaryElement is
            // null for containers that are not made of anything the sim tracks,
            // and it was dereferenced four lines later without a check - a
            // client threw nine of these out of a packet handler in five
            // minutes.
            PrimaryElement primaryElement = storage.primaryElement;
            if (primaryElement == null)
                return;

            PrimaryElement component = go.GetComponent<PrimaryElement>();
            if(!(component == null))
            {
                SimUtil.DiseaseInfo invalid = SimUtil.DiseaseInfo.Invalid;
                invalid.idx = component.DiseaseIdx;
                invalid.count = (int)((float)component.DiseaseCount * 0.05f);
                SimUtil.DiseaseInfo invalid2 = SimUtil.DiseaseInfo.Invalid;
                invalid2.idx = primaryElement.DiseaseIdx;
                invalid2.count = (int)((float)primaryElement.DiseaseCount * 0.05f);
                component.ModifyDiseaseCount(-invalid.count, "Storage.TransferDiseaseWithObject");
                primaryElement.ModifyDiseaseCount(-invalid2.count, "Storage.TransferDiseaseWithObject");
                if (invalid.count > 0)
                {
                    primaryElement.AddDisease(invalid.idx, invalid.count, "Storage.TransferDiseaseWithObject");
                }

                if (invalid2.count > 0)
                {
                    component.AddDisease(invalid2.idx, invalid2.count, "Storage.TransferDiseaseWithObject");
                }
            }
        }
    }
}
