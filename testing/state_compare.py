"""Compare the two peers' own accounts of their world state.

Everything the in-game suite can assert is local: ids do not collide, keys have
readers, packets fit. None of it can answer whether the two peers agree, because
that is not a property either box can see. So each box states its view as
[STATE] and [DAMAGE] lines and this compares them exactly.

Reads two Player.logs and reports, per NetId and key:
  differs  - both peers have it and disagree     (the finding)
  host-only / client-only - one peer has state the other never produced
Exit code 1 when anything differs, so it can gate a run.
"""
import re, sys, argparse, collections

STATE = re.compile(r'\[STATE\] (-?\d+)\|([^|]+)\|([^|]+)\|(.*)$')
DAMAGE = re.compile(r'\[DAMAGE\] (-?\d+)\|([^|]+)\|(\d+)\|(\d+)/(\d+)')
NETID = re.compile(r'\[NETID\] ([^|]+)\|([^|]+)\|(\d+)\|(-?\d+)')


TIME = re.compile(r'^\[(\d\d):(\d\d):(\d\d)\.(\d\d\d)\]')

# One dump is written inside a single frame, so its lines are milliseconds
# apart while consecutive dumps are seconds apart. Anything past this is a
# different run of the suite.
DUMP_GAP_SECONDS = 2.0


def stamp(line):
    m = TIME.match(line)
    if not m:
        return None
    h, mi, s, ms = (int(x) for x in m.groups())
    return h * 3600 + mi * 60 + s + ms / 1000.0


class Blocks:
    """One dict per run of the suite, so a stale dump cannot be read as state.

    The suite can run more than once in a session - the client ran it twice in
    the run this was written for - and merging dicts across runs keeps entries
    that had already disappeared. That does not read as "gone", it reads as
    "one peer has state the other does not", which is the exact finding this
    tool exists to report.

    Blocks are cut on elapsed time rather than on a repeated key. Keying by
    (kind, prefab, cell) legitimately repeats inside one dump - stacked items
    share a cell - so a repeat cut the dump into fragments, and the last
    fragment held a couple of items and no buildings at all. That printed
    "buildings: host 0, client 0" while both logs carried thousands, and it
    made every ABSENT verdict below vacuous: nothing is present in an empty
    table. Same class of mistake as the thing it was written to catch.
    """

    def __init__(self):
        self.blocks = [{}]
        self._last_t = None

    def add(self, key, value, t):
        if t is not None and self._last_t is not None and t - self._last_t > DUMP_GAP_SECONDS:
            self.blocks.append({})
        if t is not None:
            self._last_t = t
        self.blocks[-1][key] = value

    @property
    def last(self):
        return self.blocks[-1]


def read(path):
    """The most recent dump of each kind."""
    state, damage, netid = Blocks(), Blocks(), Blocks()
    with open(path, encoding='utf-8', errors='ignore') as fh:
        for line in fh:
            m = STATE.search(line)
            if m:
                nid, syncer, key, value = m.groups()
                state.add((nid, syncer.strip(), key.strip()), value.strip(), stamp(line))
                continue
            m = DAMAGE.search(line)
            if m:
                nid, prefab, cell, hp, maxhp = m.groups()
                damage.add((nid, prefab.strip()), (cell, int(hp), int(maxhp)), stamp(line))
                continue
            m = NETID.search(line)
            if m:
                kind, prefab, cell, nid = m.groups()
                netid.add((kind.strip(), prefab.strip(), cell), nid, stamp(line))
    return state.last, damage.last, netid.last

# Values that drift by their nature. Comparing them produces noise that buries
# the real findings, which is how a comparison stops being read at all.
NOISY = {
    'total_water', 'total_waste', 'total_gunk',      # fill levels move continuously
    'timeElapsed', 'time_since_meltdown', 'time_since_meltdown_emit',
    'emit_rads', 'water_meter_percent', 'temperature_meter_percent',
    'input_mass',
}

