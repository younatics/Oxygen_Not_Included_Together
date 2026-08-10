using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ONI_Together.Networking;
using UnityEngine;

namespace ONI_Together.DebugTools
{
    /// <summary>
    /// Drives a test scenario from a command file so a two-box run needs nobody
    /// at either keyboard.
    ///
    /// Everything a scenario needs already exists as a plain call - DebugMenu's
    /// buttons do nothing more than this - but it was only reachable by clicking
    /// ImGui, which meant every verification cycle cost a human two colonies, a
    /// host, a join and a dig. That is the real bottleneck in fixing these bugs,
    /// not the fixes.
    ///
    /// This lives on a DontDestroyOnLoad object rather than in Game.Update
    /// because the first command of a run is usually "load", and Game does not
    /// exist at the main menu.
    ///
    /// Drop commands, one per line, at %TEMP%\oni_together_cmd:
    ///     load &lt;save name or full path&gt;
    ///     host-lan &lt;ip&gt; &lt;port&gt;
    ///     join-lan &lt;ip&gt; &lt;port&gt;
    ///     stop-net
    ///     runtests [category ...]
    ///     status
    /// Results are logged as [SCENARIO] records next to the [TEST] ones.
    /// </summary>
    public class ScenarioRunner : MonoBehaviour
    {
        public const string CommandFileName = "oni_together_cmd";
        public const string Tag = "[SCENARIO]";

        private static ScenarioRunner _instance;
        private float _nextPoll;
        private int _pendingPlayReport = -1;
        private int _playAttempts;

        /// <summary>Roughly five seconds at 60 fps - long enough to outlast a join hard sync.</summary>
        private const int PlayAttemptLimit = 300;

        public static string CommandPath => Path.Combine(Path.GetTempPath(), CommandFileName);

        public static void Install()
        {
            if (_instance != null) return;

            var go = new GameObject("ONI_Together_ScenarioRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<ScenarioRunner>();
            DebugConsole.Log($"{Tag} installed; watching {CommandPath}");
        }

        private void Update()
        {
            // Unpausing does not stick on the first attempt: something re-pauses
            // right after a client joins - the hard sync on join pauses the host
            // - so a single Unpause races it and loses. Keep asking for a few
            // seconds, and report the value a frame after it finally takes.
            if (_pendingPlayReport >= 0)
            {
                var screen = SpeedControlScreen.Instance;
                bool paused = screen == null || screen.IsPaused;

                if (paused && _playAttempts < PlayAttemptLimit)
                {
                    _playAttempts++;
                    if (screen != null)
                    {
                        screen.SetSpeed(_pendingPlayReport);
                        screen.Unpause(false);
                    }
                }
                else
                {
                    DebugConsole.Log(
                        $"{Tag} {(paused ? "FAIL" : "OK")} play :: speed={_pendingPlayReport} " +
                        $"paused={paused} attempts={_playAttempts}");
                    _pendingPlayReport = -1;
                    _playAttempts = 0;
                }
            }

            // Twice a second. The commands are human-scale actions, not per-frame work.
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.5f;

            string path = CommandPath;
            string[] lines;
            try
            {
                if (!File.Exists(path)) return;

                // Opened so a concurrent writer does not lock us out.
                //
                // File.ReadAllLines asks for exclusive-ish access, and the writer
                // here is another process entirely - the peer agent, dropping a
                // command in. When the two met, every poll failed with "Sharing
                // violation" and kept failing, so the client sat at the main menu
                // ignoring every command it was sent and the run died at the join
                // step with "peer never reached an in-session state". Nothing was
                // wrong with the session; the two processes were fighting over one
                // small text file.
                var read = new List<string>();
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        read.Add(line);
                }
                lines = read.ToArray();

                // Delete before executing: a command that throws must not be
                // retried forever, and "load" tears down the scene under us.
                File.Delete(path);
            }
            catch (Exception ex)
            {
                DebugConsole.Log($"{Tag} command read failed: {ex.Message}");
                return;
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try { Execute(line); }
                catch (Exception ex)
                {
                    DebugConsole.Log($"{Tag} FAIL {line} :: {ex.Message.Replace('\n', ' ')}");
                }
            }
        }

        private void Execute(string line)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = parts[0].ToLowerInvariant();

