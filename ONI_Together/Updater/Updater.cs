using System;
using System.IO;
using System.Text.RegularExpressions;
using ONI_Together.DebugTools;
using Shared.Helpers;
using Shared.Profiling;
using Steamworks;
using UnityEngine;
using YamlDotNet.RepresentationModel;
using Version = System.Version;

namespace ONI_Together.ModUpdater
{
    public static class Updater
    {
        private static readonly PublishedFileId_t WORKSHOP_ID = new PublishedFileId_t(3630759126);
        public static string CURRENT_VERSION { get; private set; }
        public static string WORKSHOP_VERSION { get; private set; }

        public static void CheckForUpdate()
        {
            using var _ = Profiler.Scope();

            CURRENT_VERSION = GetVersion();

            if (!SteamManager.Initialized)
            {
                DebugConsole.LogError("[Updater] Steam is not initialized.", false);
                return;
            }

            UGCQueryHandle_t queryHandle = SteamUGC.CreateQueryUGCDetailsRequest(new PublishedFileId_t[] { WORKSHOP_ID }, 1);
            SteamUGC.SetReturnLongDescription(queryHandle, true);
            SteamAPICall_t apiCall = SteamUGC.SendQueryUGCRequest(queryHandle);
            CallResult<SteamUGCQueryCompleted_t> result = CallResult<SteamUGCQueryCompleted_t>.Create(OnUGCQueryCompleted);
            result.Set(apiCall);

            DebugConsole.Log("[Updater] Sent workshop query for update check.");
        }

        private static void OnUGCQueryCompleted(SteamUGCQueryCompleted_t data, bool bIOFailure)
        {
            using var _ = Profiler.Scope();

            if (bIOFailure || data.m_eResult != EResult.k_EResultOK)
            {
                DebugConsole.LogError("[Updater] Workshop query failed!", false);
                return;
            }

            SteamUGCDetails_t details;
            bool ok = SteamUGC.GetQueryUGCResult(data.m_handle, 0, out details);

            if (!ok)
            {
                DebugConsole.LogError("[Updater] Failed to get UGC result.", false);
                return;
            }

            System.DateTime workshopUpdated = System.DateTimeOffset.FromUnixTimeSeconds(details.m_rtimeUpdated).UtcDateTime;

            DebugConsole.Log($"[Updater] Workshop last updated at {workshopUpdated.ToLocalTime()}");
            WORKSHOP_VERSION = GetWorkshopVersion(details.m_rgchDescription);
            CompareLocalModVersion(details.m_nPublishedFileId.m_PublishedFileId, workshopUpdated);
        }

        private static void CompareLocalModVersion(ulong fileId, System.DateTime workshopUpdated)
        {
            using var _ = Profiler.Scope();

            string SteamPath = Path.Combine(KMod.Manager.GetDirectory(), "Steam");
            string localPath = Path.Combine(SteamPath, fileId.ToString());
#if DEBUG
            SteamPath = Path.Combine(KMod.Manager.GetDirectory(), "dev"); // Goto the dev folder instead
            localPath = Path.Combine(SteamPath, "ONI_Together_dev"); // Use the dev build instead
#endif
            if (!Directory.Exists(localPath))
            {
                // Just in case
                Debug.LogWarning("[Updater] Local mod folder not found. It may not be installed.");
                return;
            }

            System.DateTime localModTime = Directory.GetLastWriteTimeUtc(localPath);

            Version localVer = new Version(CURRENT_VERSION);
            Version workshopVer = new Version(WORKSHOP_VERSION);

            // Workshop has been update since the last time we updated the mod
            if (workshopUpdated > localModTime)
            {
                DebugConsole.Log("[Updater] Update available based on workshop timestamp!");
                OnUpdateAvailable();
            }
            // The mod has attempted to update since the workshop was updated but the versions are still mismatched (in theory these should ALWAYS be less then the workshop version)
            else if (localVer < workshopVer)
            {
                DebugConsole.Log("[Updater] Version mismatch found! Local: " + localVer + " | Workshop: " + workshopVer);
                OnUpdateAvailable();
            }
            else
            {
                DebugConsole.Log("[Updater] Mod is up to date.");
            }
        }

        public static string GetVersion()
        {
            using var _ = Profiler.Scope();

            try
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(Updater).Assembly.Location), "mod_info.yaml");
                if (!File.Exists(path))
                {
                    Debug.LogWarning("[Updater] mod_info.yaml not found.");
                    return "Unknown";
                }

                var yaml = new YamlStream();
                using (var reader = new StreamReader(path))
                {
                    yaml.Load(reader);
                    var root = (YamlMappingNode)yaml.Documents[0].RootNode;
                    var versionNode = root.Children[new YamlScalarNode("version")];

                    string rawVersion = versionNode.ToString();

                    var match = Regex.Match(
                        rawVersion,
                        @"(?<ver>\d+(\.\d+){1,2})"
                    );

                    string version = match.Success ? match.Groups["ver"].Value : rawVersion;
                    DebugConsole.Log($"[Updater] Current Version: {version}");
                    return version;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Updater] Failed to load mod version: {e}");
                return "Unknown";
            }
        }

        public static string GetWorkshopVersion(string description)
        {
            using var _ = Profiler.Scope();

            if (string.IsNullOrEmpty(description))
                return "Unknown";

            var match = Regex.Match(
                description,
                @"Current Stage:\s*v(?<version>[0-9]+(\.[0-9]+)*)",
                RegexOptions.IgnoreCase
            );

            if (!match.Success)
            {
                DebugConsole.LogWarning("[Updater] Failed to parse workshop version.");
                return "Unknown";
            }

            string version = match.Groups["version"].Value;

            DebugConsole.Log($"[Updater] Workshop Version: {version}");
            return version;
        }

        /// <summary>
        /// Says that this install is behind the Workshop, in the log.
        ///
        /// It used to do nothing at all. The body was a dialog call commented out - it
        /// names STRINGS.UI.MP_SCREEN.UPDATER entries that do not exist, so it could not
        /// have compiled - beside a URL variable pointing at Workshop item 2018291283,
        /// which is a different mod entirely, assigned and never read. The compiler said
        /// as much on every build: "mod_updater_workshop_url assigned but never used".
        ///
        /// So the whole update check ran, decided this install was out of date, and told
        /// nobody. That is worse than not checking, because the counters and the log both
        /// suggest the question is handled.
        ///
        /// A dialog is deliberately not being rebuilt here. Steam updates a subscribed
        /// mod on its own, so the case a player actually hits is not "you should update"
        /// but "you and your friend are on different builds" - and that is now answered
        /// where it matters, in the connection handshake, which refuses a mod version
        /// mismatch and names both builds when the versions match and the binaries do
        /// not.
        /// </summary>
        public static void OnUpdateAvailable()
        {
            using var _ = Profiler.Scope();

            DebugConsole.LogWarning(
                $"[Updater] this install is behind the Workshop - local {CURRENT_VERSION}, " +
                $"Workshop {WORKSHOP_VERSION}. Steam updates a subscribed mod by itself; if " +
                "this persists the mod is probably installed from somewhere else. Joining a " +
                "peer on the newer version will be refused at the handshake.");
        }
    }
}