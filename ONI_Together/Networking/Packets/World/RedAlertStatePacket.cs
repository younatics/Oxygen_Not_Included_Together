using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ONI_Together.Networking.Packets.Architecture;
using static ONI_Together.Patches.World.MeterScreenPatches;
using static Operational;

namespace ONI_Together.Networking.Packets.World
{
    public class RedAlertStatePacket : IPacket
    {
        public int ActiveWorldID;
        public bool IsRedAlert;

        private WorldContainer activeWorld;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(ActiveWorldID);
            writer.Write(IsRedAlert);
        }

        public void Deserialize(BinaryReader reader)
        {
            ActiveWorldID = reader.ReadInt32();
            IsRedAlert = reader.ReadBoolean();

            // Resolved when the packet is applied, not while it is being read.
            // ClusterManager.Instance is null until a world exists, and a
            // joining client processes packets from the menu - so this threw
            // inside the read loop, where the activeWorld == null guard below
            // could never save it. A handler that throws costs one packet; a
            // deserializer that throws costs whatever came after it in the
            // stream.
        }

        public void OnDispatched()
        {
            if (MeterScreen_RedAlertPatch.IsSyncing) 
                return;

            if (ClusterManager.Instance == null)
                return;

            activeWorld = ClusterManager.Instance.GetWorld(ActiveWorldID);
            if(activeWorld == null)
                return;

            MeterScreen_RedAlertPatch.IsSyncing = true;
            try
            {
                activeWorld.AlertManager.ToggleRedAlert(IsRedAlert);
                if (ClusterManager.Instance.activeWorldId == ActiveWorldID)
                {
                    if (IsRedAlert) // Our active world is the same as the one that was toggled
                    {
                        KMonoBehaviour.PlaySound(GlobalAssets.GetSound("HUD_Click_Open"));
                    }
                    else
                    {
                        KMonoBehaviour.PlaySound(GlobalAssets.GetSound("HUD_Click_Close"));
                    }
                }
            }
            finally
            {
                MeterScreen_RedAlertPatch.IsSyncing = false;
            }
        }
    }
}