            switch (verb)
            {
                case "status":
                    DebugConsole.Log($"{Tag} OK status :: {Describe()}");
                    break;

                case "load":
                {
                    if (parts.Length < 2) throw new ArgumentException("load needs a save name or path");
                    string arg = string.Join(" ", parts.Skip(1));
                    string file = ResolveSave(arg);
                    if (file == null) throw new FileNotFoundException($"no save matching '{arg}'");
                    DebugConsole.Log($"{Tag} OK load :: {file}");
                    LoadScreen.DoLoad(file);
                    break;
                }

                case "host-lan":
                {
                    string ip = parts.Length > 1 ? parts[1] : Configuration.Instance.Host.LanSettings.Ip;
                    int port = parts.Length > 2 ? int.Parse(parts[2]) : Configuration.Instance.Host.LanSettings.Port;

                    Configuration.Instance.Host.LanSettings.Ip = ip;
                    Configuration.Instance.Host.LanSettings.Port = port;
                    Configuration.Instance.Host.NetworkTransport = (int)NetworkConfig.NetworkTransport.RIPTIDE;
                    NetworkConfig.UpdateTransport(NetworkConfig.NetworkTransport.RIPTIDE);
                    Configuration.Instance.Save();

                    NetworkConfig.StartServer();
                    DebugConsole.Log($"{Tag} OK host-lan :: {ip}:{port}");
                    break;
                }

                case "join-lan":
                {
                    if (parts.Length < 3) throw new ArgumentException("join-lan needs <ip> <port>");
                    string ip = parts[1];
                    int port = int.Parse(parts[2]);

                    Configuration.Instance.Client.LanSettings.Ip = ip;
                    Configuration.Instance.Client.LanSettings.Port = port;
                    Configuration.Instance.Save();

                    NetworkConfig.UpdateTransport(NetworkConfig.NetworkTransport.RIPTIDE);
                    GameClient.ConnectToHost(ip: ip, port: port);
                    DebugConsole.Log($"{Tag} OK join-lan :: {ip}:{port}");
                    break;
                }

                case "stop-net":
                    NetworkConfig.Stop();
                    DebugConsole.Log($"{Tag} OK stop-net");
                    break;

                case "play":
                {
                    // A scenario that never leaves pause proves only that
                    // entities spawn and replicate. Nothing is mined, no chore
                    // runs, no duplicant moves - which is most of what can
                    // actually desync.
                    int speed = parts.Length > 1 ? int.Parse(parts[1]) : 1;
                    if (SpeedControlScreen.Instance == null) throw new InvalidOperationException("no speed control (not in a game?)");
                    SpeedControlScreen.Instance.SetSpeed(speed);
                    SpeedControlScreen.Instance.Unpause(false);

                    // IsPaused read in the same frame still returns the old
                    // value, so this used to report paused=True right after
                    // unpausing and every run looked like it had never started.
                    _pendingPlayReport = speed;
                    break;
                }

                case "pause":
                {
                    if (SpeedControlScreen.Instance == null) throw new InvalidOperationException("no speed control");
                    SpeedControlScreen.Instance.Pause(false);
                    DebugConsole.Log($"{Tag} OK pause :: paused={SpeedControlScreen.Instance.IsPaused}");
                    break;
                }

                case "dig":
                {
                    int count = parts.Length > 1 ? int.Parse(parts[1]) : 6;
                    int placed = Dig(count);
                    DebugConsole.Log($"{Tag} OK dig :: placed {placed} of {count}");
                    break;
                }

                case "packets":
                    PacketTracker.DumpCounts();
                    DebugConsole.Log($"{Tag} OK packets");
                    break;

                case "runtests":
                {
                    var categories = parts.Skip(1).ToArray();
                    UnitTestRunner.RunAndLog("scenario", categories.Length > 0 ? categories : null);
                    DebugConsole.Log($"{Tag} OK runtests");
                    break;
                }

                default:
                    throw new ArgumentException($"unknown command '{verb}'");
            }
        }