def same_value(a, b):
    """Equal enough to mean the peers agree.

    Storage is emitted as element:mass pairs, and the two peers sample a running
    simulation at slightly different instants - a toilet fills, a conduit moves
    gas. The first run of this reported two storages as divergent over 0.1 kg in
    one element out of fifteen, with everything else identical to the gram. That
    is the sampling gap, not a desync, and a comparison that reports it is one
    nobody will read twice.

    Composition still has to match exactly: an element present on one side and
    absent on the other is a real finding at any mass.
    """
    if a == b:
        return True

    ha, hb = parse_storage(a), parse_storage(b)
    if ha is None or hb is None:
        return False
    if set(ha) != set(hb):
        return False

    for elem, mass_a in ha.items():
        mass_b = hb[elem]
        # A tenth of a kilogram, or a thousandth of the amount, whichever is
        # larger - so a 10 tonne store is not judged to four decimal places.
        tolerance = max(0.1, abs(mass_a) * 0.001)
        if abs(mass_a - mass_b) > tolerance:
            return False
    return True


def parse_storage(text):
    """{element hash: mass} for a storage summary, or None if it is not one."""
    if not text or ':' not in text or text in ('empty',):
        return None
    out = {}
    for part in text.split(','):
        elem, _, mass = part.partition(':')
        try:
            out[elem.strip()] = float(mass)
        except ValueError:
            return None
    return out or None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('host'); ap.add_argument('client')
    ap.add_argument('--all', action='store_true', help='include continuously drifting values')
    args = ap.parse_args()

    hs, hd, hn = read(args.host)
    cs, cd, cn = read(args.client)

    if not hs and not cs:
        print('no [STATE] lines in either log - run the in-game suite first (Divergence category)')
        return 2

    keys = set(hs) | set(cs)
    differs, host_only, client_only = [], [], []
    for k in sorted(keys):
        if not args.all and k[2] in NOISY:
            continue
        h, c = hs.get(k), cs.get(k)
        if h is None:   client_only.append((k, c))
        elif c is None: host_only.append((k, h))
        elif not same_value(h, c): differs.append((k, h, c))

    print(f'structure state: host {len(hs)} values, client {len(cs)} values')
    print(f'  differ      {len(differs)}')
    print(f'  host only   {len(host_only)}')
    print(f'  client only {len(client_only)}')

    if differs:
        print('\n=== the two peers disagree ===')
        by_key = collections.Counter(k[2] for k, _, _ in differs)
        for key, n in by_key.most_common(10):
            print(f'  {key:<24} {n}')
        print('\n  first 15:')
        for (netid, syncer, key), h, c in differs[:15]:
            print(f'    netid {netid:>12} {syncer}.{key}: host={h} client={c}')

    if host_only or client_only:
        print('\n=== one peer only ===')
        for (netid, syncer, key), v in (host_only + client_only)[:10]:
            side = 'host' if (netid, syncer, key) in hs else 'client'
            print(f'    {side}-only  netid {netid:>12} {syncer}.{key} = {v}')

    # Which buildings each peer has at all, keyed by where and what rather than
    # by id: the ids are the thing under test and two peers can disagree about
    # them while holding the same building.
    #
    # This comparison was missing, and its absence produced a false diagnosis.
    # Two Tiles showed up as a damage disagreement reading "host=undamaged
    # client=42/100", which says the host has an intact building. The host did
    # not have the building at all - it had finished digging those cells out,
    # while the client still held a half-destroyed tile that nothing would ever
    # remove. The player sees solid ground where the other sees a tunnel, and
    # the tool called it a hit-point mismatch.
    hb = {(p, c): n for (kind, p, c), n in hn.items() if kind == 'building'}
    cb = {(p, c): n for (kind, p, c), n in cn.items() if kind == 'building'}
    host_missing = sorted(set(cb) - set(hb))
    client_missing = sorted(set(hb) - set(cb))

    # A building site and the building it becomes are different prefabs at one
    # cell, so a peer that has finished construction while the other is still
    # building shows up twice: "client-only GasConduit at 46222" and "host-only
    # GasConduitUnderConstruction at 46222". Reported as phantoms, that reads as
    # two peers holding different worlds. It is one building, mid-construction,
    # seen a moment apart - and mixing it in with the real findings is how the
    # four tiles that mattered took six runs to notice.
    def base_name(prefab):
        for suffix in ('UnderConstruction', 'Complete'):
            if prefab.endswith(suffix) and len(prefab) > len(suffix):
                return prefab[:-len(suffix)]
        return prefab

    host_cells = {(base_name(p), c) for (p, c) in hb}
    client_cells = {(base_name(p), c) for (p, c) in cb}
    in_progress = [(p, c) for (p, c) in host_missing
                   if (base_name(p), c) in host_cells]
    in_progress += [(p, c) for (p, c) in client_missing
                    if (base_name(p), c) in client_cells]
    in_progress_set = set(in_progress)
    host_missing = [k for k in host_missing if k not in in_progress_set]
    client_missing = [k for k in client_missing if k not in in_progress_set]

    print(f'\nbuildings: host {len(hb)}, client {len(cb)}, '
          f'client-only {len(host_missing)}, host-only {len(client_missing)}, '
          f'mid-construction {len(in_progress_set)}')
    for (prefab, cell) in sorted(in_progress_set)[:6]:
        print(f'    mid-construction  {prefab} at cell {cell} '
              f'- the other peer has the same building at a different stage')
    if not hb or not cb:
        print('  (no [NETID] building dump on one side - cannot compare presence)')
    # Both sides of this compare the [NETID] dump, and that dump walks the registry -
    # not the world. An object standing in a colony with no address does not appear in
    # it, so "one peer only" means "one peer has it filed", which is not the same claim.
    #
    # It used to say the client never built or received it. That sentence cost three
    # rounds of this investigation: it was read as missing replication, and the search
    # went looking for a way to send buildings that were never missing. The registry
    # population test says the client had 11,047 addressable objects against the host's
    # 9,632 and had filed 81% of them where the host filed 95%, so the peer with fewer
    # rows here is the one that files less, not the one that has less.
    #
    # The [UNFILED] rows are what settles it for a given cell.
    for (prefab, cell) in host_missing[:10]:
        print(f'    client-filed-only  {prefab} at cell {cell} (netid {cb[(prefab, cell)]}) '
              f'- not in the host registry; check [UNFILED] before calling it absent')
    for (prefab, cell) in client_missing[:10]:
        print(f'    host-filed-only    {prefab} at cell {cell} (netid {hb[(prefab, cell)]}) '
              f'- not in the client registry; check [UNFILED] before calling it absent')

    # Damage is separate because it is what a player sees first.
    def present(buildings, prefab, cell):
        return (prefab, cell) in buildings

    dkeys = set(hd) | set(cd)
    dbad = []
    for k in sorted(dkeys):
        h, c = hd.get(k), cd.get(k)
        if h != c:
            dbad.append((k, h, c))
    print(f'\ndamaged buildings: host {len(hd)}, client {len(cd)}, disagreeing {len(dbad)}')
    for (netid, prefab), h, c in dbad[:15]:
        def fmt(v, buildings, other):
            if v is not None:
                return f'{v[1]}/{v[2]} at cell {v[0]}'
            # Nothing in this peer's damage dump. Distinguish "at full health"
            # from "does not exist here" - they need opposite fixes.
            cell = other[0] if other else None
            if cell is not None and not present(buildings, prefab, cell):
                return 'ABSENT (no such building on this peer)'
            return 'undamaged'
        print(f'    netid {netid:>12} {prefab}: '
              f'host={fmt(h, hb, c)} client={fmt(c, cb, h)}')

    return 1 if (differs or dbad or host_missing or client_missing) else 0

if __name__ == '__main__':
    sys.exit(main())
