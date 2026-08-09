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
        /// <summary>
        /// Orders already announced, keyed by what makes an order unique, with
        /// when they went out.
        ///
        /// TryBuild is called for every cell under the cursor on every frame of
        /// a drag, so without this the same order goes out dozens of times - ten
        /// insulated tiles produced 181 build packets in one live session, and
        /// the host carried out every one.
        ///
        /// Judged on time rather than on whether the cell changed. The first
        /// attempt at this compared Grid.Objects before and after the call, and
        /// the build order does not always appear in that slot by the time the
        /// Postfix runs - three of twenty one orders were silently never sent.
        /// A repeat inside this window cannot be a real second order either:
        /// the first one is still pending in that cell, and the game refuses a
        /// duplicate there itself.
        /// </summary>
        private static readonly Dictionary<string, float> _announced = new Dictionary<string, float>();
        private const float RepeatWindowSeconds = 1f;

        private static bool AlreadyAnnounced(string key)
        {
            float now = Time.unscaledTime;

            if (_announced.TryGetValue(key, out float last) && now - last < RepeatWindowSeconds)
                return true;

            _announced[key] = now;

            if (_announced.Count > 512)
            {
                var stale = new List<string>();
                foreach (var kvp in _announced)
                {
                    if (now - kvp.Value > RepeatWindowSeconds)
                        stale.Add(kvp.Key);
                }
                foreach (var k in stale)
                    _announced.Remove(k);
            }

            return false;
        }

        static void Prefix(BuildTool __instance, int cell)
        {
            using var _ = Profiler.Scope();

            try
            {
                var def = __instance.def;
                if (def != null)
                    DebugConsole.Log($"[BuildTool] Attempting to build: {def.PrefabID} at cell {cell}");
            }
            catch (Exception ex)
            {
                DebugConsole.LogError($"[BuildToolPatch.Prefix] {ex}");
            }
        }

        static void Postfix(BuildTool __instance, int cell)
        {
            using var _ = Profiler.Scope();

            try
            {
                if (!MultiplayerSession.InSession || __instance == null)
                    return;


                var def = __instance.def;
                var selectedElements = __instance.selectedElements;
                var orientation = __instance.GetBuildingOrientation;

                if (def == null || selectedElements == null)
                    return;

                if (AlreadyAnnounced($"{cell}|{def.PrefabID}|{(int)orientation}"))
                    return;

                // Says what this call announced, not what happens to sit in the
                // cell. Reading Grid.Objects reported "successfully placed" for
                // every repeat of a drag, because the first order had already
                // put something there - the log agreed with itself 181 times
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