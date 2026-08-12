using System;
using System.Collections.Generic;
using UnityEngine;
using ImGuiNET;
using System.Linq;
using Shared.Profiling;

namespace ONI_Together.DebugTools
{
    public class DebugConsole
    {
        private static DebugConsole _instance;
        private static readonly List<LogEntry> logEntries = new List<LogEntry>();
        private static readonly object _lock = new object();

        private Vector2 scrollPos;
        private bool autoScroll = true;
        private bool collapseDuplicates = false;
        private string filter = "";

        private const int MaxLines = 300;
        private bool showConsole = false;

        private class LogEntry
        {
            public string message;
            public string stack;
            public LogType type;
            public bool expanded;
            public int count = 1;
        }

        public enum LogType
        {
            Error,
            Assert,
            Warning,
            Log,
            Exception,
            Success,
            NonImportant
        }

        public static DebugConsole Init()
        {
            using var _ = Profiler.Scope();

            if (_instance != null)
                return _instance;

            _instance = new DebugConsole();
            return _instance;
        }

        public static void Log(string message)
        {
            using var _ = Profiler.Scope();

            Debug.Log($"[ONI_Together] {message}");
            EnsureInstance();
            _instance.AddLog(message, "", LogType.Log);
        }

        public static void LogWarning(string message)
        {
            using var _ = Profiler.Scope();

            Debug.LogWarning($"[ONI_Together] {message}");
            EnsureInstance();
            _instance.AddLog(message, "", LogType.Warning);
        }

        public static void LogError(string message, bool trigger_error_screen = false)
        {
            using var _ = Profiler.Scope();

            // Marked when a test caused it, so a log reader can tell the two apart.
            //
            // The handler sweep hands every packet an address that resolves to
            // nothing, and some handlers report that as an error - three per run, with
            // text like "[SecureTransfer] Packet 0 CORRUPTED" and "Failed to spawn".
            // The run gate counts errors in the log, and it counted these: two whole
            // soak batches reported "errors=3" on both peers for failures the game
            // never had. Filtering on the test name did not work because these lines
            // carry the packet's tag, not the test's.
            //
            // A marker in the line itself is the only thing a later grep can rely on.
            string tag = Networking.NetworkIdentityRegistry.InDiagnosticScope ? "[test-scope] " : "";

            if (trigger_error_screen)
                Debug.LogError($"[ONI_Together] {tag}{message}");
			else //put it in the log file but don't trigger the error screen
				Debug.LogWarning($"-[ERROR] [ONI_Together] {tag}{message}");

			EnsureInstance();
            _instance.AddLog(message, "", LogType.Error);
        }

        public static void LogErrorTriggerInGameScreen(string message)
        {
            LogError(message, true);
        }

        /// <summary>
        /// Unity errors and exceptions seen since load, counted so a test can assert
        /// that a path produced none.
        ///
        /// Klei raises asserts and exceptions from inside its own code while the
        /// method that triggered them returns normally, so "did it throw" is not the
        /// same question as "did it work". Both printing-pod crashes were invisible
        /// to the first test and obvious to this counter.
        /// </summary>
        public static int UnityErrorCount { get; private set; }

        /// <summary>
        /// Errors raised while a test was deliberately provoking them, counted apart
        /// from the ones gameplay caused.
        ///
        /// The handler sweep feeds every packet an unresolvable target on purpose, and
        /// some of those make ONI log an error - "Could not find Tech:
        /// packet-robustness-probe" is the sweep's own probe string coming back. Left
        /// in one counter, the run reported nine errors that gameplay never produced
        /// and the health gate failed on its own test suite.
        ///
        /// Third time this shape of mistake has appeared here: a test polluting the
        /// number it is measured by. The split belongs at the counter, not in each
        /// test's arithmetic.
        /// </summary>
        public static int UnityErrorsInTests { get; private set; }

        public static void NoteUnityError()
        {
            if (Networking.NetworkIdentityRegistry.InDiagnosticScope) UnityErrorsInTests++;
            else UnityErrorCount++;
        }

        public static void LogException(Exception ex)
        {
            using var _ = Profiler.Scope();

            Debug.LogException(ex);
            EnsureInstance();
            _instance.AddLog(ex.Message, ex.StackTrace, LogType.Exception);
        }

