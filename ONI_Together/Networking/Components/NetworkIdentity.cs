using KSerialization;
using ONI_Together.DebugTools;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	[SerializationConfig(MemberSerialization.OptIn)]
	public class NetworkIdentity : KMonoBehaviour
	{
		[Serialize]
		public int NetId = 0;

		[SkipSaveFileSerialization]
		private bool IsRegistered = false;

		/// <summary>
		/// Drawn on a client before the host has named it. Visible, not
		/// addressable, and not in the registry until OverrideNetId arrives.
		/// </summary>
		[SkipSaveFileSerialization]
		public bool IsClientPreview { get; private set; }

		/// <summary>
		/// A name this client gave itself, which the host's name replaces.
		///
		/// A client may take the id its hash implies, because a pure hash is the same
		/// number on both peers - that is what keeps a Repairable's storage proxy
		/// addressable when the host never announces it. But adoption only ever
		/// considered objects with no id at all:
		///
		///     WorldStateSyncer   (have.IsClientPreview || have.NetId == 0)
		///     TryAdoptPreview    if (candidate.NetId != 0) continue;
		///
		/// so giving those objects a hash quietly disqualified them from being named.
		/// Failed lookups on the client went from 5 to 3,752 in one build: the host was
		/// talking about objects the client was holding, under a different number,
		/// with no way left for the host's number to land.
		///
		/// Provisional says "this is mine until I hear otherwise", and the adoption
		/// paths accept it the same as no id at all. The host's name always wins,
		/// which is the rule the whole id layer is supposed to follow.
		/// </summary>
		[SkipSaveFileSerialization]
		public bool IdIsProvisional { get; private set; }

		/// <summary>
		/// How many previews were drawn and how many were later named by the
		/// host. A gap between them is the count of objects a client can see but
		/// cannot address, which is otherwise only visible as failed lookups
		/// somewhere else entirely.
		/// </summary>
		public static int PreviewsCreated { get; private set; }
		public static int PreviewsAdopted { get; private set; }

		/// <summary>
		/// Previews broken down by prefab. The totals said 38 previews for 20
		/// dig orders without saying what the other eighteen were, and the set
		/// of prefabs that actually need a network identity is what decides how
		/// expensive host-authoritative spawning would be.
		/// </summary>
		private static readonly Dictionary<string, int> _previewsByPrefab = new();

		/// <summary>
		/// Previews that were later named, and announcements the host sent, both by
		/// prefab.
		///
		/// The totals say a client drew 101 objects and 18 were named. They do not say
		/// which, and the two possible causes need opposite fixes: if the host never
		/// announced that prefab, the sender is the problem; if it announced and the
		/// client still holds an unnamed one, the matching is. Counting all three
		/// numbers per prefab is what tells them apart.
		/// </summary>
		private static readonly Dictionary<string, int> _previewsAdoptedByPrefab = new();
		private static readonly Dictionary<string, int> _announcedByPrefab = new();

		private static void Bump(Dictionary<string, int> table, string key)
		{
			table.TryGetValue(key, out int n);
			table[key] = n + 1;
		}

		/// <summary>
		/// One line naming the prefabs a client is holding without an address, with
		/// what the host said about each. Emitted next to the health row.
		/// </summary>
		public static string DescribePreviewGap(int count = 8)
		{
			// On a host there are no previews, so this used to print "none" and hide
			// the half of the answer only the host has.
			//
			// The question is whether an unnamed object on the client was ever
			// announced by the host, and the two halves live on different machines:
			// previews are client-side, announcements host-side. Printing only the
			// client half meant every prefab read "0announced" regardless of what the
			// host did - which looks like proof that nothing is announced, and is not.
			//
			// So the host reports what it announced, and the client what it is holding
			// unnamed. Two rows, one comparison.
			if (_previewsByPrefab.Count == 0)
			{
				if (_announcedByPrefab.Count == 0) return "none";

				var announced = new List<string>();
				foreach (var kvp in _announcedByPrefab)
					announced.Add($"{kvp.Key}:{kvp.Value}announced");
				announced.Sort((a, b) => string.CompareOrdinal(b, a));
				return string.Join(" ", announced.GetRange(0, System.Math.Min(count, announced.Count)));
			}

			var parts = new List<string>();
			foreach (var kvp in _previewsByPrefab)
			{
				_previewsAdoptedByPrefab.TryGetValue(kvp.Key, out int adopted);
				int stranded = kvp.Value - adopted;
				if (stranded <= 0) continue;

				_announcedByPrefab.TryGetValue(kvp.Key, out int announced);
				parts.Add($"{kvp.Key}:{stranded}unnamed/{kvp.Value}drawn/{announced}announced");
			}

			if (parts.Count == 0) return "none";
			parts.Sort((a, b) => string.CompareOrdinal(b, a));
			return string.Join(" ", parts.GetRange(0, System.Math.Min(count, parts.Count)));
		}

		/// <summary>
		/// Id reserved for the very next NetworkIdentity to spawn.
		///
		/// A handler that creates an object the host has already named used to
		/// let it mint its own id first and replace it a moment later. For that
		/// moment the object sat in the registry under a foreign id, which is a
		/// breakoff slot every other object in that cell then had to step over -
		/// an Iron pile came out differing from the host's by exactly 2. Claiming
		/// the id up front means it is registered once, under the right name.
		/// </summary>
		private static int _reservedNetId;

		/// <summary>Frame the reservation was made in. A reservation is for one spawn, now.</summary>
		private static int _reservedFrame = -1;

		/// <summary>
		/// This object's prefab name, for objects that may not have a prefab.
		///
		/// Plenty of things carry a NetworkIdentity and no KPrefabID -
		/// WorldSelectionCollider is one - and PrefabID() throws on those. It threw
		/// inside OnSpawn in a live session and the client shut itself down: three
		/// "Error in WorldSelectionCollider.NetworkIdentity.OnSpawn" lines and then
		/// Game.OnApplicationQuit, with no other cause anywhere in the log.
		///
		/// Used everywhere this class names an object, including the log lines. A
		/// diagnostic that crashes the game is worse than no diagnostic, and the
		/// rename logging runs on exactly the objects most likely to be missing a
		/// prefab id.
		/// </summary>
		private string SafePrefabName
		{
			get
			{
				if (gameObject.IsNullOrDestroyed()) return "?";
				if (TryGetComponent<KPrefabID>(out var kpid) && !kpid.IsNullOrDestroyed())
					return kpid.PrefabTag.Name;
				return gameObject.name;
			}
		}

		public static void ReserveNextNetId(int netId, string expectedPrefab = null)
		{
			_reservedNetId = netId;
			_reservedFrame = netId == 0 ? -1 : UnityEngine.Time.frameCount;
			_reservedFor = netId == 0 ? null : expectedPrefab;
		}

		/// <summary>
		/// What the reservation was made for, so a different object cannot take it.
		///
		/// The frame check stops a reservation being claimed a frame later. It does
		/// nothing about the same frame, and the same frame is where the damage was:
		/// three ids where the host held BasicPlantFood or BasicPlantBar and the client
		/// held a Creature pile, still there after the client stopped minting ids and
		/// stopped converging them. The client was not inventing those names - it was
		/// being handed them. SpawnResource merges into an existing pile and returns
		/// it without an OnSpawn, the reservation stays set, and whatever spawns next in
		/// that same frame collects an id the host meant for something else.
		///
		/// Null means "no expectation", which is how every caller that has not been
		/// taught to say behaves - the old behaviour, unchanged.
		/// </summary>
		private static string _reservedFor;

		/// <summary>Reservations refused to the wrong object. Zero is the goal.</summary>
		public static int ReservationsProtected { get; private set; }

		/// <summary>
		/// The reserved id, if it is still for this spawn.
		///
		/// A reservation left lying around is worse than none. SpawnResource does not
		/// always create an object - it merges the mass into a pile that is already
		/// there and hands that back - and on that path no OnSpawn runs, so the
		/// reservation stayed set. The next object to spawn took it, and because ONI
		/// pools pickupables that next object can be a recycled one that already had
		/// an id and a registry slot. It ended up with a field saying one id and a
		/// registry entry under another, which is the field-versus-key drift that
		/// three separate tests report as "two objects share a NetId".
		///
		/// Caught by attributing renames: in every failing run the object had been
		/// named once, by WorldDamageSpawnResourcePacket, and its field said something
		/// else afterwards with no second rename to explain it.
		///
		/// Frame-scoped, so a reservation that was not consumed by the spawn it was
		/// made for cannot be applied to an unrelated object later.
		/// </summary>
		private static int ConsumeReservation(string actualPrefab)
		{
			if (_reservedNetId == 0)
				return 0;

			// The right object, or nobody.
			//
			// Left in place rather than cleared: the object the id was meant for may
			// still be spawning in this frame, and taking the reservation away from it
			// would trade a wrong id for a missing one. The frame check below discards
			// it if that spawn never comes.
			if (_reservedFor != null && actualPrefab != _reservedFor)
			{
				ReservationsProtected++;
				DebugConsole.LogWarning(
					$"[NetworkIdentity] '{actualPrefab}' tried to take NetId {_reservedNetId}, which the " +
					$"host reserved for '{_reservedFor}' - refused, because one id meaning two different " +
					"things on the two peers is how packets end up addressed to the wrong object");
				return 0;
			}

			if (_reservedFrame != UnityEngine.Time.frameCount)
			{
				DebugConsole.LogWarning(
					$"[NetworkIdentity] discarding a stale reserved NetId {_reservedNetId} from frame " +
					$"{_reservedFrame} (now {UnityEngine.Time.frameCount}) - the spawn it was made for " +
					"never happened, and giving it to a different object is how two objects come to " +
					"share one id");
				_reservedNetId = 0;
				_reservedFrame = -1;
				return 0;
			}

			int id = _reservedNetId;
			_reservedNetId = 0;
			_reservedFrame = -1;
			return id;
		}

		/// <summary>
		/// Objects that only got an identity because somebody was about to send
		/// a packet about them, counted per prefab.
		///
		/// A lazily attached identity is one-sided by construction: the sender
		/// asks and gets one, the receiver holds the same object, never asks,
		/// and so has nothing to resolve the packet against. Anything appearing
		/// here should be attached at spawn on both peers instead.
		/// </summary>
		private static readonly Dictionary<string, int> _lazyIdentities = new Dictionary<string, int>();

		public static IReadOnlyDictionary<string, int> LazyIdentities => _lazyIdentities;

		public static void NoteLazyIdentity(GameObject go,
			[System.Runtime.CompilerServices.CallerMemberName] string caller = "",
			[System.Runtime.CompilerServices.CallerFilePath] string callerFile = "")
		{
			// Grouped by kind, not by instance. Unity names a clone
			// "LadderPreview(193236)_visualizer", so counting raw names produced
			// a list of one-offs and hid that they were all the same thing.
			string prefab = StripInstanceId(go == null ? "?" : go.name);

			// Who asked, and what the object looked like when they did.
			//
			// Three egg prefabs still turn up here - PacuEgg, DreckoEgg,
			// PacuTropicalEgg, one each in three runs out of ten - even though
			// CreatureSpawnPatch attaches an identity to anything whose prefab tag
			// ends in "Egg" at KPrefabID.OnSpawn. So either that spawn hook never ran
			// for these objects or the tag was not what it looks like, and the count
			// alone cannot tell those apart.
			//
			// Attributing the caller is what cracked the id-collision drift earlier
			// today, after five rounds of reading code had failed. Same tool.
			if (!go.IsNullOrDestroyed())
			{
				string tag = go.TryGetComponent<KPrefabID>(out var kpid) && kpid != null
					? kpid.PrefabTag.Name
					: "<no KPrefabID>";
				DebugConsole.LogWarning(
					$"[LazyIdentity] '{prefab}'#{go.GetInstanceID()} had no NetworkIdentity when " +
					$"{System.IO.Path.GetFileNameWithoutExtension(callerFile)}.{caller} asked for its id " +
					$"(prefabTag='{tag}', active={go.activeInHierarchy}, " +
					$"cell={(Grid.WidthInCells == 0 ? -1 : Grid.PosToCell(go))}). " +
					"It should have been attached at spawn on both peers.");
			}
			_lazyIdentities.TryGetValue(prefab, out int n);
			_lazyIdentities[prefab] = n + 1;

			// Once per prefab, not once per object - the point is which kinds of
			// thing are missing a spawn-time identity, not how many there were.
			if (n == 0)
			{
				DebugConsole.LogWarning(
					$"[NetworkIdentity] '{prefab}' had no identity until a packet needed one; " +
					"the other peer will not have given it the same address");
			}
		}

		public static void ClearLazyIdentities() => _lazyIdentities.Clear();

		/// <summary>
		/// Was this kind of thing addressed before its spawn hook ran?
		///
		/// The spawn hooks need this to tell two situations apart that look identical to
		/// them. An object that already has an identity may have got it the ordinary way,
		/// in which case both peers agree and moving it is churn - or from the lazy path
		/// moments earlier, in which case the number came from whenever something first
		/// asked and the other peer cannot reproduce it.
		/// </summary>
		internal static bool WasAddressedBeforeSpawn(GameObject go)
		{
			if (go.IsNullOrDestroyed()) return false;
			string prefab = go.TryGetComponent<KPrefabID>(out var kpid) && kpid != null
				? kpid.PrefabTag.Name
				: go.name;
			return !string.IsNullOrEmpty(prefab) && _lazyIdentities.ContainsKey(prefab);
		}

		/// <summary>
		/// Take one prefab back off the lazily-attached list, because its id has since
		/// been converged onto the value both peers compute.
		///
		/// Counted per prefab rather than per object, like the recording it undoes, so
		/// this removes the kind once the kind has been repaired. If another object of
		/// the same prefab is still one-sided the next ask puts it straight back.
		/// </summary>
		internal static void ForgetLazyAttachment(NetworkIdentity identity)
		{
			if (identity.IsNullOrDestroyed() || identity.gameObject.IsNullOrDestroyed()) return;

			string prefab = identity.gameObject.TryGetComponent<KPrefabID>(out var kpid) && kpid != null
				? kpid.PrefabTag.Name
				: identity.gameObject.name;
			if (string.IsNullOrEmpty(prefab)) return;

			if (_lazyIdentities.Remove(prefab)) LazyAttachmentsRepaired++;
		}

		/// <summary>
		/// Prefabs taken off the lazily-attached list because convergence gave them the
		/// id both peers compute. Non-zero beside a zero lazy count means the warning was
		/// raised and then answered, not that it never happened.
		/// </summary>
		public static int LazyAttachmentsRepaired { get; private set; }

		/// <summary>
		/// A reservation that was claimed but never consumed is the dangerous
		/// one: the next identity to spawn - in the next session, belonging to
		/// something else entirely - takes the id that was set aside for an
		/// object that never arrived.
		/// </summary>
		public static void ResetForNewSession()
		{
			_reservedNetId = 0;
			ClearLazyIdentities();
			ResetPreviewCounters();
		}

		private static string StripInstanceId(string name)
		{
			int open = name.IndexOf('(');
			if (open < 0) return name;
			int close = name.IndexOf(')', open);
			if (close < 0) return name.Substring(0, open);
			return name.Substring(0, open) + name.Substring(close + 1);
		}

		/// <summary>Objects refused an address for being purely local. Reported so
		/// the exclusion is visible rather than assumed.</summary>
		public static int LocalOnlySkipped { get; private set; }

		/// <summary>
		/// Client previews that declined to mint an id, waiting to be named.
		///
		/// Expected to be zero on a host and to roughly track previews on a client.
		/// If it stays at zero while previews climb, local objects are still drawing
		/// numbers out of the host's id space - which is what produced three ids that
		/// meant different things on the two peers.
		/// </summary>
		public static int PreviewsWithoutId { get; private set; }

		/// <summary>
		/// Ids a client declined to invent for objects the host never named.
		///
		/// Zero on a host. On a client this is the count of local objects that used to
		/// draw a number out of the host's id space, which is how one id came to mean
		/// a plant on one machine and a corpse on the other.
		/// </summary>
		public static int ClientMintsRefused { get; private set; }

		/// <summary>
		/// Convergences a client declined to perform on the host's names.
		///
		/// Zero on a host, and idMoves on a client should fall to zero beside it.
		/// </summary>
		public static int ClientConvergesRefused { get; private set; }

		/// <summary>
		/// Ids a client took because the hash alone produced them - no walk, so both
		/// peers reach the same number without talking.
		///
		/// This is how objects the host never announces stay addressable on a client:
		/// storage proxies, and anything else derived purely from prefab, cell and
		/// workable type.
		/// </summary>
		public static int ClientHashesAccepted { get; private set; }

		/// <summary>
		/// Announcements that wanted to rename an object which already had a name.
		///
		/// Refused, and counted so the refusal is visible. Allowing it once moved a
		/// pile off the host's own id and cost eight thousand failed lookups from that
		/// single object.
		/// </summary>
		public static int AdoptionsRefusedNamed { get; private set; }

		/// <summary>
		/// Whether a prefab name denotes scaffolding that exists only on the peer
		/// that made it.
		///
		/// Four categories, each one named by the creation-time attribution:
		/// locators (a chore's "stand here" marker), placers (the ghost drawn while
		/// an order is pending), storage proxies, and effects. ONI names them
		/// consistently, which is what makes matching on the name sound: there is no
		/// component they share and nothing about a locator that distinguishes it
		/// from the object it points at.
		///
		/// Deliberately narrow. CO2 and BreathFX both come out of CO2Manager and
		/// only the second is an effect - the first is gas that becomes part of the
		/// simulation, and excluding it would hide a real divergence. Anything not
		/// listed keeps its identity.
		/// </summary>
		/// <summary>Chunks of gas or liquid that were refused an address.</summary>
		public static int EphemeralSkipped { get; private set; }

		/// <summary>
		/// Whether this is matter the simulation will reabsorb, rather than an object.
		///
		/// Gas and liquid chunks are created by ONI's own element simulation on both
		/// peers - a pocket of gas condenses, a puddle falls - and they are gone again in
		/// seconds. Nothing hauls them, stores them or works them, so no packet ever
		/// names one.
		///
		/// Solids are the opposite: ore is hauled, stored, eaten and built with, and the
		/// protocol refers to it constantly. So the line is drawn at the element's state,
		/// which is also exactly where the measurement pointed - every one of the 44 to
		/// 60 objects a run that the two peers disagreed about was a gas or a liquid.
		///
		/// Deliberately narrow: a Pickupable with no PrimaryElement, or a solid, or
		/// anything carrying a Storage, keeps its identity. Being wrong in the other
		/// direction would silently stop a real object replicating, which is far worse
		/// than a gas chunk nobody can point at.
		/// </summary>
		/// <summary>
		/// Why IsEphemeralMatter reached its answer, as a word.
		///
		/// Added because reading the code could not settle a contradiction the logs
		/// stated plainly: the host held fourteen Hydrogen ids while the registry-wide
		/// assertion that no ephemeral object may be registered passed on that same
		/// host. Both cannot be true unless the predicate declines those objects, and
		/// which branch declines them is not visible from outside.
		///
		/// So it reports the branch. One run of this answers what three rounds of
		/// reasoning about ONI's component model did not.
		/// </summary>
		public static string EphemeralVerdict(GameObject go)
		{
			if (go.IsNullOrDestroyed()) return "destroyed";
			if (!go.TryGetComponent<PrimaryElement>(out var primary) || primary.IsNullOrDestroyed())
				return "no-primary-element";

			var element = primary.Element;
			if (element == null) return "no-element";
			if (!element.IsGas && !element.IsLiquid)
				return element.IsSolid ? "solid" : "other-state";

			if (go.TryGetComponent<Pickupable>(out var pickupable)
				&& !pickupable.IsNullOrDestroyed()
				&& pickupable.storage != null
				&& !pickupable.storage.IsNullOrDestroyed())
				return "in-storage";

			return "ephemeral";
		}

		public static bool IsEphemeralMatter(GameObject go)
		{
			if (go.IsNullOrDestroyed()) return false;
			if (!go.TryGetComponent<PrimaryElement>(out var primary) || primary.IsNullOrDestroyed())
				return false;

			var element = primary.Element;
			if (element == null) return false;
			if (!element.IsGas && !element.IsLiquid) return false;

			// Inside something? Then somebody put it there on purpose and it is being
			// tracked - a bottle, a tank, a duplicant's hands.
			if (go.TryGetComponent<Pickupable>(out var pickupable)
				&& !pickupable.IsNullOrDestroyed()
				&& pickupable.storage != null
				&& !pickupable.storage.IsNullOrDestroyed())
				return false;

			return true;
		}

		/// <summary>
		/// Whether this object may never hold an address, whoever asks and whenever.
		///
		/// Ephemeral matter only. Scaffolding is refused at spawn and granted on demand,
		/// so it is not "excluded" in this sense and asserting that it is would assert
		/// something false - a DigPlacer being addressable is the correct state once a
		/// duplicant is digging.
		///
		/// Static and taking the object, so a test can ask it of everything in the
		/// registry rather than only of whatever is running OnSpawn. That gap is what
		/// let the last question go unanswered for three rounds.
		/// </summary>
		public static bool IsExcludedFromIdentity(GameObject go)
		{
			if (go.IsNullOrDestroyed()) return false;
			return IsEphemeralMatter(go);
		}

		/// <summary>Set once this object has been refused, so it is counted once.</summary>
		private bool _addressRefused;

		/// <summary>
		/// Whether this object was ephemeral matter at the moment it was given an
		/// address.
		///
		/// The distinction the registry-wide assertion needs. "No ephemeral object holds
		/// an address" reads like an invariant and is not one: the classification depends
		/// on whether the thing is sitting in a container, and that changes. A bottle of
		/// water in a storage bin is correctly identified, and when a duplicant empties
		/// it the same object becomes loose liquid - still holding the id it was
		/// legitimately given.
		///
		/// The assertion used to fail on exactly those two objects a run, on the client
		/// only, and there was nothing wrong with them. An assertion that fires without a
		/// defect costs more than it finds: this project has three episodes of chasing
		/// numbers that meant nothing.
		///
		/// What the code does promise is narrower and worth checking - that nothing which
		/// was ephemeral when it asked was given an address anyway. That is a real
		/// invariant, and a bypass of the refusal in RegisterIdentity is exactly what
		/// would break it.
		/// </summary>
		public bool WasEphemeralWhenAddressed { get; private set; }

		/// <summary>
		/// Times a sender asked for the address of an object that may not have one.
		///
		/// The refusal was invisible where it mattered. Moving it to RegisterIdentity
		/// halved the client's failed lookups, and in the same run produced 476 packets
		/// carrying NetId 0 - the first in ninety-six builds. GetNetIdentity asks
		/// RegisterIdentity for an id, is refused, and hands the caller a zero, which
		/// the caller writes into the packet without knowing anything happened.
		///
		/// So the refusal is counted here, at the point of use, and the objects are
		/// named. Which objects a sender needs to address is the thing that decides
		/// where the rule's line belongs, and three rounds of reasoning about ONI's
		/// component model did not produce it - every Pickupable is a Workable, so
		/// "can it be worked" cannot separate a gas cloud from a bottle somebody is
		/// hauling.
		/// </summary>
		public static int AddressAskedAfterRefusal { get; private set; }

		private static readonly Dictionary<string, int> _refusedAsksByPrefab =
			new Dictionary<string, int>();

		/// <summary>The prefabs senders tried to address despite the refusal, worst first.</summary>
		public static string RefusedAskBreakdown()
		{
			if (_refusedAsksByPrefab.Count == 0) return "none";
			var parts = new List<string>();
			foreach (var kv in _refusedAsksByPrefab.OrderByDescending(kv => kv.Value).Take(8))
				parts.Add($"{StripInstanceId(kv.Key)}:{kv.Value}");
			return string.Join(" ", parts);
		}

		/// <summary>
		/// A sender wanted this object's address and the rules say it has none.
		/// </summary>
		public static void NoteRefusedAsk(GameObject go)
		{
			AddressAskedAfterRefusal++;

			string prefab = go.IsNullOrDestroyed() ? "?" : (go.name ?? "?");
			_refusedAsksByPrefab.TryGetValue(prefab, out int n);
			_refusedAsksByPrefab[prefab] = n + 1;

			// The caller, once per prefab. A count says how much; only the frames
			// above say which sender needs this object to be addressable, and that is
			// what decides whether the rule or the sender is wrong.
			if (n == 0)
			{
				var trace = new System.Diagnostics.StackTrace(1, false);
				var frames = new List<string>();
				for (int i = 0; i < trace.FrameCount && frames.Count < 6; i++)
				{
					var m = trace.GetFrame(i)?.GetMethod();
					if (m?.DeclaringType == null) continue;
					string owner = m.DeclaringType.Name;
					if (owner == nameof(NetworkIdentity) || owner == "Extensions") continue;
					frames.Add($"{owner}.{m.Name}");
				}
				DebugConsole.LogWarning(
					$"[RefusedAsk] a sender wants the address of '{StripInstanceId(prefab)}', " +
					$"which is excluded from identity ({EphemeralVerdict(go)}), from: " +
					string.Join(" <- ", frames));
			}
		}

		/// <summary>
		/// Whether this object may never hold a network address, and record it.
		///
		/// Two rules, and they apply in different places on purpose. The asymmetry was
		/// measured, not designed, and undoing it costs a fix that shipped.
		///
		/// Rule one - scaffolding, matched by name suffix. Applies at spawn only.
		/// Creation-time attribution named these: ChoreHelpers.CreateLocator,
		/// Navigator.OnPrefabInit, Repairable.CreateStorageProxy, the pooled FX spawners,
		/// and our own WorldStateSyncer drawing a DigPlacer. Nobody announces them, so
		/// refusing them at spawn stops a client indexing each one as a preview waiting
		/// for a name that never arrives.
		///
		/// But it must not be refused when a sender actually asks. A duplicant's dig
		/// order IS a DigPlacer and a repair delivery IS a RepairableStorageProxy - the
		/// work packet is addressed to that object and nothing else. Applying this rule
		/// at the choke point produced 476 packets a run carrying NetId 0, the first in
		/// ninety-six builds, and [RefusedAsk] named both prefabs with the sender:
		/// StandardWorker_WorkingState_Packet from StandardWorker_StartWork_Patch. The
		/// same mistake had already been made once and is recorded further down this
		/// file - refusing the proxy outright broke wire repair in a live session.
		///
		/// So the lazy grant in GetNetIdentity is the design, not a leak: an object
		/// nobody addresses never takes an id, and one a sender addresses gets a
		/// deterministic one that both peers compute alike.
		///
		/// Rule two - matter the simulation reabsorbs. Applies everywhere, because
		/// nothing can legitimately need the address of a falling gas cloud. Worth
		/// almost nothing in practice: with the rule at the choke point, exactly one to
		/// three registered objects were ephemeral. The gas and liquid ids that looked
		/// like a bypass were in-storage=193 - bottled and tanked, correctly identified,
		/// and the disagreement about them is a storage replication gap.
		///
		/// Counted per object rather than per call, because a counter that grows with
		/// how often something is asked is not a count of anything.
		/// </summary>
		private bool RefusesAddressAtSpawn()
		{
			bool localOnly = IsLocalOnlyName(gameObject.name);
			bool ephemeral = !localOnly && IsEphemeralMatter(gameObject);
			if (!localOnly && !ephemeral) return false;

			if (!_addressRefused)
			{
				_addressRefused = true;
				if (localOnly) LocalOnlySkipped++;
				else EphemeralSkipped++;
			}
			return true;
		}

		/// <summary>
		/// Whether this object may never hold an address, however it is asked for.
		/// Ephemeral matter only - see RefusesAddressAtSpawn for why scaffolding is not
		/// in here.
		/// </summary>
		private bool RefusesAddressAlways()
		{
			if (!IsEphemeralMatter(gameObject)) return false;

			if (!_addressRefused)
			{
				_addressRefused = true;
				EphemeralSkipped++;
			}
			return true;
		}

		public static bool IsLocalOnlyName(string name)
		{
			if (string.IsNullOrEmpty(name)) return false;

			string n = StripInstanceId(name);
			if (n.EndsWith("(Clone)")) n = n.Substring(0, n.Length - 7);

			return n.EndsWith("Locator")
				|| n.EndsWith("Placer")
				|| n.EndsWith("Proxy")
				|| n.EndsWith("FX")
				|| n.EndsWith("Fx")
				|| n.StartsWith("fx_");
		}

		public static void ResetPreviewCounters()
		{
			PreviewsCreated = 0;
			PreviewsAdopted = 0;
			_previewsByPrefab.Clear();
		}

		public static void DumpPreviewBreakdown(string tag)
		{
			foreach (var kvp in _previewsByPrefab)
				DebugConsole.Log($"{tag} preview|{kvp.Key}|{kvp.Value}");
		}

		public override void OnSpawn()
		{
			using var _ = Profiler.Scope();

			base.OnSpawn();

			// Read here rather than in the patch: OnSpawn runs inside the
			// KInstantiate call that set it, and only objects that carry a
			// NetworkIdentity care.
			// Through the registry, not into the field.
			//
			// This used to assign NetId directly, which is the one way to end up
			// filed under one id while believing another - and a mis-consumed
			// reservation then corrupted the object it landed on rather than merely
			// being wrong. OverrideNetId moves the field and the registry entry
			// together and evicts whoever held the id, so the worst case becomes a
			// visible reassignment instead of a silent duplicate.
			// Not gameObject.PrefabID(): plenty of objects carry a NetworkIdentity and
			// no KPrefabID, and on those it throws.
			//
			// This shipped and crashed a live session. WorldSelectionCollider spawns
			// with no KPrefabID, PrefabID() threw a NullReferenceException inside
			// OnSpawn, and the client closed itself - "Error in
			// WorldSelectionCollider.NetworkIdentity.OnSpawn" three times and then
			// Game.OnApplicationQuit with no other cause in the log. The reservation
			// only needed a name to compare against; it did not need that name to come
			// from a component that might not be there.
			int reserved = ConsumeReservation(SafePrefabName);
			if (reserved != 0)
				OverrideNetId(reserved);

			// Objects that take no address at spawn - see RefusesAddressAtSpawn.
			//
			// Returning here does two things: it skips the registration, and it stops a
			// client indexing the object as a preview waiting to be named by an
			// announcement that is never sent. A sender that genuinely needs one of these
			// addressed still gets it later, through GetNetIdentity.
			if (RefusesAddressAtSpawn())
			{
				KInstantiatePatch.ConsumeClientPreviewFlag();
				return;
			}

			if (KInstantiatePatch.ConsumeClientPreviewFlag())
			{
				IsClientPreview = true;
				PreviewsCreated++;

				string prefab = gameObject.name ?? "?";
				_previewsByPrefab.TryGetValue(prefab, out int n);
				_previewsByPrefab[prefab] = n + 1;

				// Who on the client created this, once per prefab.
				//
				// The host announces two Sandstone and the client is holding two it
				// cannot name; it announces two Oxygen and the client holds four. So
				// the client is not merely failing to match what arrives - it is
				// creating objects of its own that the host has no counterpart for,
				// and those are what every failed lookup is about.
				//
				// The dig path that did this was closed yesterday. Something else is
				// still doing it, and a count cannot say what. A stack can: the frames
				// above OnSpawn name the code that instantiated the object.
				if (n == 0)
				{
					var trace = new System.Diagnostics.StackTrace(1, false);
					var frames = new List<string>();
					for (int i = 0; i < trace.FrameCount && frames.Count < 6; i++)
					{
						var m = trace.GetFrame(i)?.GetMethod();
						if (m?.DeclaringType == null) continue;
						string owner = m.DeclaringType.Name;
						if (owner == nameof(NetworkIdentity)) continue;
						frames.Add($"{owner}.{m.Name}");
					}
					DebugConsole.LogWarning(
						$"[PreviewOrigin] client drew '{StripInstanceId(prefab)}' with no id, from: " +
						string.Join(" <- ", frames));
				}

				IndexPreview();

				// A local preview does not take a number.
				//
				// It used to. The preview was marked, counted, indexed by cell - and
				// then registered like anything else, so an object that exists only on
				// this client drew an id out of the same space the host is naming
				// objects in. The free-slot walk then has no way to avoid a value the
				// host has already given to something else, because it cannot see the
				// host's table.
				//
				// Measured across two peers on the same run: three ids where the host
				// held BasicPlantBar or BasicPlantFood and the client held a CREATURE
				// element pile of its own making. Same id, different object, so every
				// packet about the host's food landed on the client's rubble. Nothing
				// inside either peer could detect it - the "no id held by two prefabs"
				// check passes on both, because on each one the id is held once.
				//
				// A preview is an object waiting to be named. Until the host names it,
				// it has no address, and that is the honest state: TryAdoptPreview
				// finds it through the by-cell index, not through the registry.
				//
				// Unless the host already reserved one for it - a dropped resource
				// arrives with its id ahead of the object - in which case NetId is
				// already set and registered, and this is not a nameless preview.
				if (NetId == 0)
				{
					PreviewsWithoutId++;
					return;
				}
			}

			RegisterIdentity();
		}

		public void RegisterIdentity()
		{
			using var _ = Profiler.Scope();

			// "Registered" only counts if it produced an address. A previous
			// attempt can mark this and still leave NetId at 0 - the grid was
			// not ready, or an eviction sent it looking for a new slot and found
			// none - and without this the object is stuck at zero for good,
			// because nothing else ever calls back.
			if (IsRegistered && NetId != 0)
				return;

			// Only the rule that holds no matter who asks - see RefusesAddressAlways.
			//
			// Scaffolding is deliberately not refused here. It is refused at spawn and
			// granted on demand, because a dig order and a repair delivery are addressed
			// to a DigPlacer and a RepairableStorageProxy, and refusing those here sent
			// 476 packets a run to NetId 0.
			if (RefusesAddressAlways())
				return;

			// A client-side preview draws immediately so the game stays
			// responsive, but it must not mint its own id: the host has not
			// issued one, so anything the client registered under it would be
			// an address the host cannot use. It stays unregistered until
			// OverrideNetId hands it the host's id.
			if (IsClientPreview)
				return;

			// A client never invents an id.
			//
			// The preview check above only covers objects created through
			// Util.KInstantiate, which is where the flag is set. Everything else the
			// client makes for itself - and it makes plenty, the element piles a dying
			// critter leaves being the ones that showed up here - arrives with the flag
			// clear and mints an id like a host would.
			//
			// It cannot do that safely. The id comes from a hash plus a walk to the
			// next free slot, and the walk can only see this peer's table. Landing on a
			// value the host has already given to something else is undetectable from
			// inside either peer: each one holds that id exactly once, so every
			// single-peer check passes. Comparing the two tables is what found it, and
			// it found the same shape every time - the host holding BasicPlantFood,
			// BasicPlantBar or SwampLilyFlower, the client holding a Creature pile,
			// under one id. Packets about the host's food arrive addressed to rubble.
			//
			// So on a client an object with no id keeps no id. If the host owns it, the
			// host names it and OverrideNetId registers it then. If the host does not
			// own it, it is local and needs no address at all - which is the honest
			// answer, and strictly better than an address that means something else on
			// the other machine.
			if (MultiplayerSession.InSession && MultiplayerSession.IsClient && NetId == 0)
			{
				// Except the part both peers can compute alike.
				//
				// Refusing outright was too wide and broke repairs in a live session.
				// A Repairable keeps its materials in a RepairableStorageProxy, the host
				// names that proxy from prefab, cell, workable type and salt - a pure
				// function - and never announces it, because it is not a loose item.
				// The two peers were always meant to agree on it by computing the same
				// number without talking, which is the mechanism the project notes
				// describe. With the client refusing to compute anything, it had no id
				// for the proxy at all: it asked the host, the host answered "I hold it
				// and it is not something I can send", and the repair state had nowhere
				// to land. The host went on answering 95 damage queries every thirty
				// seconds without the count ever falling.
				//
				// The distinction that matters is not who computes, it is whether the
				// answer depends on local state. The hash does not. The walk to the next
				// free slot does - it can only see this peer's table - and that is the
				// half that produced ids meaning different things on the two machines.
				//
				// So: take the hash when it lands where it hashes, and refuse when it
				// would have to walk.
				// Back to refusing, which is the best-measured behaviour there is.
				//
				// Letting a client compute its own hash was meant to fix repairs, and it
				// did address the mechanism it was aimed at - a Repairable's storage
				// proxy is never announced, so the client had no way to name it and
				// repair state had nowhere to land. But the measured cost was far larger
				// than the gain: failed lookups per minute went from about one to between
				// 765 and 2,161, and four separate attempts to explain that were each
				// refuted by the next run.
				//
				// The three follow-up fixes were all regressions - relaxing adoption let
				// an announcement rename a pile that already agreed with the host, the
				// announce-aware narrowing made the rate worse, and the sweep never fired
				// at all. Continuing to guess from here is the pattern, not a fix.
				//
				// So this returns to the rule that measured 4 to 6 failures for a whole
				// run, and repair replication stays open and written down rather than
				// traded for a hundredfold increase somewhere else. What is missing is a
				// way for a client to name an object the host will never announce, and
				// that needs the announcement side fixed - not the client guessing.
				ClientMintsRefused++;
				return;
			}

			if (Grid.WidthInCells == 0)
			{
				// DebugConsole.LogWarning($"[NetworkIdentity] Skipping registration for {gameObject.name} - Grid not ready");
				return;
			}

			// Try to handle deterministic ID for buildings first
			if (NetId == 0)
			{
				if (TryGetComponent<Building>(out var building))
				{
					int detId = NetIdHelper.GetDeterministicBuildingId(gameObject);
					if (detId != 0)
					{
						NetId = detId;
						// DebugConsole.Log($"[NetworkIdentity] Generated Deterministic NetId {detId} for building {gameObject.name}");
					}
				}
				else if(TryGetComponent<Workable>(out var workable))
				{
					int detId = NetIdHelper.GetDeterministicWorkableId(gameObject);
					if (detId != 0)
					{
						NetId = detId;
					}
				}
				else
				{
					int detId = NetIdHelper.GetDeterministicEntityId(gameObject);
					if (detId != 0)
					{
						NetId = detId;
						// DebugConsole.Log($"[NetworkIdentity] Generated Deterministic NetId {detId} for building {gameObject.name}");
					}
				}
				//DebugConsole.Log($"[NetworkIdentity] Generated Deterministic NetId {NetId} for {gameObject.name}");
			}

			if (NetId == 0)
			{
				NetId = NetworkIdentityRegistry.Register(this);
				//DebugConsole.Log($"[NetworkIdentity] Generated Random NetId {NetId} for {gameObject.name}");
			}
			else if (!NetworkIdentityRegistry.RegisterExisting(this, NetId))
			{
				// Somebody else holds this id. Leaving it here is what made the
				// damage permanent: the object kept an id that resolves to
				// another object, was marked registered anyway, and then got
				// SAVED that way - so the duplicate came back on every load and
				// the loser never appeared in the registry at all. Two
				// hydroponic farms, two swamp lilies and three hatches were in
				// exactly that state in a live colony, and nothing addressed to
				// them could ever arrive.
				//
				// NetId is [Serialize]d, so repairing it here also repairs the
				// save the next time the world is written.
				if (!TryRehouse())
					return;
			}

			// Recorded at the moment the address is granted, because that is when the
			// refusal was consulted. Asked later it answers a different question - see
			// WasEphemeralWhenAddressed.
			WasEphemeralWhenAddressed = IsEphemeralMatter(gameObject);

			IsRegistered = true;

			// Convergence is NOT called here, and the attempt to do so is worth
			// recording.
			//
			// Applying the cell tie-break on every registration looked like the
			// general version of the egg fix. It produced a cascade instead: A takes
			// the id it wants from B, B re-registers and takes one from C, and so on.
			// Measured immediately - IdConverge fired between 3,227 and 7,016 times in
			// a single run, and the bulk-rehouse check went from passing to reporting
			// 163 collisions. That is the plant naming loop again, in a new place.
			//
			// The egg path calls it once, at spawn, for one object. That is where it
			// belongs until there is a version whose cost is bounded by construction
			// rather than by hoping the cascade terminates.
			AnnounceSpawnIfHost();
		}

		/// <summary>
		/// Find this object a free id after its own turned out to be taken.
		///
		/// The replacement is derived, not allocated: recomputing the
		/// deterministic id gives a value that is a pure function of what this
		/// object is and where it is, so two peers loading the same save reach
		/// the same answer without talking. Only if that is taken too does it
		/// walk upward, and a host that is in session announces the result so a
		/// client cannot be left guessing.
		///
		/// Returns false if no id could be found at all, in which case the
		/// object stays unregistered and unmarked - it will try again the next
		/// time RegisterIdentity runs, rather than pretending it succeeded.
		/// </summary>
		/// <summary>
		/// Called when the registry hands this object's id to somebody else.
		/// Without it the evicted object keeps an id that now resolves to a
		/// different object, which is how two duplicants from one printing pod
		/// ended up sharing an address.
		/// </summary>
		/// <summary>
		/// Every change of this object's id, with the reason.
		///
		/// One pair of objects claims a single id on every run, and it is the same
		/// shape each time: a GasConduit whose field says a Clay's id while it is
		/// filed in the registry under a third number. The registry corrects the
		/// field whenever it files something, and it never reports correcting these
		/// - so the write happens after the filing, and four rounds of reading the
		/// three functions that move an id found no ordering that produces it.
		///
		/// Recording the transitions costs one line per move and ends the guessing:
		/// the sequence that produced the drift will be in the log, in order, with
		/// who did it. Bounded, because a busy colony moves ids constantly and
		/// per-object logging at that rate has frozen a host before.
		/// </summary>
		private void NoteIdChange(int from, int to, string reason)
		{
			if (from == to)
				return;

			_idChanges++;

			// Count the renames per object as well as overall. One object moving
			// repeatedly is a loop; many objects moving once each is a session
			// starting up, and the totals alone cannot tell them apart.
			_renames++;

			if (_idChanges <= 40 || _idChanges % 100 == 0 || _renames == 3)
			{
				DebugConsole.Log(
					$"[IdMove] '{SafePrefabName}'#{GetInstanceID()} {from} -> {to} ({reason}" +
					$"{(string.IsNullOrEmpty(_lastNamedBy) ? "" : " by " + _lastNamedBy)}), " +
					$"move #{_idChanges}, this object's rename #{_renames}");
			}

			if (_renames == RenameAlarm)
			{
				DebugConsole.LogWarning(
					$"[IdMove] '{SafePrefabName}'#{GetInstanceID()} has now been renamed " +
					$"{_renames} times, most recently to {to} by {(_lastNamedBy ?? "?")}. Every rename " +
					"drops the previous id, so anything still addressed by one of them cannot be " +
					"resolved. This is a naming loop, not initialisation.");
			}
		}

		/// <summary>
		/// Forget that this object is filed anywhere, while keeping the number it was
		/// filed under.
		///
		/// The registry is cleared on every disconnect and it only clears its own
		/// dictionary - the identities keep NetId and keep IsRegistered set to true. The
		/// first line of RegisterIdentity is "if it is registered and has an id, there is
		/// nothing to do", so after a reconnect eight thousand objects each believe they
		/// are registered while the registry has never heard of them, and nothing ever
		/// calls back.
		///
		/// Measured on a client put through a live reconnect for the first time:
		/// registry 8085 before, 41 after, and failed lookups went from 4,586 to 90,068.
		/// The world was intact - twenty-one duplicants, 343 plants - and every object in
		/// it had lost its address. That is the whole of "reconnect never succeeds": the
		/// connection is fine and nothing can be addressed.
		///
		/// The id is deliberately kept. It came from a save both peers load identically,
		/// so re-filing under the same number restores exactly the mapping that was
		/// there - no renaming, no new ids, nothing for the other peer to disagree with.
		/// </summary>
		internal void ForgetRegistration()
		{
			IsRegistered = false;
		}

		/// <summary>Renames of this one object, as opposed to the process-wide total.</summary>
		[SkipSaveFileSerialization]
		private int _renames;

		/// <summary>Who last called OverrideNetId on this object.</summary>
		[SkipSaveFileSerialization]
		private string _lastNamedBy;

		/// <summary>
		/// A legitimately named object is named once. Three is generous; four is what
		/// the loop in the log did.
		/// </summary>
		private const int RenameAlarm = 4;

		/// <summary>
		/// Renames refused across all objects. Zero once the cause is fixed; a rising
		/// number says the loop is still happening and is merely no longer harmful.
		/// </summary>
		public static int RenamesRefused => _renamesRefused;

		private static int _renamesRefused;

		public static void ResetRenameRefusals() => _renamesRefused = 0;

		private static int _idChanges;
		public static int IdChanges => _idChanges;

		internal void RehouseAfterEviction(int lostId)
		{
			if (NetId != lostId)
			{
				NoteIdChange(NetId, NetId,
					$"eviction of {lostId} ignored: this object believes it holds {NetId}");
				return;
			}

			IsRegistered = false;
			if (!TryRehouse())
			{
				NoteIdChange(NetId, 0, "rehouse after eviction found nowhere to go");
				NetId = 0;
				return;
			}

			// A successful rehouse leaves the object filed under its new id, and
			// this used to leave IsRegistered false anyway. The object was then in
			// the registry while believing it was not, so the next RegisterIdentity
			// call - and there are ninety-odd places that add an identity and
			// register it - ran the whole path again and reached
			// AnnounceSpawnIfHost a second time for an object that had already been
			// announced. Rehousing is common on this save, because the ids in it
			// were minted by the old XOR hash and collide.
			WasEphemeralWhenAddressed = IsEphemeralMatter(gameObject);
			IsRegistered = true;
		}

		/// <summary>
		/// Move to the id this object's kind and position imply, if it is not already
		/// there. Both peers compute the same answer, so both end up agreeing.
		///
		/// For objects that exist well before they are placed - a laid egg sits
		/// somewhere for the best part of a minute before it lands in the world - the
		/// two peers do not mint their id at the same moment. The host asks for an id
		/// early, because it is about to send something about the egg, and that id is
		/// derived from wherever the egg is at that instant. The client never asks, so
		/// it mints at spawn from the final cell. Two cells, two ids, and every packet
		/// about that egg misses.
		///
		/// Three attempts were made to attach the identity earlier instead - at the
		/// incubation monitor, at the prefab tag, at OnPrefabInit - and each failed
		/// because the thing being tested does not exist yet at that point. This stops
		/// chasing the moment of creation and fixes the property that actually
		/// matters: by the time the object is in the world, both peers call it the
		/// same thing.
		/// </summary>
		internal void ConvergeOnDeterministicId()
		{
			if (IsClientPreview) return;
			if (Grid.WidthInCells == 0) return;

			// Only the host converges.
			//
			// This is the same defect as a client minting an id, through a different
			// door: the client takes an object the host has already named and moves it
			// to whatever value its own hash and its own free-slot walk produce. The
			// log even says "named by the host" while a client is doing it.
			//
			// The evidence was in front of me for hours. A PacuEgg at cell 61849: the
			// host converged 1538373406 to -455343586 and the client, already holding
			// -455343586 because the host had told it so, converged that to
			// -455343585. Fixing the walk to exempt the object's own slot stopped the
			// off-by-one, but the client should not have been in that arithmetic at
			// all - and with the client refusing to mint new ids, convergence was the
			// one remaining way for it to invent one. Three ids still meant different
			// things on the two peers after the minting fix, on a longer run.
			//
			// The host is the authority. A client's job is to accept the name it is
			// given, which is also why idMoves on a client should read zero.
			// Unless it has no name to protect.
			//
			// The refusal above is right about what it describes: a client taking an
			// object the host has already addressed and moving it to whatever its own
			// hash produces. That is the client inventing an id, and three ids meant
			// different things on the two peers because of it.
			//
			// An object holding zero is not that case. Nothing has been given to it, so
			// there is nothing to overwrite, and refusing here does not protect a host
			// id - it leaves the object with none at all. That is the last thing keeping
			// "1 of 82 creatures have no NetId: CrabBaby" alive: the host now converges
			// its own copy onto the deterministic id, and the client, holding the same
			// animal, is forbidden from computing the same answer.
			//
			// Deterministic means both peers compute it from what the object is and
			// where it is, so a client arriving at it independently agrees with the host
			// by construction rather than by being told.
			if (MultiplayerSession.InSession && MultiplayerSession.IsClient && NetId != 0)
			{
				ClientConvergesRefused++;
				return;
			}

			int expected = ComputeDeterministicId();

			// Already the number both peers compute? Then whatever gave it that number
			// is no longer a problem, and saying so is the point.
			//
			// The lazy-attach warning is raised when something asks for an id before the
			// spawn hook has run, and it is raised for good reason - an id minted at an
			// arbitrary moment is one the other peer cannot reproduce. But it is recorded
			// once and never revisited, so a PuftEgg that was asked about four
            // milliseconds early kept being reported as "only got an identity when a
			// packet needed one" for the rest of the session, when the very next thing
			// that happened was this method giving it the id its cell implies. Measured
			// on one object across both logs: the lazy warning at 01:29:53.110 and the
			// spawn attach at 01:29:53.114, ending on the same id the client computes.
			//
			// Clearing it here is not hiding the failure. It is the repair reporting
			// itself, so the count means "still one-sided" rather than "was one-sided at
			// some point".
			if (expected == 0) return;

			// Already the right number - so it is no longer one-sided, and the mark
			// should go even though nothing moves.
			//
			// Both exits have to clear it. Clearing only on the equal case left lazyFixed
			// at 0 because this method exists to change ids; clearing only after a move
			// left it at 0 too, because an egg addressed early can land on the
			// deterministic id by luck and then there is nothing to move. Two runs were
			// spent finding that out one exit at a time.
			if (expected == NetId)
			{
				ForgetLazyAttachment(this);
				return;
			}

			// If somebody else is in that slot, decide by a rule both peers compute
			// the same way - never by who got there first.
			//
			// This is the actual defect behind the eggs, and it is not what the last
			// five attempts assumed. Comparing the two peers' id tables shows every
			// egg differing by exactly one, with the direction swapping between runs:
			//
			//   soak02  DreckoEgg@33185  host=-1578446294  client=-1578446295
			//   soak03  DreckoEgg@33185  host=-1578446295  client=-1578446294
			//
			// Both peers computed the same base id. Two eggs want it, one is pushed a
			// slot up by the free-id walk, and which one is pushed depends on the
			// order they happened to register in - which the peers have no reason to
			// share. Lazy attachment was a symptom of the same root, not the cause.
			//
			// Cells are the tie-break: both peers know where each object is, and the
			// answer does not depend on time. The lower cell keeps the id; the other
			// walks. Applied on both sides, the two tables converge no matter who
			// arrived first.
			// Only into an empty slot. Taking one starts a cascade.
			//
			// The first version broke the tie by cell - lower cell wins, evict the
			// other - reasoning that both peers compute the same comparison and so
			// reach the same table. They do, but eviction makes the loser re-register,
			// which makes it evict somebody else. One run logged 7,016 of these moves
			// and the bulk-rehouse check went from clean to 163 collisions.
			//
			// So the rule is now: move if the id its cell implies is free, otherwise
			// stay. That fixes the case the eggs were actually in - both peers arrive
			// at the same free slot - without the part that cascades. A collision
			// between two live objects is left alone here and reported by the tests,
			// which is the honest state: known, measured, not silently churned.
			if (NetworkIdentityRegistry.Exists(expected)
				&& !NetworkIdentityRegistry.Holds(expected, this))
			{
				return;
			}

			DebugConsole.Log(
				$"[IdConverge] '{SafePrefabName}' moving from {NetId} to {expected}, " +
				"the id its kind and final cell imply - both peers compute this, so both agree");
			OverrideNetId(expected);

			// After the move, not before it.
			//
			// The first attempt cleared the lazily-attached mark when the id already
			// equalled the deterministic one, and lazyFixed read 0 for a whole run:
			// this method exists to change the id, so at the point it was checked they
			// are never equal. The object stops being one-sided when the move lands,
			// which is here.
			ForgetLazyAttachment(this);
		}

		private bool TryRehouse()
		{
			using var _ = Profiler.Scope();

			int taken = NetId;
			int candidate = ComputeDeterministicId();

			// The saved id may itself be the deterministic one, in which case
			// recomputing changes nothing and the walk below does the work.
			if (candidate == 0 || candidate == taken || NetworkIdentityRegistry.Exists(candidate))
				candidate = NetworkIdentityRegistry.FindFreeId(candidate != 0 ? candidate : taken + 1);

			if (candidate == 0)
			{
				DebugConsole.LogWarning(
					$"[NetworkIdentity] '{gameObject.name}' could not be rehoused off NetId {taken}; " +
					"it stays unregistered and will retry");
				return false;
			}

			NetId = candidate;
			if (!NetworkIdentityRegistry.RegisterExisting(this, NetId))
			{
				// Put back, and say so: this is one of the two places that leaves
				// the field holding an id the registry does not have this object
				// under, and the other one is the guard above.
				NoteIdChange(candidate, taken, "rehouse target was taken too; reverted");
				NetId = taken;
				return false;
			}

			NoteIdChange(taken, candidate, "rehoused off a duplicate");
			return true;
		}

		/// <summary>
		/// The id this object would be given if it had none, by the same branch
		/// order RegisterIdentity uses. Kept as one method so the repair path
		/// and the first-registration path cannot drift apart.
		/// </summary>
		/// <summary>
		/// Delegated so there is one answer to "what id should this object have".
		///
		/// This used to be a second copy of the same three-way choice, which is how
		/// a test could agree with the rule it was checking and still both be wrong
		/// together - or worse, drift apart and disagree about an object neither
		/// would move.
		/// </summary>
		private int ComputeDeterministicId()
			=> NetIdHelper.GetDeterministicIdFor(gameObject, quiet: false);

		/// <summary>
		/// Tell clients about an object the host just named.
		///
		/// Announcing from KInstantiate only caught two spawns in a whole run,
		/// because most objects never go through Util.KInstantiate -
		/// SpawnResource, which produces the element piles that are 71% of what
		/// a client draws unnamed, is one of them. Announcing here instead
		/// catches every creation path, because they all end up needing an id.
		/// </summary>
		/// <summary>
		/// Re-announce an object whose id has just moved.
		///
		/// OverrideNetId re-files the registry and tells nobody, which is correct for the
		/// case it was written for - a host applying an id it was given. It is wrong
		/// straight after a convergence, because the clients were told the old number
		/// when this object was registered a moment earlier.
		///
		/// Deliberately not called from ConvergeOnDeterministicId itself. The note there
		/// records why: announcing from every convergence produced between 3,227 and
		/// 7,016 of them in one run and turned a bulk-rehouse check from passing to 163
		/// collisions. This is the one caller that has just created the discrepancy.
		/// </summary>
		internal void AnnounceRenameIfHost() => AnnounceSpawnIfHost();

		/// <summary>
		/// Why an announcement did not go out, by reason.
		///
		/// The host named a BasicPlantFood, registered it, and the client's log has no
		/// mention of that id anywhere - and 104 loose items ended a run in that state
		/// while 178 announcements did go out. Every gate in this method looks like it
		/// should have passed for a Pickupable, which is exactly the situation where
		/// reading the code has been wrong in this project and counting has not.
		///
		/// One counter per return, so the next run names the gate instead of leaving it
		/// to deduction.
		/// </summary>
		/// <summary>
		/// Whether this object's existence was ever sent to the clients.
		///
		/// On the object rather than in a log, because the log cannot answer it. The
		/// registration message is routed through NetIdHelper.Note, which is silent when
		/// the caller asks for quiet - so "no registration line in the host log" was read
		/// as "this object was named at load time" when it equally means "the call site
		/// passed quiet: true". Reading absence from a log as a fact is a mistake this
		/// project has already paid for four times.
		///
		/// Deliberately not serialised. It describes this session's traffic, and a
		/// reloaded save has announced nothing to anybody.
		/// </summary>
		[SkipSaveFileSerialization]
		public bool WasAnnounced { get; private set; }

		public static int AnnounceSkippedNoId { get; private set; }
		public static int AnnounceSkippedNotHost { get; private set; }
		public static int AnnounceSkippedNotReplicated { get; private set; }

		/// <summary>
		/// How many of those refusals were plants, by GameTags.Plant - the tag the game's
		/// own plant template always adds, unlike Growing, which only crops get.
		/// </summary>
		public static int AnnounceSkippedPlant { get; private set; }
		public static int AnnounceSent { get; private set; }

		/// <summary>
		/// Announcements not repeated because this object was already announced under this
		/// same id.
		///
		/// WasAnnounced existed and nothing read it, which is the shape this file already
		/// documents for a flag that eight call sites toggled and nobody consulted. It
		/// matters more now than it did: an announcement asks the far peer to build the
		/// object, and the adoption path that would otherwise absorb a repeat only adopts
		/// unnamed client previews - so a second announcement for an object the client
		/// already has produces a second object, not a no-op.
		///
		/// ReattachAll calls RegisterIdentity on everything it finds unfiled, and
		/// RegisterIdentity reaches the announcement. Reattachment has measured zero on
		/// both peers in every run so far, so this is a hole nothing has fallen into yet
		/// rather than a fault being fixed - and the reason to close it now is that the
		/// next commit puts plants through here, which turns "unlikely" into "458 objects".
		///
		/// Keyed on the id rather than on the flag alone, because a rename has to be
		/// announced: AnnounceRenameIfHost comes through this same method deliberately, and
		/// blocking it would make the peers disagree about the name instead.
		/// </summary>
		public static int AnnounceSkippedRepeat { get; private set; }

		/// <summary>The id this object was last announced under, so a rename still gets through.</summary>
		[SkipSaveFileSerialization]
		private int _announcedNetId;

		private void AnnounceSpawnIfHost()
		{
			if (NetId == 0) { AnnounceSkippedNoId++; return; }
			if (!MultiplayerSession.IsHost || !MultiplayerSession.InSession)
			{
				// Only counted while this peer is the host in a session. Off-session
				// registrations are the normal state during load and would swamp the
				// number with something that is not the question being asked.
				if (MultiplayerSession.IsHost) AnnounceSkippedNotHost++;
				return;
			}
			if (!NeedsReplication())
			{
				AnnounceSkippedNotReplicated++;

				// Plants, separately, because the three instruments that would have shown
				// this one are all blind to it.
				//
				// Growing.OnSpawn, the PlantablePlot postfix and PlantTracker.AllPlants -
				// which is the health row's plants= - every one of them requires a Growing
				// component, and the plant that actually diverges is a Wheezewort, which
				// has none: EntityTemplates.ExtendEntityToBasicPlant adds GameTags.Plant to
				// every plant and adds Growing only to the ones that grow a crop. So a run
				// reported plantSeen=0, plantPlot=0 and plants=458 while the host was
				// refusing to replicate a plant, and all three numbers were consistent with
				// nothing being wrong.
				//
				// GameTags.Plant is the game's own answer to "is this a plant", taken from
				// the template rather than picked. This counter is what makes the next
				// attempt at replicating plants judgeable at all: it is the size of the gap
				// now, and it has to fall to zero when the filter changes.
				if (gameObject.HasTag(GameTags.Plant)) AnnounceSkippedPlant++;
				// Sixty rather than ten. Ten was enough to see that the first ones were
				// buildings and placers, and that reading turned out to be a sample of
				// the head rather than a description of the set - the loose items that
				// are actually missing on the client could have been anywhere in the
				// remaining thirty-one.
				if (AnnounceSkippedNotReplicated <= 60)
				{
					DebugTools.ThrottledLog.Warn(
						$"[Announce] not replicating '{SafePrefabName}' (NetId {NetId}) - " +
						$"building={TryGetComponent<Building>(out _)} " +
						$"minion={gameObject.HasTag(GameTags.BaseMinion)} " +
						$"pickupable={TryGetComponent<Pickupable>(out _)} " +
						$"navigator={TryGetComponent<Navigator>(out _)}");
				}
				return;
			}

			// Already told them about this object under this number - see AnnounceSkippedRepeat.
			if (WasAnnounced && _announcedNetId == NetId)
			{
				AnnounceSkippedRepeat++;
				return;
			}

			AnnounceSent++;
			WasAnnounced = true;
			_announcedNetId = NetId;

			// One line per announcement, by name and id.
			//
			// Three loose items were traced by hand this run - the host registered each
			// BasicPlantFood, the client's log has no mention of the id anywhere, and
			// the totals said 208 announcements went out. Which of those two facts is
			// about these three objects could not be answered, because nothing recorded
			// what was announced, only how many. Two hundred lines in a fifty-thousand
			// line log is a cheap price for being able to grep an id and get an answer
			// instead of a deduction.
			DebugConsole.Log($"[Announce] sent {SafePrefabName}#{NetId}");

			Bump(_announcedByPrefab, StripInstanceId(gameObject.name ?? "?"));

			Misc.World.InstantiationBatcher.Queue(new Packets.InstantiationsPacket.InstantiationEntry
			{
				NetId = NetId,
				PrefabName = TryGetComponent<KPrefabID>(out var kpid) ? kpid.PrefabTag.Name : gameObject.name,
				Position = transform.position,
				Rotation = transform.rotation,
				ObjectName = gameObject.name,
				InitializeId = true,
				GameLayer = gameObject.layer
			});
		}

		/// <summary>
		/// Unnamed local objects, by the cell they were drawn at.
		///
		/// A client draws what it is told to build or dig immediately and does not
		/// mint an id, waiting to be named. Nothing was matching those up: when the
		/// host's announcement arrived, the receiver built a *second* object from the
		/// prefab name and named that one, leaving the client's own copy in the world
		/// with no id at all. Measured over eleven minutes of real play: 577 previews
		/// created, 38 adopted, and the registry's failed-lookup counter climbing
		/// from 270 to 3925 over the same rows - one number is the other's cause.
		/// The warnings are all of a kind ("pickup arrived for an item this peer does
		/// not have"), because the item the host means and the item the client is
		/// holding are two different objects.
		///
		/// Indexed by cell so an arriving announcement can find the object already
		/// standing there instead of adding another. Entries are removed when the
		/// object is named, when it is destroyed, and when the cell turns out to be
		/// stale - things get carried, and an index that lies is worse than none.
		/// </summary>
		private static readonly Dictionary<int, List<NetworkIdentity>> _previewsByCell =
			new Dictionary<int, List<NetworkIdentity>>();

		public static int PreviewsIndexed { get; private set; }
		public static int PreviewsAdoptedByCell { get; private set; }
		public static int PreviewsStale { get; private set; }

		private int _indexedCell = -1;

		private void IndexPreview()
		{
			if (Grid.WidthInCells == 0) return;

			int cell = Grid.PosToCell(gameObject);
			if (!Grid.IsValidCell(cell)) return;

			if (!_previewsByCell.TryGetValue(cell, out var list))
			{
				list = new List<NetworkIdentity>();
				_previewsByCell[cell] = list;
			}
			list.Add(this);
			_indexedCell = cell;
			PreviewsIndexed++;
		}

		private void UnindexPreview()
		{
			if (_indexedCell < 0) return;
			if (_previewsByCell.TryGetValue(_indexedCell, out var list))
			{
				list.Remove(this);
				if (list.Count == 0) _previewsByCell.Remove(_indexedCell);
			}
			_indexedCell = -1;
		}

		/// <summary>
		/// Give an already-drawn local object the host's id, instead of building a
		/// second one beside it. Returns false if there is nothing here that matches,
		/// in which case the caller should create the object as before.
		/// </summary>
		/// <summary>
		/// Announcements that found no candidate at the announced cell, but would have
		/// found one at a neighbouring cell.
		///
		/// A measurement, not a behaviour: nothing is adopted on the strength of it. The
		/// remaining unpaired loose items are matter each peer's own simulation made -
		/// LiquidSourceManager.CreateChunk and Substance.SpawnResource under
		/// Game.StepTheSim - and the obvious explanation for adoption missing them is
		/// that a pile settles one cell apart on the two peers. Obvious explanations in
		/// this project have been wrong about as often as they have been right, and
		/// widening the match is the change that already went wrong once: matching too
		/// loosely renamed a pile that was holding the host's own id and produced eight
		/// thousand failed lookups from one object.
		///
		/// So the question gets a number first. If this reads near zero, the cell is not
		/// what is missing and widening the search would cost that risk for nothing.
		/// </summary>
		public static int AdoptionsMissedByOneCell { get; private set; }

		/// <summary>
		/// How far the nearest unnamed preview of the right kind was, when adoption
		/// failed. Buckets rather than a total, because the question is what radius
		/// would work and a mean cannot answer it.
		///
		/// The one-cell check answered 0 every run, which was read as "position is not
		/// the problem" - and it is not evidence for that at all: it searched the
		/// by-cell index, and if the client's copy is indexed at a cell more than one
		/// step away the search sees nothing whether the object is two cells off or
        /// on the other side of the map.
		/// </summary>
		public static int AdoptMissNone { get; private set; }
		public static int AdoptMissWithin2 { get; private set; }
		public static int AdoptMissWithin8 { get; private set; }
		public static int AdoptMissFar { get; private set; }

		/// <summary>
		/// The nearest unnamed preview of this prefab, in grid steps, or -1 if there is
		/// none anywhere. Walks every indexed preview - a few hundred at most, and only
		/// on the failure path.
		/// </summary>
		private static void RecordNearestUnnamed(int cell, string prefabName)
		{
			if (!Grid.IsValidCell(cell)) return;

			int best = int.MaxValue;
			Grid.CellToXY(cell, out int x0, out int y0);

			foreach (var kvp in _previewsByCell)
			{
				foreach (var candidate in kvp.Value)
				{
					if (candidate.IsNullOrDestroyed() || candidate.gameObject.IsNullOrDestroyed()) continue;
					if (!candidate.IsClientPreview || candidate.NetId != 0) continue;
					if (candidate.SafePrefabName != prefabName) continue;

					int here = Grid.PosToCell(candidate.gameObject);
					if (!Grid.IsValidCell(here)) continue;

					Grid.CellToXY(here, out int x1, out int y1);
					int dx = x1 > x0 ? x1 - x0 : x0 - x1;
					int dy = y1 > y0 ? y1 - y0 : y0 - y1;
					int steps = dx > dy ? dx : dy;
					if (steps < best) best = steps;
				}
			}

			if (best == int.MaxValue) AdoptMissNone++;
			else if (best <= 2) AdoptMissWithin2++;
			else if (best <= 8) AdoptMissWithin8++;
			else AdoptMissFar++;
		}

		private static bool WouldMatchNearby(int cell, string prefabName)
		{
			if (!Grid.IsValidCell(cell)) return false;

			// The four the game actually has. Grid offers CellAbove, CellBelow, CellLeft
			// and CellRight and no diagonals - checked against the assembly rather than
			// assumed, after assuming produced four compile errors in one go.
			//
			// The diagonals are reachable as combinations and are deliberately not
			// walked. A pile that settles is one step away, and every cell added to the
			// search is another chance to match the wrong pile.
			foreach (int neighbour in new[]
			{
				Grid.CellAbove(cell), Grid.CellBelow(cell),
				Grid.CellLeft(cell), Grid.CellRight(cell),
			})
			{
				if (!Grid.IsValidCell(neighbour)) continue;
				if (!_previewsByCell.TryGetValue(neighbour, out var near)) continue;

				foreach (var candidate in near)
				{
					if (candidate.IsNullOrDestroyed() || candidate.gameObject.IsNullOrDestroyed()) continue;
					if (!candidate.IsClientPreview || candidate.NetId != 0) continue;
					if (candidate.SafePrefabName != prefabName) continue;
					return true;
				}
			}
			return false;
		}

		public static bool TryAdoptPreview(int cell, string prefabName, int netId)
		{
			if (netId == 0 || string.IsNullOrEmpty(prefabName)) return false;
			if (!_previewsByCell.TryGetValue(cell, out var list) || list.Count == 0)
			{
				if (WouldMatchNearby(cell, prefabName)) AdoptionsMissedByOneCell++;
				RecordNearestUnnamed(cell, prefabName);
				return false;
			}

			for (int i = list.Count - 1; i >= 0; i--)
			{
				var candidate = list[i];

				// A destroyed or moved candidate is not a candidate, and leaving it
				// indexed would make this answer wrong later as well.
				if (candidate.IsNullOrDestroyed() || candidate.gameObject.IsNullOrDestroyed()
					|| Grid.PosToCell(candidate.gameObject) != cell)
				{
					list.RemoveAt(i);
					PreviewsStale++;
					continue;
				}

				// Nameless candidates only, which is what it was before I widened it.
				//
				// Letting a provisionally named object be adopted was meant to fix 3,786
				// failed lookups and did not - that count was the same in the build
				// before it. What it did do was let an announcement rename an object
				// that was already right: the host had Cuprite at -428056059, the client
				// had computed the same number for the same pile, and the announcement
				// for a *second* pile in that cell matched by cell and prefab and moved
				// the first one to -428056058. Nothing held the host's id after that, and
				// one object accounted for eight thousand failures.
				//
				// "Only objects with no name" is not a limitation here, it is the
				// protection. An object that already agrees with the host must not be
				// renamed by an announcement meant for its neighbour.
				if (!candidate.IsClientPreview || candidate.NetId != 0)
				{
					// Counted, because this is the protection working and I removed it
					// once already. Each one is an announcement that wanted to rename an
					// object which already had a name - and the one time that was
					// allowed, a pile holding the host's own id was moved off it and one
					// object produced eight thousand failed lookups.
					if (candidate.NetId != 0) AdoptionsRefusedNamed++;
					continue;
				}
				// Safe name here too. A candidate is normally a KInstantiate'd prefab and
				// has a KPrefabID, but "normally" is what the crash was.
				if (candidate.SafePrefabName != prefabName)
					continue;

				candidate.OverrideNetId(netId);
				PreviewsAdoptedByCell++;
				return true;
			}

			// The cell had candidates and none of them fit. Same question as the empty
			// case: was there one next door?
			if (WouldMatchNearby(cell, prefabName)) AdoptionsMissedByOneCell++;
			RecordNearestUnnamed(cell, prefabName);

			if (list.Count == 0) _previewsByCell.Remove(cell);
			return false;
		}

		public static void ResetPreviewIndex()
		{
			_previewsByCell.Clear();
			PreviewsIndexed = 0;
			PreviewsAdoptedByCell = 0;
			PreviewsStale = 0;
		}

		/// <summary>
		/// Objects the peers must agree on by name: anything that can be picked
		/// up, hauled or interacted with across the link. Buildings and conduits
		/// are excluded - they already replicate through their own paths, and
		/// including them would make this scale with the whole colony.
		/// </summary>
		private bool NeedsReplication()
		{
			if (TryGetComponent<Building>(out _)) return false;

			// Never duplicants. This announcement asks the other peer to build the
			// object from a prefab name, and a duplicant is not reconstructible that
			// way: personality, traits, aptitudes and name live in the immigrant
			// data, not in the prefab. Instantiating "Minion" by name produces a
			// colonist with personality 0x0, and the renderer cannot draw one -
			// FaceGraph reads the personality inside World.LateUpdate, so it throws
			// every frame from then on.
			//
			// A client died of exactly this: "Could not find Personality: 0x0"
			// repeating with NullReferenceExceptions between them, the null-Gametag
			// warnings that follow a colonist with no tags, and the game closing
			// itself a second and a half later. Three sessions ended that way.
			//
			// Duplicants carry a Navigator, which is what let them in here. They
			// replicate through the telepad path, which carries what they are made
			// of.
			if (gameObject.HasTag(GameTags.BaseMinion)) return false;

			if (TryGetComponent<Pickupable>(out _) || TryGetComponent<Navigator>(out _))
				return true;

			// Anything the host sends packets about, it also has to announce.
			//
			// RepairableStorageProxy is the object a duplicant actually works on to
			// repair a building. The host creates one, gives it an id, sends
			// StandardWorker_WorkingState_Packet and WorkableProgressPacket addressed to
			// that id - and did not announce it, because it is not a Pickupable and has
			// no Navigator. The client's log is the whole story in three lines: "NetId
			// 1064133649 not found, asked by StandardWorker_WorkingState_Packet",
			// "Could not resolve workable 1064133649 for worker", "not found, asked by
			// WorkableProgressPacket".
			//
			// The visible cost is 22 wires that the client still believes need work
			// while the host has them repaired - the last differing rows in the priority
			// comparison, every one of them the active flag and every one of them a
			// Wire. The client's repair chore has nothing to tell it the job is done.
			//
			// The rule this restores is simple and was already implied: the set of
			// objects a peer addresses and the set it announces have to be the same set.
			// A Workable is precisely what those two packets address.
			//
			// The client normally has its own copy already - Repairable.CreateStorageProxy
			// runs there too - so the announcement is usually satisfied by adoption
			// rather than by building a second one.
			//
			// Narrowed to the repair proxy after "any Workable" killed a client.
			//
			// Announcing means the other peer may rebuild the object from its prefab
			// name, and plenty of workables cannot survive that. A BalloonStand is one:
			// the host announced it, the client instantiated it, and
			// BalloonStandConfig.OnSpawn threw a NullReferenceException on an object that
			// had been assembled rather than grown. The game logged it at ERROR and shut
			// the client down 1.4 seconds later, which is the same ending this file
			// already documents for duplicants - "a colonist cannot be rebuilt from a
			// prefab name" - and I walked another object into it while widening this
			// line.
			//
			// So the rule is not "everything that is addressed" after all. It is "the
			// thing this was written for": the repair proxy, which is created identically
			// on both peers by Repairable.CreateStorageProxy and is therefore always
			// adopted rather than built. Anything else that turns out to need announcing
			// has to earn it one type at a time, with the rebuild path checked first.
			return GetComponent<RepairableStorageProxy>() != null;

			// PLANTS: TRIED TWICE, REVERTED TWICE. The reason is below and it is not the
			// one either attempt assumed.
			//
			// A duplicant sowed a Wheezewort during a run: the host ended with 19
			// ColdBreathers and the client with 18, the missing one exactly the cell that
			// was sown, and this method is what turned it down - the host's own log says
			// "not replicating 'ColdBreather' - building=False minion=False
			// pickupable=False navigator=False". A plant is none of the four. The 458
			// plants that do agree came from the save, which both peers load, so a count
			// of plants never showed this and only sowing during a session does.
			//
			// It is the same shape as the artwork that finished on the host and stayed
			// blank on the client: the order is replicated, the completion is not, and the
			// client cannot produce the completion itself because its duplicants do not
			// work.
			//
			// The rebuild path WAS checked first, as the paragraph above requires, and the
			// check still gave the wrong answer - which is the part worth keeping.
			//
			// "spawn-probe ColdBreather keep" instantiated the prefab on a live peer and
			// nothing threw; SwampLily and BasicSingleHarvestPlant too. So the line went in
			// with Uprootable as the marker, and the run measured this:
			//
			//   client errors   0 -> 159
			//   AnimEventHandler.UpdateOffset -> KAnimControllerBase.GetPivotSymbolPosition
			//                   -> Component.get_transform    NullReference, 52 frames
			//   Game.StopBE -> Component.get_gameObject       NullReference, 53 frames
			//
			// The three runs before it had 0 and the same four test-scope lines, so the
			// test suite is ruled out as the source.
			//
			// The replication worked, which makes the failure sharper rather than softer:
			// the host announced ColdBreather#1895125411 and the client logged "Registered
			// overridden NetId 1895125411 for ColdBreather(Clone)" in the same second. The
			// object arrived. It then threw from the animation and game-object teardown
			// paths every frame afterwards.
			//
			// What the probe actually measured was instantiation ON THE HOST, in a game
			// that grew that colony. The question was whether the CLIENT can carry one, and
			// the client is a peer with its AI switched off and half its systems in a
			// different state. That is the same distinction BalloonStand died on, and I
			// re-ran into it while believing I had checked. A probe on the wrong peer is
			// not a check.
			//
			// If this is attempted again: probe on the client, leave the object standing
			// for several minutes, and watch the error count rather than the return value -
			// the exception here is thrown by Unity's own LateUpdate, so nothing at the
			// call site would ever see it.
			//
			// What changed since: the client's leftover planting ghost is now removed before
			// the plant is built - see InstantiationsPacket.RemovePlantingGhost. Re-reading
			// the failed run supports that being the fault rather than the plant itself:
			// the client DID create ColdBreather(Clone) under the host's id at 04:05:48, its
			// own dump still listed ColdBreather_preview at that cell at 04:09:09, the
			// plant's id read UNRESOLVED at 04:09:10, and the exception storm began at
			// 04:09:12. The plant was built, then destroyed, and the teardown threw - which
			// is also exactly what spawn-probe measured when it destroyed what it built, and
			// what it measured as harmless when it did not.
			//
			// So two objects in one cell was the fault, not building a plant.
			//
			// THE SECOND ATTEMPT AND WHAT IT SETTLED
			//
			// The theory was that the client's leftover planting ghost collided with the new
			// plant in one cell, so the ghost was removed first. It never fired -
			// ghostsCleared read 0 across four runs, first because the layer was guessed and
			// then because every layer was walked and the object still was not found.
			//
			// cell-dump on the client answered it: cell 53105 holds ZERO objects on ZERO
			// layers, while that same client has registered "ColdBreather_preview at cell
			// 53105". The ghost exists as a GameObject and is not in Grid.Objects at all, so
			// there was never a collision to remove. Both attempts were aimed at a state
			// that does not exist.
			//
			// What is actually wrong is one line in InstantiationsPacket:
			//
			//     GameObject obj = Object.Instantiate(prefab, e.Position, e.Rotation);
			//
			// That makes a GameObject. It does not put it in Grid.Objects, which is what the
			// game's own placement does and what everything downstream reads - the circuit
			// walk, the state dump, the plant scans. So the client ends up with a plant that
			// is not anywhere, invisible to every check, and whatever later collects it is
			// the teardown that threw 159 errors on the first attempt.
			//
			// This also retires the first probe's answer. "spawn-probe ColdBreather keep"
			// threw nothing, and that was read as "a plant can be built here". It only
			// showed that instantiating does not throw - not that the result is a working
			// plant, which it is not.
			//
			// A third attempt needs the game's own plant placement rather than
			// Object.Instantiate, and that path has to be read out of the assembly rather
			// than guessed. Until then plants stay unreplicated, which costs one plant a
			// session and no errors, against an attempt that has twice produced objects the
			// client cannot use.
		}

		/// <summary>
		/// This will be primarily used when the host spawns in an object and the client and host need to sync the netid
		/// </summary>
		/// <param name="netIdOverride"></param>
		/// <summary>Sequence of the packet that last named this object.</summary>
		[SkipSaveFileSerialization]
		private int _namedBySequence = int.MinValue;

		/// <summary>
		/// <paramref name="caller"/> and <paramref name="callerFile"/> are filled in
		/// by the compiler, so every rename says who asked for it.
		///
		/// Needed because the log already proves a rename loop and cannot name its
		/// source. One critter was renamed four times in a session - 334932354 to
		/// 1079163616 to 1744268860 to -605060782 to -360805404, all "named by the
		/// host" - while the host's own log records no id movement at all. Every
		/// rename unregisters the previous id, so packets still stamped with it miss:
		/// one such id accumulated thousands of failed lookups from
		/// EntityPositionPacket and VitalStatsPacket, and a dead duplicant's
		/// carried-corpse animation could not be turned off because the id it was
		/// addressed by no longer resolved.
		///
		/// Thirteen call sites can rename an object and none of them, read on its
		/// own, explains renaming the same one four times. Four rounds of deduction
		/// about a different id drift got nowhere; the registry's own lookup failures
		/// were only diagnosable once they carried the asking method. Same technique
		/// here.
		/// </summary>
		public void OverrideNetId(int netIdOverride,
			[System.Runtime.CompilerServices.CallerMemberName] string caller = "",
			[System.Runtime.CompilerServices.CallerFilePath] string callerFile = "")
		{
			using var _ = Profiler.Scope();

			_lastNamedBy = string.IsNullOrEmpty(callerFile)
				? caller
				: $"{System.IO.Path.GetFileNameWithoutExtension(callerFile)}.{caller}";

			// Whatever name this object gave itself is no longer provisional: it has
			// been told one. Cleared here rather than at each call site so no future
			// naming path can forget to.
			IdIsProvisional = false;

			// Naming the same object over and over cannot be right, so stop doing it.
			//
			// One critter took four different ids in a session and each rename
			// unregistered the one before, so every packet still addressed by an
			// earlier id missed - thousands of failed lookups from EntityPositionPacket
			// and VitalStatsPacket against a single id, and a dead duplicant's carried
			// animation that could not be switched off because its address no longer
			// resolved.
			//
			// Which caller does this is not known yet; the attribution above will say
			// so in the next session's log. But the id the object holds now is as
			// likely to be right as the next one, and continuing to trade guarantees
			// that half the traffic is addressed to a name nobody answers. Keeping the
			// first name bounds the damage to one object instead of spreading it over
			// every id that object ever held.
			//
			// Capping a fight rather than winning it is what stopped the plant naming
			// loop, which had reached 460 renames a minute and was making the game
			// slower the longer it ran.
			if (_renames >= RenameAlarm && NetId != 0 && NetId != netIdOverride)
			{
				_renamesRefused++;
				if (_renamesRefused <= 5 || _renamesRefused % 100 == 0)
				{
					DebugConsole.LogWarning(
						$"[IdMove] refusing to rename '{SafePrefabName}'#{GetInstanceID()} " +
						$"from {NetId} to {netIdOverride} ({_lastNamedBy}): it has already been renamed " +
						$"{_renames} times and each rename drops the previous address. Keeping {NetId}. " +
						$"({_renamesRefused} refusals so far)");
				}
				return;
			}

			// Refuse a name older than the one already applied.
			//
			// Without this the last packet to arrive wins whatever order it was
			// sent in, and the same object could be named twice by a spawn
			// announcement and a periodic sweep racing each other. That is why
			// every attempt to announce spawns faster - 500 ms, 100 ms,
			// immediately - turned netid_compare from agreeing to disagreeing
			// while the 2 s interval happened to keep them apart.
			int sequence = Packets.Architecture.PacketHandler.CurrentSequence;
			if (_namedBySequence != int.MinValue && sequence - _namedBySequence < 0)
			{
				DebugConsole.Log(
					$"[NetworkIdentity] ignoring stale naming of {gameObject.name}: " +
					$"packet {sequence} is older than {_namedBySequence}");
				return;
			}
			_namedBySequence = sequence;

			// The host has named this object, so it is no longer a preview.
			if (IsClientPreview)
			{
				IsClientPreview = false;
				PreviewsAdopted++;
				Bump(_previewsAdoptedByPrefab, StripInstanceId(gameObject.name ?? "?"));
				UnindexPreview();
			}

			// Unregister old NetId
			NetworkIdentityRegistry.Unregister(NetId, this);

			// Override internal value
			NoteIdChange(NetId, netIdOverride, "named by the host");
			NetId = netIdOverride;

			// Re-register with new NetId
			NetworkIdentityRegistry.RegisterOverride(this, netIdOverride);

			// RegisterOverride can decline - it refuses id 0 outright - and it used
			// to be assumed to have filed the object. If it did not, the field holds
			// an id nobody can look this object up by, which is exactly the state
			// one pair of objects lands in on every run of this save.
			if (!NetworkIdentityRegistry.Holds(netIdOverride, this))
			{
				DebugConsole.LogWarning(
					$"[NetworkIdentity] '{SafePrefabName}' was told it is NetId {netIdOverride}, " +
					"but the registry did not file it there. It now believes an id nothing can " +
					"resolve; unregistering it so the next attempt starts clean.");
				NoteIdChange(netIdOverride, 0, "registry declined the host's name");
				NetId = 0;
				IsRegistered = false;
			}

			//DebugConsole.Log($"[NetworkIdentity] Overridden NetId. New NetId = {NetId} for {gameObject.name}");
		}


		public override void OnCleanUp()
		{
			using var _ = Profiler.Scope();

			RemoteProgressRegistry.Clear(NetId);
			NetworkIdentityRegistry.Unregister(NetId, this);
			// A destroyed object must leave the preview index with it, or an
			// announcement later adopts a corpse and the real object is built twice
			// anyway.
			UnindexPreview();
			//DebugConsole.Log($"[NetworkIdentity] Unregistered NetId {NetId} for {gameObject.name}");
			base.OnCleanUp();
		}
	}
}
