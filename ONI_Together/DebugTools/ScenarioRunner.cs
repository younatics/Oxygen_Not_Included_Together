using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
// GetAmounts is an extension in this namespace - the same one VitalStatsSyncer uses.
using Klei.AI;
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

        /// <summary>Write time plus content of the last command batch acted on, so the
        /// same batch is never executed twice even if the file survives.</summary>
        private string _lastConsumed;

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

                // Consume by identity, not by deletion.
                //
                // The delete used to sit inside this try, ahead of execution, so
                // a writer holding the file - the peer agent reads it back after
                // writing to report what it dropped - threw a sharing violation
                // *after* a perfectly good read. The commands were never
                // executed, the file was still there, and the next poll did the
                // same thing: hundreds of "command read failed: Sharing
                // violation" half a second apart while the client ignored every
                // command it was sent.
                //
                // Recording what was consumed makes the delete an optimisation
                // rather than the mechanism, so losing it costs nothing and
                // cannot cause a repeat either. Repeats are not benign: these
                // commands are not idempotent and a second join-lan strands the
                // client for good.
                string fingerprint = File.GetLastWriteTimeUtc(path).Ticks + ":" + string.Join("\n", lines);
                if (fingerprint == _lastConsumed) return;
                _lastConsumed = fingerprint;

                try { File.Delete(path); }
                catch (Exception ex)
                {
                    DebugConsole.Log($"{Tag} could not remove the command file, continuing anyway " +
                                     $"(it is recorded as consumed): {ex.Message}");
                }
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

                case "build":
                {
                    int count = parts.Length > 1 ? int.Parse(parts[1]) : 4;
                    // "build 4 Tile" - a tile's scaffold sits on a different layer from
                    // a ladder's, which is the case the leftover sweep exists for.
                    string what = parts.Length > 2 ? parts[2] : "Ladder";
                    int placed = Build(count, what);
                    DebugConsole.Log($"{Tag} OK build :: placed {placed} of {count}");
                    break;
                }

                case "deconstruct":
                {
                    int count = parts.Length > 1 ? int.Parse(parts[1]) : 2;
                    int marked = Deconstruct(count);
                    DebugConsole.Log($"{Tag} OK deconstruct :: marked {marked} of {count}");
                    break;
                }

                case "hatch":
                {
                    int count = parts.Length > 1 ? int.Parse(parts[1]) : 3;
                    int forced = ForceHatch(count);
                    DebugConsole.Log($"{Tag} OK hatch :: brought {forced} of {count} eggs to term");
                    break;
                }

                case "fabricate":
                {
                    int count = parts.Length > 1 ? int.Parse(parts[1]) : 2;
                    int asked = ForceFabricate(count);
                    DebugConsole.Log($"{Tag} OK fabricate :: asked {asked} fabricator(s) for a product");
                    break;
                }

                case "finishbuild":
                {
                    int count = parts.Length > 1 ? int.Parse(parts[1]) : 4;
                    int done = ForceFinishConstruction(count);
                    DebugConsole.Log($"{Tag} OK finishbuild :: completed {done} of {count} sites");
                    break;
                }

                case "damage":
                {
                    int count = parts.Length > 1 ? int.Parse(parts[1]) : 3;
                    int hit = DamageBuildings(count);
                    DebugConsole.Log($"{Tag} OK damage :: damaged {hit} of {count} repairable building(s)");
                    break;
                }

                case "hp":
                {
                    // The cross-peer answer, dumped for comparison rather than judged
                    // here. Whether the client agrees about a building's hit points is
                    // not something one peer can decide, and the damage test on each box
                    // can only say "everything damaged here has an address".
                    int reported = DumpDamagedBuildings();
                    DebugConsole.Log($"{Tag} OK hp :: dumped {reported} damaged building(s)");
                    break;
                }

                case "storage":
                {
                    int reported = DumpStorageContents();
                    DebugConsole.Log($"{Tag} OK storage :: dumped {reported} stored item(s)");
                    break;
                }

                case "coverage":
                {
                    int reported = DumpSyncCoverage();
                    DebugConsole.Log($"{Tag} OK coverage :: reported {reported} building prefab(s)");
                    break;
                }

                case "api":
                {
                    if (parts.Length < 2) throw new ArgumentException("api needs <TypeName>");
                    int found = DumpTypeMembers(parts[1]);
                    DebugConsole.Log($"{Tag} OK api :: {parts[1]} has {found} member(s)");
                    break;
                }

                case "state":
                {
                    int rows = DumpGameState();
                    DebugConsole.Log($"{Tag} OK state :: dumped {rows} row(s)");
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
		/// <summary>
		/// Order some ladders built, the way a player does.
		///
		/// Digging was the only action a run ever took, so a whole half of the
		/// protocol went unmeasured: an order that creates a building site, the site
		/// becoming a building, and both peers agreeing about which is which. The
		/// building-versus-site collision that rehoused 694 tiles in one session was
		/// found by reading a live log, not by any run.
		///
		/// Ladders on purpose. They go in open cells, need no foundation and cost
		/// little, so a run places its order where a duplicant can actually reach it
		/// - the same lesson the dig scenario learned when it marked cells nobody
		/// could stand beside and mined nothing for an hour.
		///
		/// Both peers call TryPlace, which is what the build tool itself calls. A
		/// client is not special-cased here: whether its order reaches the host, and
		/// whether it draws a site the host never named, is the question - and
		/// answering it by not asking is how it stayed unanswered.
		/// </summary>
		private static int Build(int count, string defName = "Ladder")
		{
			if (Game.Instance == null) throw new InvalidOperationException("no game loaded");

			// The building to place. Tiles on purpose when asked for, because a tile's
			// scaffold sits on a different object layer from a ladder's - and the
			// leftover-scaffold sweep exists for exactly that mismatch, which a run
			// that only ever builds ladders can never reach.
			var def = Assets.GetBuildingDef(defName);
			if (def == null) throw new InvalidOperationException($"{defName} building def not found");

			int origin = FindDuplicantCell();
			var material = new List<Tag> { SimHashes.SandStone.CreateTag() };

			var seen = new HashSet<int> { origin };
			var frontier = new Queue<int>();
			frontier.Enqueue(origin);
			int placed = 0;

			while (frontier.Count > 0 && placed < count)
			{
				int cell = frontier.Dequeue();

				foreach (var n in new[] { Grid.CellLeft(cell), Grid.CellRight(cell), Grid.CellAbove(cell), Grid.CellBelow(cell) })
				{
					if (!Grid.IsValidCell(n) || !seen.Add(n)) continue;
					if (Grid.Solid[n]) continue;

					frontier.Enqueue(n);
					if (placed >= count) break;

					// Nothing already standing there - placing over an existing
					// building produces a refusal, not an order, and the run would
					// report a build it never made.
					if (Grid.Objects[n, (int)ObjectLayer.Building] != null) continue;

					// A visualizer and a facade, because that is what TryPlace needs.
					//
					// Passing null for the source object threw a NullReferenceException
					// on the first cell and the whole run died before it collected a
					// single log. The mod's own remote-build handler does it this way -
					// Util.KInstantiate of the preview, then TryPlace with
					// "DEFAULT_FACADE" - and that path is exercised every time a player
					// builds, so it is the one to copy rather than invent a shorter one.
					// Step by step, because two guesses at this have both been wrong.
					//
					// "Object reference not set" names no line, and reading the code to
					// pick the likely null produced a fix that did not work and then a
					// second one that did not either. Each step says what it is about to
					// do, so the next run names the null instead of me nominating one.
					Vector3 pos = Grid.CellToPosCBC(n, Grid.SceneLayer.Building);

					if (def.BuildingPreview == null)
					{
						DebugConsole.LogWarning($"{Tag} build step: '{def.PrefabID}' has no BuildingPreview");
						continue;
					}

					DebugConsole.Log($"{Tag} build step: instantiating preview for cell {n}");
					GameObject visualizer = Util.KInstantiate(def.BuildingPreview, pos);

					DebugConsole.Log($"{Tag} build step: TryPlace at cell {n}");
					var site = def.TryPlace(visualizer, pos, Orientation.Neutral, material, "DEFAULT_FACADE");
					if (site == null)
					{
						DebugConsole.Log($"{Tag} build step: TryPlace refused cell {n}");
						continue;
					}

					// Which member actually carries the id, measured rather than assumed.
					//
					// The host's four orders reached the client and all four were
					// refused with "Unknown building def: " and nothing after the colon,
					// so def.PrefabID went over the wire empty. BuildToolPatch reads the
					// same member, which means live play sends the same empty id and the
					// failure is only hidden by other packets filling the building in
					// later.
					//
					// Printed once per run: guessing which member is populated is how the
					// last three attempts at this bug went.
					if (!_loggedDefId)
					{
						_loggedDefId = true;
						DebugConsole.Log(
							$"{Tag} build def members: PrefabID='{def.PrefabID}' name='{def.name}' " +
							$"tag='{def.Tag}' type={def.GetType().Name}");
					}

					DebugConsole.Log($"{Tag} build step: announcing cell {n}");

					// Announced as well as placed, because that is what the build tool
					// does and placing alone is not what a player does.
					//
					// The first version called TryPlace and stopped there, which skips
					// BuildTool.TryBuild - and the announcement lives in that method's
					// Postfix, not in TryPlace. So the run built sites neither peer told
					// the other about, and the counts came out two low when the host
					// built and five high when the client did. Read as a replication
					// bug that was entirely the scenario's own doing, in both
					// directions at once.
					//
					// A scenario that takes a shortcut the game does not take measures
					// the shortcut.
					PacketSender.SendToAllOtherPeers(new Networking.Packets.Tools.Build.BuildPacket(
						def.PrefabID,
						n,
						Orientation.Neutral,
						material,
						def.ObjectLayer,
						instantBuild: false));

					placed++;
					DebugConsole.Log($"{Tag} build site at cell {n}");
				}
			}

			return placed;
		}

		/// <summary>
		/// Mark a few finished buildings for deconstruction.
		///
		/// The other end of the same gap. Removal replication was fixed once and the
		/// evidence was a live log again: five buildings the client had destroyed and
		/// the host still had. A run that never deconstructs cannot notice that
		/// coming back.
		///
		/// Ladders only - the ones this scenario built. Tearing down whatever the
		/// colony happens to contain would make two runs incomparable and could
		/// remove something the duplicants need.
		/// </summary>
		private static int Deconstruct(int count)
		{
			if (Game.Instance == null) throw new InvalidOperationException("no game loaded");

			int marked = 0;
			foreach (var building in UnityEngine.Object.FindObjectsByType<Deconstructable>(FindObjectsSortMode.None))
			{
				if (marked >= count) break;
				if (building.IsNullOrDestroyed()) continue;
				if (building.gameObject.PrefabID().Name != "Ladder") continue;
				if (building.IsMarkedForDeconstruction()) continue;

				building.QueueDeconstruction();
				marked++;
				DebugConsole.Log($"{Tag} deconstruct queued at cell {Grid.PosToCell(building)}");
			}

			return marked;
		}

		/// <summary>So the def-member dump happens once, not once per cell.</summary>
		private static bool _loggedDefId;

		/// <summary>
		/// Bring eggs to term so hatching actually happens inside a run.
		///
		/// The client-side hatch block has reported zero on every run since it shipped,
		/// and the patch-attachment test has since proved the hook is on the method - so
		/// the zero means incubation simply never finishes in 150 seconds of wall clock.
		/// Eggs take cycles. Waiting for one is not a test, it is a hope.
		///
		/// Filling the incubation amount makes it deterministic: the eggs on hand hatch
		/// within seconds, on both peers, and the counter either moves or the block does
		/// not work. Nothing else about the colony is touched.
		///
		/// Wrapped, and reporting what it actually did, because this reaches into game
		/// state by name and a wrong name here would otherwise look exactly like "no
		/// eggs present" - which is the failure mode this whole exercise is about.
		/// </summary>
		private static int ForceHatch(int count)
		{
			if (Game.Instance == null) throw new InvalidOperationException("no game loaded");

			var incubation = Db.Get().Amounts.Incubation;
			if (incubation == null)
			{
				DebugConsole.LogWarning($"{Tag} hatch: no Incubation amount in the database");
				return 0;
			}

			int forced = 0;
			int seen = 0;

			// Found through KPrefabID, because a state-machine instance is not a Unity
			// object and cannot be searched for directly - and its amounts live on
			// Modifiers, not on the GameObject. Both of those I guessed wrong first,
			// which is the same "assume the API name" mistake this project keeps paying
			// for; the compiler caught it here only because it is C# rather than a log.
			foreach (var prefab in UnityEngine.Object.FindObjectsByType<KPrefabID>(FindObjectsSortMode.None))
			{
				if (forced >= count) break;
				if (prefab.IsNullOrDestroyed() || prefab.gameObject.IsNullOrDestroyed()) continue;

				var smi = prefab.gameObject.GetSMI<IncubationMonitor.Instance>();
				if (smi == null) continue;

				seen++;
				try
				{
					// GetAmounts, the same accessor VitalStatsSyncer uses. Copied from a
					// working call site instead of guessed at - the two names I invented
					// before this both failed to compile.
					var amounts = prefab.gameObject.GetAmounts();
					var value = amounts?.Get(incubation);
					if (value == null) continue;

					value.SetValue(value.GetMax());
					forced++;
					DebugConsole.Log($"{Tag} hatch: '{prefab.PrefabTag.Name}' brought to term");
				}
				catch (Exception ex)
				{
					DebugConsole.LogWarning($"{Tag} hatch: could not force '{prefab.name}': {ex.GetType().Name}");
				}
			}

			if (seen == 0)
				DebugConsole.LogWarning($"{Tag} hatch: this colony has no eggs, so the hatch block cannot be exercised");

			return forced;
		}

		/// <summary>
		/// Ask a fabricator for a product, on whichever peer this runs on.
		///
		/// The client-side product block has reported zero since it shipped, and the
		/// attachment test says the hook is on the method - so the call never happens.
		/// It cannot happen by itself either: a fabricator needs a duplicant to work it
		/// and the client's AI is switched off, so nothing on that peer will ever run an
		/// order to completion on its own.
		///
		/// Calling it directly is the honest way to exercise the guard. On a host this
		/// produces and announces exactly as a finished order would; on a client the
		/// patch should refuse it and the counter should move. Either the guard works or
		/// this run says it does not.
		/// </summary>
		private static int ForceFabricate(int count)
		{
			if (Game.Instance == null) throw new InvalidOperationException("no game loaded");

			int asked = 0;
			foreach (var fabricator in UnityEngine.Object.FindObjectsByType<ComplexFabricator>(FindObjectsSortMode.None))
			{
				if (asked >= count) break;
				if (fabricator.IsNullOrDestroyed()) continue;

				var recipes = fabricator.recipe_list;
				if (recipes == null || recipes.Length == 0) continue;

				try
				{
					fabricator.SpawnOrderProduct(recipes[0]);
					asked++;
					DebugConsole.Log(
						$"{Tag} fabricate: asked '{fabricator.gameObject.PrefabID()}' for '{recipes[0].id}'");
				}
				catch (Exception ex)
				{
					DebugConsole.LogWarning(
						$"{Tag} fabricate: '{fabricator.gameObject.name}' threw {ex.GetType().Name}");
				}
			}

			if (asked == 0)
				DebugConsole.LogWarning($"{Tag} fabricate: no fabricator with a recipe in this colony");

			return asked;
		}

		/// <summary>
		/// Finish the construction sites standing around, so completion actually happens.
		///
		/// The leftover-scaffold sweep runs when a client applies BuildCompletePacket,
		/// and a run produced zero of those: duplicants do not finish a tile in 150
		/// seconds of wall clock, so no completion was ever sent. The counter read zero
		/// for want of the event, exactly as the egg block did.
		///
		/// FinishConstruction is the method the mod's own completion patch hangs off
		/// (ConstructablePatch), so calling it drives the real path - the same packet,
		/// with the same fields, from the same place. Nothing is simulated or faked here;
		/// the work is simply not waited for.
		/// </summary>
		private static int ForceFinishConstruction(int count)
		{
			if (Game.Instance == null) throw new InvalidOperationException("no game loaded");

			int done = 0;
			int seen = 0;

			foreach (var site in UnityEngine.Object.FindObjectsByType<Constructable>(FindObjectsSortMode.None))
			{
				if (done >= count) break;
				if (site.IsNullOrDestroyed() || site.gameObject.IsNullOrDestroyed()) continue;

				seen++;
				try
				{
					// A temperature, because skipping delivery leaves it at zero.
					//
					// In a real build the material a duplicant carries sets this. Calling
					// FinishConstruction directly skips that, and ONI logs "TileComplete
					// has a temperature of zero which has always been an error in my
					// experience" for every one - four a run, exactly the number of sites
					// forced, all within the same millisecond.
					//
					// Those four were being reported as unexplained host errors for many
					// runs, and they were this tool. The fourth time in this project that
					// the measurement was the thing being measured, which is why the
					// error gate already excludes the test suite's own provoked failures.
					//
					// Accessor copied from BuildCompletePacket.cs:148 rather than guessed.
					// Room temperature where the site itself does not say otherwise.
					if (site.initialTemperature <= 0f)
					{
						float ambient = site.TryGetComponent<PrimaryElement>(out var sitePe)
							&& !sitePe.IsNullOrDestroyed() && sitePe.Temperature > 0f
							? sitePe.Temperature
							: 293.15f;
						site.initialTemperature = ambient;
					}

					// Signature read off the compiler rather than guessed:
					// FinishConstruction(UtilityConnections, WorkerBase). No connections
					// and no worker - the mod's patch reads the building, not the
					// duplicant, and a fresh site has nothing wired to it yet.
					site.FinishConstruction((UtilityConnections)0, null);
					done++;
					DebugConsole.Log($"{Tag} finishbuild: '{site.gameObject.PrefabID()}' completed at cell {Grid.PosToCell(site.gameObject)}");
				}
				catch (Exception ex)
				{
					DebugConsole.LogWarning(
						$"{Tag} finishbuild: '{site.gameObject.name}' threw {ex.GetType().Name}");
				}
			}

			if (seen == 0)
				DebugConsole.LogWarning($"{Tag} finishbuild: no construction sites standing, so completion cannot be exercised");

			return done;
		}

		/// <summary>
		/// Damage buildings on the host so repair becomes reachable at all.
		///
		/// Wire repair not showing on the client was reported from play and has never
		/// once been reproduced here, for a simple reason: nothing in the scenario ever
		/// damages anything. The damage test therefore passed every run by finding zero
		/// damaged buildings - the same vacuous pass that hid the egg block and the
		/// fabricator block for three runs each.
		///
		/// Damaged buildings are what create the repair chore, and the repair chore is
		/// what makes a duplicant work a RepairableStorageProxy - the object [RefusedAsk]
		/// named, 3229 times on the host and never once on the client. So this is the
		/// step that lets that asymmetry be measured instead of argued about.
		///
		/// Wires first, because that is what was reported. Conduits next, then anything
		/// else with hit points, so a colony without damaged wire still exercises the
		/// path.
		///
		/// The trigger is copied from BuildingDamagePacket.cs:243 rather than written
		/// from memory - BoxingTrigger with a DamageSourceInfo, which is the only form
		/// the game's handler accepts. A plain Trigger delivers a payload the handler
		/// cannot read, which is a mistake this file's history already contains.
		/// </summary>
		private static int DamageBuildings(int count)
		{
			if (Game.Instance == null) throw new InvalidOperationException("no game loaded");
			if (!MultiplayerSession.IsHost)
			{
				DebugConsole.LogWarning($"{Tag} damage: only the host may damage buildings - the client's damage is suppressed by design");
				return 0;
			}

			var candidates = new List<BuildingHP>();
			foreach (var hp in UnityEngine.Object.FindObjectsByType<BuildingHP>(
						 FindObjectsInactive.Exclude, FindObjectsSortMode.None))
			{
				if (hp.IsNullOrDestroyed() || hp.gameObject.IsNullOrDestroyed()) continue;
				if (hp.HitPoints <= 1) continue;
				if (!hp.gameObject.TryGetComponent<Repairable>(out var repairable) || repairable.IsNullOrDestroyed())
					continue;
				candidates.Add(hp);
			}

			// Wire, then conduit, then the rest. Ordered rather than filtered, so the
			// step still does something in a colony with no damaged-wire candidates.
			candidates = candidates
				.OrderByDescending(hp => hp.gameObject.GetComponent<Wire>() != null)
				.ThenByDescending(hp => hp.gameObject.GetComponent<Conduit>() != null)
				.ToList();

			int damaged = 0;
			foreach (var hp in candidates)
			{
				if (damaged >= count) break;

				// A third of its health, so it is clearly damaged and clearly not
				// destroyed. Destroying it would replace the object and exercise a
				// different path.
				int amount = Math.Max(1, hp.MaxHitPoints / 3);

				hp.gameObject.BoxingTrigger((int)GameHashes.DoBuildingDamage, new BuildingHP.DamageSourceInfo
				{
					damage = amount,
					source = "ScenarioRunner",
					popString = string.Empty,
				});

				damaged++;
				DebugConsole.Log(
					$"{Tag} damage: '{hp.gameObject.PrefabID()}' at cell {Grid.PosToCell(hp.gameObject)} " +
					$"took {amount}, now {hp.HitPoints}/{hp.MaxHitPoints}");
			}

			if (candidates.Count == 0)
				DebugConsole.LogWarning($"{Tag} damage: nothing repairable in this colony, so repair cannot be exercised");

			return damaged;
		}

		/// <summary>
		/// Dump every damaged building as an [HP] record, for the two peers to be
		/// compared line by line.
		///
		/// Keyed on prefab and cell, not on NetId, and deliberately: the question is
		/// whether the two peers agree about a building's health, and if the ids
		/// disagree then keying on them would hide exactly the case worth seeing. A
		/// building does not move, so its cell is a stable key - which is not true of
		/// the pickupables the NetId dump has to handle.
		///
		/// The NetId is printed as well, so a disagreement can be read either way:
		/// same cell and different health is a replication failure, same cell and
		/// different id is why.
		/// </summary>
		private static int DumpDamagedBuildings()
		{
			if (Game.Instance == null) throw new InvalidOperationException("no game loaded");

			int reported = 0;
			var rows = new List<string>();

			foreach (var hp in UnityEngine.Object.FindObjectsByType<BuildingHP>(
						 FindObjectsInactive.Exclude, FindObjectsSortMode.None))
			{
				if (hp.IsNullOrDestroyed() || hp.gameObject.IsNullOrDestroyed()) continue;
				if (hp.HitPoints >= hp.MaxHitPoints) continue;

				var identity = hp.gameObject.GetExistingNetIdentity();
				int netId = identity == null ? 0 : identity.NetId;

				rows.Add($"[HP] {hp.gameObject.PrefabID()}|{Grid.PosToCell(hp.gameObject)}|" +
						 $"{hp.HitPoints}/{hp.MaxHitPoints}|{netId}");
				reported++;
			}

			// Sorted so two dumps line up without the comparer having to sort.
			rows.Sort(StringComparer.Ordinal);
			foreach (var row in rows) DebugConsole.Log(row);

			if (reported == 0)
				DebugConsole.LogWarning($"{Tag} hp: nothing damaged on this peer - a comparison of two empty lists proves nothing");

			return reported;
		}

		/// <summary>
		/// Dump what is inside every container, as [STORE] records, for the two peers
		/// to be compared.
		///
		/// This is the last big disagreement, and it took a wrong turn to find. The
		/// host holds 108 to 121 ids the client has never heard of, and the whole thing
		/// was attributed to gas and liquid physics - both peers making their own
		/// falling chunks. A verdict breakdown killed that: of the registered objects
		/// carrying an element, exactly one to three were loose ephemeral matter, while
		/// 193 on the host and 139 on the client were in-storage. Bottled and tanked,
		/// correctly identified, and 54 of them known to one peer only. That difference
		/// is the same size as the disagreement.
		///
		/// So the question is no longer "do the peers agree about a gas cloud" but "do
		/// the peers agree about what is inside this container", and nothing in this
		/// harness could ask it.
		///
		/// Keyed on the container's prefab and cell, and the item's prefab. Not on the
		/// NetId, because the ids disagreeing is one of the answers this is meant to
		/// distinguish from the contents disagreeing - and keying on the thing under
		/// test would hide it.
		///
		/// Mass is rounded to a tenth of a kilogram. Two peers simulating the same
		/// storage will not agree on the twelfth decimal place, and reporting that as a
		/// divergence is how the building-count comparison wasted three rounds.
		/// </summary>
		private static int DumpStorageContents()
		{
			if (Game.Instance == null) throw new InvalidOperationException("no game loaded");

			int reported = 0;
			var rows = new List<string>();

			foreach (var storage in UnityEngine.Object.FindObjectsByType<Storage>(
						 FindObjectsInactive.Exclude, FindObjectsSortMode.None))
			{
				if (storage.IsNullOrDestroyed() || storage.gameObject.IsNullOrDestroyed()) continue;
				if (storage.items == null) continue;

				int containerCell = Grid.PosToCell(storage.gameObject);
				if (!Grid.IsValidCell(containerCell)) continue;

				string container = $"{storage.gameObject.PrefabID()}@{containerCell}";

				// What kind of container this is, so the inclusion rule is decided by
				// data. The 57 host-only entries were machine buffers holding 0 kg for
				// part of a tick, and the rule that excludes them keys on conduit
				// components - so those components, and whether a syncer was attached,
				// are printed rather than assumed.
				var go = storage.gameObject;
				string kind = string.Concat(
					go.TryGetComponent<Building>(out var b) && !b.IsNullOrDestroyed() ? "B" : "-",
					go.TryGetComponent<ConduitConsumer>(out var cc) && !cc.IsNullOrDestroyed() ? "C" : "-",
					go.TryGetComponent<Conduit>(out var cd) && !cd.IsNullOrDestroyed() ? "P" : "-",
					go.TryGetComponent<Networking.Components.StructureStateSyncers.StorageStateSyncer>(out var ss)
						&& !ss.IsNullOrDestroyed() ? "S" : "-");

				// Grouped by item prefab, not listed per object.
				//
				// The stored-item id is a hash of item prefab, container prefab,
				// container cell and workable type - and nothing else. Two Dirt in one
				// bin therefore hash to the same value and the second is placed by the
				// free-slot walk, which depends on arrival order and so on this peer
				// only. That makes the count per prefab the number that decides whether
				// the ids can agree at all, which is why it is what gets reported.
				var byPrefab = new Dictionary<string, int>();
				var massByPrefab = new Dictionary<string, float>();

				foreach (var item in storage.items)
				{
					if (item.IsNullOrDestroyed()) continue;

					string prefab = item.PrefabID().ToString();
					byPrefab.TryGetValue(prefab, out int n);
					byPrefab[prefab] = n + 1;

					float mass = 0f;
					if (item.TryGetComponent<PrimaryElement>(out var pe) && !pe.IsNullOrDestroyed())
						mass = pe.Mass;
					massByPrefab.TryGetValue(prefab, out float m);
					massByPrefab[prefab] = m + mass;

					reported++;
				}

				foreach (var kv in byPrefab)
				{
					rows.Add($"[STORE] {container}|{kv.Key}|{kv.Value}|" +
							 $"{Math.Round(massByPrefab[kv.Key], 1)}|{kind}");
				}
			}

			rows.Sort(StringComparer.Ordinal);
			foreach (var row in rows) DebugConsole.Log(row);

			if (reported == 0)
				DebugConsole.LogWarning($"{Tag} storage: nothing stored anywhere on this peer - a comparison of two empty lists proves nothing");

			return reported;
		}

		/// <summary>
		/// For every building type in the colony, what state it carries and which
		/// syncers are watching it.
		///
		/// This exists because of how the storage gap was found. StorageStateSyncer was
		/// attached by four hand-written Harmony patches naming four game types, and
		/// every other container in the colony was simply not replicated - 96 of them
		/// held different masses on the two peers, including a SuitLocker 28 kg apart.
		/// Nothing in the code said which types were meant to be covered, so nothing
		/// could notice the ones that were not. It took a cross-peer log comparison.
		///
		/// The same shape applies to Battery, Generator, Toilet and Reactor: seven
		/// separate patches, each naming one type, and no list anywhere of what should
		/// be on the list. So before writing a policy that decides what to track, this
		/// prints what is actually there. A policy written from imagination is how a
		/// whole round went into "gas physics" for a divergence that turned out to be
		/// machine buffers.
		///
		/// One row per building prefab, not per instance. A colony has thousands of
		/// buildings and about a hundred kinds, and the decision is per kind.
		///
		/// Component names come from the objects themselves rather than a curated list.
		/// A curated list of "components that hold state" would be the same guess this
		/// is meant to replace.
		/// </summary>
		private static int DumpSyncCoverage()
		{
			if (Game.Instance == null) throw new InvalidOperationException("no game loaded");

			// prefab -> (syncers, other components), first instance wins. Buildings of
			// one kind carry the same components.
			var seen = new Dictionary<string, string>(StringComparer.Ordinal);

			foreach (var building in UnityEngine.Object.FindObjectsByType<Building>(
						 FindObjectsInactive.Exclude, FindObjectsSortMode.None))
			{
				if (building.IsNullOrDestroyed() || building.gameObject.IsNullOrDestroyed()) continue;

				string prefab = building.gameObject.PrefabID().ToString();
				if (seen.ContainsKey(prefab)) continue;

				var syncers = new List<string>();
				var carriers = new List<string>();

				foreach (var component in building.gameObject.GetComponents<KMonoBehaviour>())
				{
					if (component.IsNullOrDestroyed()) continue;
					string name = component.GetType().Name;

					// Ours, or the game's.
					if (component.GetType().Namespace != null
						&& component.GetType().Namespace.StartsWith("ONI_Together", StringComparison.Ordinal))
					{
						syncers.Add(name);
						continue;
					}
					carriers.Add(name);
				}

				syncers.Sort(StringComparer.Ordinal);
				carriers.Sort(StringComparer.Ordinal);

				seen[prefab] = $"{string.Join(",", syncers)}|{string.Join(",", carriers)}";
			}

			var rows = new List<string>();
			foreach (var kv in seen)
				rows.Add($"[COVERAGE] {kv.Key}|{kv.Value}");
			rows.Sort(StringComparer.Ordinal);
			foreach (var row in rows) DebugConsole.Log(row);

			return rows.Count;
		}

		/// <summary>
		/// Print a game type's fields, properties and methods.
		///
		/// The rule in this project is not to guess ONI API names, and the way it has
		/// been kept is to find a working call site and copy it. That fails when there is
		/// no call site: door control state, the building enable toggle and the manual
		/// delivery amount are all player-set state that only event patches touch, and
		/// none of them reads the current value - so there was nothing to copy, and the
		/// work stopped rather than guess.
		///
		/// Offline reflection was tried first and cannot work here: PowerShell 5.1 runs
		/// on a framework that refuses to load Assembly-CSharp, because the assembly uses
		/// default interface members. Asking the running game is the way in.
		///
		/// One round trip answers what three compile failures answered badly earlier
		/// today - IncubationMonitor.Instance, GetAmounts, Modifiers were each wrong
		/// before a Grep found the real form in this repository.
		/// </summary>
		private static int DumpTypeMembers(string typeName)
		{
			var type = System.Type.GetType(typeName + ", Assembly-CSharp")
					   ?? System.Type.GetType(typeName)
					   ?? HarmonyLib.AccessTools.TypeByName(typeName);

			if (type == null)
			{
				DebugConsole.LogWarning($"{Tag} api: no type named '{typeName}'");
				return 0;
			}

			const System.Reflection.BindingFlags any =
				System.Reflection.BindingFlags.Instance |
				System.Reflection.BindingFlags.Static |
				System.Reflection.BindingFlags.Public |
				System.Reflection.BindingFlags.NonPublic;

			int count = 0;

			foreach (var field in type.GetFields(any))
			{
				DebugConsole.Log($"[API] {type.Name}|field|{field.FieldType.Name} {field.Name}");
				count++;
			}
			foreach (var property in type.GetProperties(any))
			{
				DebugConsole.Log($"[API] {type.Name}|prop|{property.PropertyType.Name} {property.Name}" +
								 $"|{(property.CanRead ? "get" : "")}{(property.CanWrite ? "set" : "")}");
				count++;
			}
			foreach (var method in type.GetMethods(any))
			{
				if (method.DeclaringType != type) continue;
				var args = method.GetParameters();
				var names = new List<string>(args.Length);
				foreach (var arg in args) names.Add(arg.ParameterType.Name);
				DebugConsole.Log($"[API] {type.Name}|method|{method.ReturnType.Name} " +
								 $"{method.Name}({string.Join(",", names)})");
				count++;
			}
			foreach (var nested in type.GetNestedTypes(any))
			{
				DebugConsole.Log($"[API] {type.Name}|nested|{nested.Name}");
				count++;
			}

			return count;
		}

		/// <summary>
		/// One dump for every kind of game state worth comparing across peers.
		///
		/// Written because the alternative was becoming absurd. Damage got a dump and a
		/// comparer, container contents got a dump and a comparer, the id tables got a
		/// dump and a comparer - three of each, about a hundred lines apiece, and the
		/// list of things nobody was comparing was still long: research, recipe queues,
		/// the flags a player sets by clicking, duplicant vitals. Each new category meant
		/// another pair of files, so most categories never got one, and the two worst bugs
		/// of the day were in categories with no comparison at all.
		///
		/// So the shape is uniform: category|key|field|value, one row per fact. Adding a
		/// category is a few lines here and nothing at all on the comparison side, which
		/// is what makes "check everything" affordable instead of aspirational.
		///
		/// The key must identify the same thing on both peers without depending on
		/// anything under test. Cells for buildings, because buildings do not move, and
		/// names for duplicants - not NetIds, since whether the two peers agree about ids
		/// is one of the things being measured, and keying on it would hide exactly the
		/// case worth seeing.
		///
		/// Every accessor here is one this repository already uses or one the api verb was
		/// asked about. Categories whose accessors are not confirmed are listed as not
		/// covered at the end rather than guessed at - power grids, plant growth, critter
		/// age, conduit contents and per-building priorities are all still uncompared, and
		/// saying so is more useful than a dump that silently reports nothing.
		/// </summary>
		private static int DumpGameState()
		{
			if (Game.Instance == null) throw new InvalidOperationException("no game loaded");

			var rows = new List<string>();

			DumpResearchState(rows);
			DumpRecipeQueues(rows);
			DumpBuildingFlags(rows);
			DumpDuplicantVitals(rows);

			// GAMESTATE, not STATE. StateDivergenceTests already emits [STATE] rows with a
			// different schema - NetId first, then the syncer name - and a comparer reading
			// both saw NetIds where it expected category names. Tags are a namespace and
			// this one was taken; checking that before reusing it would have cost nothing.
			rows.Sort(StringComparer.Ordinal);
			foreach (var row in rows) DebugConsole.Log("[GAMESTATE] " + row);

			DebugConsole.LogWarning(
				"[GAMESTATE] not covered yet: power grid, plant growth, critter age, conduit " +
				"contents, per-building priority. Their accessors are unconfirmed - a dump " +
				"that guesses is worse than one that admits the gap.");

			return rows.Count;
		}

		/// <summary>
		/// Which techs are done, what is being researched, and how far in.
		///
		/// The client reported research half finished while the host had completed it.
		/// Nothing compared this, and the function that sends the completed set had no
		/// caller at all.
		/// </summary>
		private static void DumpResearchState(List<string> rows)
		{
			if (Research.Instance == null || Db.Get().Techs == null) return;

			foreach (var tech in Db.Get().Techs.resources)
			{
				if (tech == null) continue;
				var instance = Research.Instance.Get(tech);
				if (instance == null) continue;

				// IsComplete and GetTotalPercentageComplete were both read off the running
				// game with the api verb rather than assumed.
				rows.Add($"research|{tech.Id}|complete|{(instance.IsComplete() ? 1 : 0)}");

				// Percentage only while it is unfinished. A finished tech reports whatever
				// its inventory happens to hold and that is not a fact about agreement.
				if (!instance.IsComplete())
				{
					float pct = instance.GetTotalPercentageComplete();
					rows.Add($"research|{tech.Id}|percent|{Math.Round(pct, 2)}");
				}
			}

			var active = Research.Instance.GetActiveResearch();
			rows.Add($"research|_active|tech|{active?.tech?.Id ?? "none"}");
		}

		/// <summary>
		/// How many of each recipe every fabricator has queued.
		///
		/// A player queued iron on the client and the count read zero there while the host
		/// refined it. Both peers keep their own count, both consume it, and nothing
		/// reconciles them.
		/// </summary>
		private static void DumpRecipeQueues(List<string> rows)
		{
			foreach (var fabricator in UnityEngine.Object.FindObjectsByType<ComplexFabricator>(
						 FindObjectsInactive.Exclude, FindObjectsSortMode.None))
			{
				if (fabricator.IsNullOrDestroyed() || fabricator.gameObject.IsNullOrDestroyed()) continue;

				int cell = Grid.PosToCell(fabricator.gameObject);
				if (!Grid.IsValidCell(cell)) continue;

				string key = $"{fabricator.gameObject.PrefabID()}@{cell}";

				foreach (var recipe in fabricator.GetRecipes())
				{
					if (recipe == null) continue;
					int count = fabricator.GetRecipeQueueCount(recipe);

					// Only what is queued. Every fabricator knows every recipe it could
					// make, and rows for the ones nobody ordered would bury the ones
					// somebody did.
					if (count == 0) continue;
					rows.Add($"recipe|{key}|{recipe.id}|{count}");
				}
			}
		}

		/// <summary>
		/// The state a player sets by clicking: enabled, door control, delivery amount.
		///
		/// Synced by event patches only, with no way to look again if one is missed. The
		/// syncer written for this could not be attached - see BuildingFlagsSyncer - so
		/// this is currently the only thing that would notice a divergence.
		/// </summary>
		private static void DumpBuildingFlags(List<string> rows)
		{
			foreach (var button in UnityEngine.Object.FindObjectsByType<BuildingEnabledButton>(
						 FindObjectsInactive.Exclude, FindObjectsSortMode.None))
			{
				if (button.IsNullOrDestroyed() || button.gameObject.IsNullOrDestroyed()) continue;
				int cell = Grid.PosToCell(button.gameObject);
				if (!Grid.IsValidCell(cell)) continue;
				rows.Add($"flag|{button.gameObject.PrefabID()}@{cell}|enabled|{(button.IsEnabled ? 1 : 0)}");
			}

			foreach (var door in UnityEngine.Object.FindObjectsByType<Door>(
						 FindObjectsInactive.Exclude, FindObjectsSortMode.None))
			{
				if (door.IsNullOrDestroyed() || door.gameObject.IsNullOrDestroyed()) continue;
				int cell = Grid.PosToCell(door.gameObject);
				if (!Grid.IsValidCell(cell)) continue;
				rows.Add($"flag|{door.gameObject.PrefabID()}@{cell}|door|{door.CurrentState}");
			}

			foreach (var delivery in UnityEngine.Object.FindObjectsByType<ManualDeliveryKG>(
						 FindObjectsInactive.Exclude, FindObjectsSortMode.None))
			{
				if (delivery.IsNullOrDestroyed() || delivery.gameObject.IsNullOrDestroyed()) continue;
				int cell = Grid.PosToCell(delivery.gameObject);
				if (!Grid.IsValidCell(cell)) continue;
				string key = $"{delivery.gameObject.PrefabID()}@{cell}";
				rows.Add($"flag|{key}|capacity|{Math.Round(delivery.capacity, 1)}");
				rows.Add($"flag|{key}|paused|{(delivery.paused ? 1 : 0)}");
			}
		}

		/// <summary>
		/// Duplicant health, stress and calories.
		///
		/// Never compared. A duplicant starving on one peer and fine on the other is the
		/// kind of divergence that ends a colony, and the only reason to believe it does
		/// not happen is that nobody has looked.
		///
		/// Keyed by name rather than NetId, and rounded: two peers running the same
		/// simulation will not agree on the third decimal of a calorie.
		/// </summary>
		private static void DumpDuplicantVitals(List<string> rows)
		{
			var amounts = Db.Get().Amounts;
			if (amounts == null) return;

			foreach (var minion in UnityEngine.Object.FindObjectsByType<MinionIdentity>(
						 FindObjectsInactive.Exclude, FindObjectsSortMode.None))
			{
				if (minion.IsNullOrDestroyed() || minion.gameObject.IsNullOrDestroyed()) continue;

				// The same accessor VitalStatsSyncer uses, with using Klei.AI.
				var values = minion.gameObject.GetAmounts();
				if (values == null) continue;

				string key = minion.GetProperName();

				foreach (var pair in new[] {
					("hp", amounts.HitPoints),
					("calories", amounts.Calories),
					("stress", amounts.Stress),
					("stamina", amounts.Stamina),
				})
				{
					if (pair.Item2 == null) continue;
					var value = values.Get(pair.Item2);
					if (value == null) continue;
					rows.Add($"vital|{key}|{pair.Item1}|{Math.Round(value.value, 1)}");
				}
			}
		}

		/// <summary>Where a duplicant is standing, or the middle of the world.</summary>
		private static int FindDuplicantCell()
		{
			foreach (var minion in global::Components.LiveMinionIdentities.Items)
			{
				if (minion == null) continue;
				int c = Grid.PosToCell(minion);
				if (Grid.IsValidCell(c)) return c;
			}
			return Grid.XYToCell(Grid.WidthInCells / 2, Grid.HeightInCells / 2);
		}

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

            // An exact colony name wins outright, and a substring match is only a
            // last resort.
            //
            // This used to take whatever matched the name *anywhere* in the file or
            // folder path and then pick the newest. Autosaves make that dangerous:
            // the colony under test accumulates them, but so does every other colony,
            // and "newest wins" eventually points somewhere else entirely. The
            // investigation clone was asked for and a different colony's autosave -
            // the player's own save, which is not to be touched at all - was the
            // newest thing matching, so it was loaded and then autosaved over for
            // three cycles.
            //
            // Matching the colony folder exactly removes the ambiguity: a save named
            // for the colony can only be that colony's.
            var exact = candidates
                .Where(f => string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(f) ?? ""), arg, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f) ?? "") ?? ""),
                        arg, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
            if (exact != null) return exact;

            var loose = candidates
                .Where(f => Path.GetFileNameWithoutExtension(f).IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0
                         || (Path.GetDirectoryName(f) ?? "").IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();

            if (loose != null)
            {
                DebugConsole.LogWarning(
                    $"{Tag} no colony named exactly '{arg}'; falling back to '{loose}'. " +
                    "Check this is the intended save - a loose match can select a different colony.");
            }
            return loose;
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
