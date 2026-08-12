namespace ONI_Together.Networking.Components
{
	/// <summary>
	/// What an object should do about its network id, decided in one place.
	///
	/// This decision used to be five separate conditions in three methods, and every
	/// bug fixed in one of them moved the problem into another. In a single day:
	/// a client minting ids that collided with the host's, a client converging ids
	/// the host had issued, a reservation taken by the wrong object, previews holding
	/// numbers they should not have, and a repair proxy that needed the id the client
	/// had just been forbidden to compute. Each fix read correctly on its own and the
	/// combination did not, because no single place said what the rules were.
	///
	/// So the rules live here, as a pure function of a described situation. No Unity,
	/// no registry, no statics - which is what makes it testable exhaustively rather
	/// than by playing the game and reading logs afterwards.
	/// </summary>
	public enum IdAction
	{
		/// <summary>Already named. Nothing to do.</summary>
		Keep,

		/// <summary>The host reserved an id for this exact spawn; use it.</summary>
		TakeReservation,

		/// <summary>
		/// Use the id the hash implies. Safe when the hash alone produces it -
		/// prefab, cell and workable type are the same on both peers, so both reach
		/// the same number without talking.
		/// </summary>
		TakeDeterministicHash,

		/// <summary>
		/// Stay nameless until the host says otherwise.
		///
		/// The honest state for a client-side object the host has not named: no
		/// address at all is better than an address that means something else on the
		/// other machine.
		/// </summary>
		WaitForHost,
	}

	/// <summary>Everything the decision depends on, and nothing else.</summary>
	public readonly struct IdSituation
	{
		public readonly bool InSession;
		public readonly bool IsHost;

		/// <summary>Drawn locally by a client, waiting to be named.</summary>
		public readonly bool IsClientPreview;

		public readonly int CurrentNetId;

		/// <summary>Non-zero when the host claimed an id for this spawn ahead of it.</summary>
		public readonly int ReservedNetId;

		/// <summary>The id the hash implies, or zero when this kind has none.</summary>
		public readonly int DeterministicId;

		/// <summary>
		/// True when the deterministic id was only reached by walking past occupied
		/// slots.
		///
		/// This is the distinction that took a day to find. The hash itself does not
		/// depend on local state, so both peers compute it alike. The walk does - it
		/// can only see this peer's table - so an id that needed a walk is an id the
		/// two peers have no reason to agree on. Three ids ended up meaning a plant on
		/// one machine and a corpse on the other exactly that way.
		/// </summary>
		public readonly bool DeterministicNeededWalk;

		/// <summary>
		/// Whether the host announces this kind of object, so a name is coming.
		///
		/// The missing distinction, and the expensive one. A client taking the hash was
		/// meant for objects the host never announces - a Repairable's storage proxy,
		/// named from prefab and cell and nothing else. Applied to everything, it also
		/// named the ore piles and carried items that the host *does* announce, and an
		/// object that already has a name is not adoptable: adoption only ever
		/// considered nameless candidates, deliberately, because renaming an object
		/// that already agrees with the host is worse.
		///
		/// So those objects self-named, stopped being adoptable, and never received the
		/// host's number. Failed lookups on the client went from 5 to 3,943 - the host
		/// talking about ids nothing on the client held.
		///
		/// If a name is coming, wait for it. If none is coming, compute it.
		/// </summary>
		public readonly bool HostWillAnnounce;

		public IdSituation(bool inSession, bool isHost, bool isClientPreview,
						   int currentNetId, int reservedNetId,
						   int deterministicId, bool deterministicNeededWalk,
						   bool hostWillAnnounce = false)
		{
			HostWillAnnounce = hostWillAnnounce;
			InSession = inSession;
			IsHost = isHost;
			IsClientPreview = isClientPreview;
			CurrentNetId = currentNetId;
			ReservedNetId = reservedNetId;
			DeterministicId = deterministicId;
			DeterministicNeededWalk = deterministicNeededWalk;
		}
	}

	public static class IdPolicy
	{
		/// <summary>
		/// The whole rule set, in the order the cases exclude each other.
		///
		/// Ordered deliberately: a reservation outranks a hash because the host issued
		/// it for this object, and an id already held outranks everything because
		/// renaming a named object is how the registry ends up filed under one number
		/// while the object believes another.
		/// </summary>
		public static IdAction Decide(in IdSituation s)
		{
			// Already named. Includes every object restored from a save, where NetId
			// is serialised - which is why loading a colony does not renumber it.
			if (s.CurrentNetId != 0)
				return IdAction.Keep;

			// The host claimed this number for this spawn. It outranks the hash even
			// on a client preview: an object arriving with its id already decided is
			// not nameless.
			if (s.ReservedNetId != 0)
				return IdAction.TakeReservation;

			// Playing alone. There is no other peer to disagree with, so the walk is
			// harmless and uniqueness is all that matters.
			if (!s.InSession)
				return s.DeterministicId != 0 ? IdAction.TakeDeterministicHash : IdAction.WaitForHost;

			// The host is the authority. It may walk, and what it lands on becomes the
			// truth that gets propagated.
			if (s.IsHost)
				return s.DeterministicId != 0 ? IdAction.TakeDeterministicHash : IdAction.WaitForHost;

			// A preview is by definition something the host has not named yet, and it
			// is found for adoption by cell rather than by id, so it needs none.
			if (s.IsClientPreview)
				return IdAction.WaitForHost;

			// A client takes the hash only when the hash alone produced it. This is
			// what keeps objects the host never announces addressable - a Repairable's
			// storage proxy is named from prefab, cell and workable type and nothing
			// else, so both peers arrive at the same number on their own. Forbidding
			// this outright broke repairs: the client had no id for the proxy, asked
			// the host, was told "I hold it and cannot send it", and repair state had
			// nowhere to land while the host answered 95 damage queries a minute
			// forever.
			// And only when no name is coming. An object the host announces will be
			// named by that announcement, and naming itself first makes it unadoptable -
			// which is how 5 failed lookups became 3,943.
			// A client computes nothing.
			//
			// Both narrower versions of this were tried against live runs and both cost
			// more than they returned. Taking any pure hash: failed lookups per minute
			// went from about one to 765-2,161. Taking one only when the host would not
			// announce that kind: worse again, because "this kind is announced" is not
			// the same as "this object will be announced" - a Cuprite pile that came out
			// of the save file is an announceable kind that nobody ever announces, so it
			// waited forever.
			//
			// The rule that measured best is the blunt one, and the cost of it is honest
			// and known: objects the host never announces stay unaddressable on a client,
			// which is why repairs do not replicate. That is written down as an open
			// problem rather than papered over by letting the client invent names.
			return IdAction.WaitForHost;
		}
	}
}