        public static void LogAssert(string message)
        {
            using var _ = Profiler.Scope();

            Debug.Log($"[ONI_Together/Assert] {message}");
            EnsureInstance();
            _instance.AddLog(message, "", LogType.Assert);
        }

        public static void LogSuccess(string message)
        {
            using var _ = Profiler.Scope();

            Debug.Log($"[ONI_Together] {message}");
            EnsureInstance();
            _instance.AddLog(message, "", LogType.Success);
        }

        public static void LogNonImportant(string message)
        {
            using var _ = Profiler.Scope();

            Debug.Log($"[ONI_Together] {message}");
            EnsureInstance();
            _instance.AddLog(message, "", LogType.NonImportant);
        }

        private static void EnsureInstance()
        {
            using var _ = Profiler.Scope();

            _instance = new DebugConsole();
        }

        private void AddLog(string message, string stack, LogType type)
        {
            using var _ = Profiler.Scope();

            lock (_lock)
            {
                if (collapseDuplicates && logEntries.Count > 0)
                {
                    var last = logEntries[logEntries.Count - 1];
                    if (last.message == message && last.type == type)
                    {
                        last.count++;
                        return;
                    }
                }

                logEntries.Add(new LogEntry
                {
                    message = message,
                    stack = stack,
                    type = type,
                    expanded = false
                });

                if (logEntries.Count > MaxLines)
                    logEntries.RemoveAt(0);
            }
        }

        /// <summary>
        /// Toggles visibility of the ImGui console window.
        /// </summary>
        public void Toggle()
        {
            using var _ = Profiler.Scope();

            showConsole = !showConsole;
        }

        /// <summary>
        /// Draws the ImGui window for the debug console.
        /// Call this from your DevTool.RenderTo() or ImGui render loop.
        /// </summary>
        public void ShowWindow()
        {
            using var _ = Profiler.Scope();

            if (!showConsole)
                return;

            if (ImGui.Begin("Multiplayer Console", ref showConsole, ImGuiWindowFlags.MenuBar))
            {
                ShowConsoleContent(true);
            }

            ImGui.End();
        }

        public void ShowInTab()
        {
            using var _ = Profiler.Scope();

            ShowConsoleContent(false);
        }

        private void ShowConsoleContent(bool usesMenuBar)
        {
            using var _ = Profiler.Scope();

            // Toolbar
            if (usesMenuBar)
            {
                if (ImGui.BeginMenuBar())
                {
                    if (ImGui.Button("Clear"))
                    {
                        lock (_lock) { logEntries.Clear(); }
                    }
                    ImGui.SameLine();
                    ImGui.InputText("Filter", ref filter, 128);

                    ImGui.EndMenuBar();
                }
            }
            else
            {
                if (ImGui.Button("Clear"))
                {
                    lock (_lock) { logEntries.Clear(); }
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(200);
                ImGui.InputText("Filter", ref filter, 128);
            }

                ImGui.Separator();

                // Scroll region
                ImGui.BeginChild("ConsoleScroll", new Vector2(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar);

                lock (_lock)
                {
                    foreach (var entry in logEntries)
                    {
                        if (!string.IsNullOrEmpty(filter) && entry.message.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        Vector4 color = new Vector4(1f, 1f, 1f, 1f);
                        switch (entry.type)
                        {
                            case LogType.Warning:
                                color = new Vector4(1f, 1f, 0.3f, 1f);
                                break;
                            case LogType.Error:
                                color = new Vector4(1f, 0.4f, 0.4f, 1f);
                                break;
                            case LogType.Assert:
                                color = new Vector4(0.8f, 0.5f, 1f, 1f);
                                break;
                            case LogType.Exception:
                                color = new Vector4(1f, 0.4f, 0.4f, 1f);
                                break;
                            case LogType.Success:
                                color = new Vector4(0f, 1f, 0f, 1f);
                                break;
                            case LogType.NonImportant:
                                color = new Vector4(0.5f, 0.5f, 0.5f, 1.0f);
                                break;
                            default:
                                break;
                        }

                        string displayMsg = entry.count > 1 ? $"{entry.message} (x{entry.count})" : entry.message;

                        ImGui.TextColored(color, displayMsg);
                    }
                }

                if (autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                    ImGui.SetScrollHereY(1.0f);

                ImGui.EndChild();
        }
    }
}