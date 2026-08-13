using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.IO;
using Shared.Profiling;
using ONI_Together.Networking.Components;
using static TUNING.NOISE_POLLUTION;
using ONI_Together.Misc;
using UnityEngine;
using ONI_Together.Networking.Components.StructureStateSyncers;
using static ONI_Together.STRINGS.UI.MP_OVERLAY;
using System.Collections.Generic;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking.Packets.World
{
	public class StructureStatePacket : IPacket
	{

        public int NetId;
        public int Cell;
		public Variant Value; // Joules for Battery, Progress for others

		public Dictionary<string, Variant> OptionalValues = []; // Extra things (such as EnergyGenerator mass, storage amount etc)

		public bool IsActive; // Operational active state

		/// <summary>
		/// The class name of the syncer that produced this snapshot.
		///
		/// Without it a building could only ever carry one StructureSyncerBase: the
		/// receiver handed every packet to every syncer on the object, so a second one
		/// meant the storage syncer trying to read flag packets and logging "Key: stor
		/// not found" a couple of hundred times a run. That limit was never documented
		/// and was found by hitting it.
		///
		/// Empty when it comes from a peer built before this field existed, and the
		/// receiver then falls back to the old behaviour rather than dropping the state.
		/// </summary>
		public string SyncerType = string.Empty;

        public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

            writer.Write(NetId);
			writer.Write(Cell);
            Value.Write(writer);
			writer.Write(IsActive);
			// Which syncer produced this, so the receiver can route it to the matching
			// one instead of to every syncer on the object. See OnDispatched.
			writer.Write(SyncerType ?? string.Empty);

            using var optMs = new MemoryStream();
            using var optBw = new BinaryWriter(optMs);
            optBw.Write(OptionalValues.Count);
            foreach (var kvp in OptionalValues)
            {
                optBw.Write(kvp.Key);
                kvp.Value.Write(optBw);
            }
            writer.Write((int)optMs.Length);
            writer.Write(optMs.GetBuffer(), 0, (int)optMs.Length);
        }

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

            NetId = reader.ReadInt32();
			Cell = reader.ReadInt32();
			Value = Variant.Read(reader);
			IsActive = reader.ReadBoolean();
			SyncerType = reader.ReadString();

            int optLen = reader.ReadInt32();
            byte[] optBlob = reader.ReadBytes(optLen);
            using var optBr = new BinaryReader(new MemoryStream(optBlob));
            int length = optBr.ReadInt32();
            OptionalValues = new Dictionary<string, Variant>(length);
            for (int i = 0; i < length; i++)
            {
                string key = optBr.ReadString();
                OptionalValues[key] = Variant.Read(optBr);
            }
        }

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost) return;

			// Handled by StructureStateSyncer on client
            if(NetworkIdentityRegistry.TryGet(NetId, out var identity))
            {
                var syncers = identity.GetComponents<StructureSyncerBase>();
                // An identity with no structure syncer swallows the whole
                // snapshot - battery charge, generator fuel, storage contents,
                // toilet fill, reactor state and the damage reconciliation with
                // it - and did so without a word.
                if (syncers == null || syncers.Length == 0)
                {
                    ThrottledLog.Warn(
                        $"[StructureState] '{identity.name}' has no structure syncer; its state cannot be applied");
                    return;
                }

                // To the syncer that sent it, not to every syncer on the object.
                //
                // This handed the packet to all of them, which is fine while a building
                // has one - and every building did, because a second syncer could not be
                // attached without breaking things. Attaching one proved why: most
                // machines have both a storage and an enable toggle, so the storage
                // syncer started receiving flag packets and reporting "Key: stor not
                // found" 149 to 229 times a run on a client that had been at zero errors.
                //
                // The constraint was never written down anywhere; it was discovered by
                // violating it. A name on the wire removes it, and the cost is one string
                // per structure packet.
                //
                // An empty SyncerType means a peer on an older build sent it. Those go to
                // everyone, exactly as before, so a mixed pair degrades to the old
                // behaviour rather than losing state outright.
                int delivered = 0;
                foreach (var syncer in syncers)
                {
                    if (!string.IsNullOrEmpty(SyncerType)
                        && syncer.GetType().Name != SyncerType) continue;

                    syncer.HandlePacket(this);
                    delivered++;
                }

                if (delivered == 0 && !string.IsNullOrEmpty(SyncerType))
                {
                    // The sender has a syncer this peer does not. Worth saying: it means
                    // the two builds disagree about which components a building carries,
                    // and the state silently goes nowhere.
                    ThrottledLog.Warn(
                        $"[StructureState] '{identity.name}' has no {SyncerType}; " +
                        "its state cannot be applied");
                }
                /*
                if(identity.TryGetComponent<StructureSyncerBase>(out var syncer))
                {
                    syncer.HandlePacket(this);
                }
                */
            }
		}

        public static bool VariantValueChanged(Variant a, Variant b, float epsilon = 0.01f)
        {
            if (a.Type != b.Type) return true;
            switch (a.Type)
            {
                case Variant.TypeCode.Float:
                    if (Mathf.Abs(a.Float - b.Float) > epsilon) return true;
                    break;
                case Variant.TypeCode.Int:
                    if (a.Int != b.Int) return true;
                    break;
                case Variant.TypeCode.Byte:
                    if (a.Byte != b.Byte) return true;
                    break;
                case Variant.TypeCode.String:
                    if (a.String != b.String) return true;
                    break;
                case Variant.TypeCode.Boolean:
                    if (a.Boolean != b.Boolean) return true;
                    break;
                case Variant.TypeCode.ByteArray:
                    if (!ByteArraysEqual(a.ByteArray, b.ByteArray)) return true;
                    break;
            }

            return false;
        }

        private static bool ByteArraysEqual(byte[] a, byte[] b)
        {
            if (a == b) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        public static bool OptionalValuesChanged(Dictionary<string, Variant> a, Dictionary<string, Variant> b)
        {
            if (a == null && b == null) return false;
            if (a == null || b == null) return true;
            if (a.Count != b.Count) return true;

            foreach (var kvp in a)
            {
                if (!b.TryGetValue(kvp.Key, out var bVal)) return true;
                if (VariantValueChanged(kvp.Value, bVal)) return true;
            }
            return false;
        }
    }
}
