using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Tools.Build;
using System;
using System.Collections.Generic;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.ToolPatches.Build
{
    [HarmonyPatch(typeof(BuildTool), nameof(BuildTool.TryBuild))]
    public static class BuildToolPatch
    {
        static void Prefix(BuildTool __instance, int cell)
        {
            using var _ = Profiler.Scope();

            try
            {
                var def = __instance.def;
                if (def != null)
                {
                    DebugConsole.Log($"[BuildTool] Attempting to build: {def.PrefabID} at cell {cell}");
                }
            }
            catch (Exception ex)
            {
                DebugConsole.LogError($"[BuildToolPatch.Prefix] {ex}");
            }
        }

        static void Postfix(BuildTool __instance, int cell, bool __result)
        {
            using var _ = Profiler.Scope();

            try
            {
                if (!MultiplayerSession.InSession || __instance == null)
                    return;

                // Only tell the other peers about a build that actually
                // happened here. TryBuild is called for every cell under the
                // cursor on every frame of a drag and returns false once a cell
                // already holds an order - this ignored that and sent anyway.
                // Ten insulated tiles became 181 build packets in one live
                // session, eighteen orders per cell, all of which the host
                // dutifully carried out.
                if (!__result)
                    return;

                var def = __instance.def;
                var selectedElements = __instance.selectedElements;
                var orientation = __instance.GetBuildingOrientation;

                if (def == null || selectedElements == null)
                    return;

                // Reports what this call did, not what happens to be in the
                // cell. Reading Grid.Objects said "successfully placed" for
                // every repeat of a drag, because the first order had already
                // put something there - so the log agreed with itself 181 times
                // about ten tiles and hid the duplication completely.
                GameObject obj = Grid.Objects[cell, (int) def.ObjectLayer];
                DebugConsole.Log(obj != null
                    ? $"[BuildTool] Placed {def.PrefabID} at cell {cell}"
                    : $"[BuildTool] Placed intention/ghost for {def.PrefabID} at cell {cell}");

                bool instantBuild = DebugHandler.InstantBuildMode || (Game.Instance.SandboxModeActive && SandboxToolParameterMenu.instance.settings.InstantBuild);
                var packet = new BuildPacket(
                    def.PrefabID,
                    cell,
                    orientation,
                    selectedElements,
                    def.ObjectLayer,
                    instantBuild
                );

                PacketSender.SendToAllOtherPeers(packet);
            }
            catch (Exception ex)
            {
                DebugConsole.LogError($"[BuildToolPatch.Postfix] {ex}");
            }
        }
    }
}