        /// <summary>
        /// Mark cells for digging, the way the client's reconcile path does:
        /// instantiate DigPlacer directly rather than going through DigTool,
        /// which would fire the client-to-host patches and make the scenario
        /// part of what it is measuring.
        ///
        /// Digging is what makes a run produce freshly spawned Diggables, which
        /// is the only way to exercise the NetId path - a save restores its
        /// serialized ids and never calls the hash.
        ///
        /// Cells are picked by scanning outward from the middle of the world in
        /// a fixed order, so two runs of the same scenario dig the same cells.
        /// </summary>
        private static int Dig(int count)
        {
            if (Game.Instance == null) throw new InvalidOperationException("no game loaded");

            var prefab = Assets.GetPrefab("DigPlacer");
            if (prefab == null) throw new InvalidOperationException("DigPlacer prefab not found");

            var already = new HashSet<int>();
            foreach (var d in global::Components.Diggables.Items)
            {
                if (d != null) already.Add(Grid.PosToCell(d));
            }

            // Start from a duplicant, not the middle of the map. Cells scanned
            // from the world centre are usually walled off, so the order was
            // placed somewhere nobody could reach and nothing was ever mined -
            // the marker replicated and the dig never happened.
            int origin = -1;
            foreach (var minion in global::Components.LiveMinionIdentities.Items)
            {
                if (minion == null) continue;
                int c = Grid.PosToCell(minion);
                if (Grid.IsValidCell(c)) { origin = c; break; }
            }
            if (origin < 0) origin = Grid.XYToCell(Grid.WidthInCells / 2, Grid.HeightInCells / 2);

            Grid.CellToXY(origin, out int ox, out int oy);
            int placed = 0;

            // Breadth-first over open cells, marking the solid faces they touch.
            // A solid cell next to open space is one a duplicant can stand beside
            // and therefore actually dig.
            var seen = new HashSet<int> { origin };
            var frontier = new Queue<int>();
            frontier.Enqueue(origin);

            while (frontier.Count > 0 && placed < count)
            {
                int cell = frontier.Dequeue();
                Grid.CellToXY(cell, out int x, out int y);

                foreach (var n in new[] { Grid.CellLeft(cell), Grid.CellRight(cell), Grid.CellAbove(cell), Grid.CellBelow(cell) })
                {
                    if (!Grid.IsValidCell(n) || !seen.Add(n)) continue;

                    if (Grid.Solid[n])
                    {
                        if (already.Contains(n)) continue;
                        // Skip near-undiggable rock; a duplicant would stand
                        // beside it forever and the scenario would never mine.
                        if (Grid.Element[n].hardness >= 150) continue;

                        if (MultiplayerSession.IsClient)
                        {
                            // A client asks; it does not decide. Instantiating
                            // here would skip the intent path entirely, which is
                            // the half of the protocol a one-sided scenario never
                            // exercises - and both peers issuing orders is the
                            // case that actually has to work.
                            PacketSender.SendToAllOtherPeers(
                                new Networking.Packets.Tools.Dig.DiggablePacket(n, 0));
                        }
                        else
                        {
                            var go = Util.KInstantiate(prefab, Grid.CellToPosCBC(n, Grid.SceneLayer.Move));
                            go.SetActive(true);
                        }

                        already.Add(n);
                        placed++;
                        if (placed >= count) break;
                    }
                    else
                    {
                        frontier.Enqueue(n);
                    }
                }
            }

            DebugConsole.Log($"{Tag} dig origin cell {origin} ({ox},{oy})");
            return placed;
        }

        /// <summary>Accept a full path, or match a colony/save name under save_files.</summary>
        private static string ResolveSave(string arg)
        {
            if (File.Exists(arg)) return arg;

            // Both roots: with Steam Cloud on, saves live under cloud_save_files
            // and GetSavePrefixAndCreateFolder points at the local one, which is
            // then empty. Looking in only one is how "load <name>" came back as
            // "no save matching" for a save that plainly existed.
            var roots = new List<string>();
            try { roots.Add(SaveLoader.GetSavePrefixAndCreateFolder()); } catch { }
            string docs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Klei", "OxygenNotIncluded");
            roots.Add(Path.Combine(docs, "save_files"));
            roots.Add(Path.Combine(docs, "cloud_save_files"));

            var candidates = new List<string>();
            foreach (var r in roots)
            {
                if (string.IsNullOrEmpty(r) || !Directory.Exists(r)) continue;
                candidates.AddRange(Directory.GetFiles(r, "*.sav", SearchOption.AllDirectories));
            }
            if (candidates.Count == 0) return null;

            // Newest match wins: a scenario usually wants the save it just made.
            return candidates
                .Where(f => Path.GetFileNameWithoutExtension(f).IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0
                         || (Path.GetDirectoryName(f) ?? "").IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
        }

        private static string Describe()
        {
            string role = MultiplayerSession.IsHost ? "host"
                        : MultiplayerSession.IsClient ? "client"
                        : "none";
            return $"role={role} transport={NetworkConfig.transport} insession={MultiplayerSession.InSession} " +
                   $"registry={NetworkIdentityRegistry.Count} lookupfails={NetworkIdentityRegistry.LookupFailCount} " +
                   $"game={(Game.Instance != null)}";
        }
    }
}
