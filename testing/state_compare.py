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

def read(path):
    """Last dump wins - a log can hold several runs of the suite."""
    state, damage = {}, {}
    with open(path, encoding='utf-8', errors='ignore') as fh:
        for line in fh:
            m = STATE.search(line)
            if m:
                netid, syncer, key, value = m.groups()
                state[(netid, syncer.strip(), key.strip())] = value.strip()
                continue
            m = DAMAGE.search(line)
            if m:
                netid, prefab, cell, hp, maxhp = m.groups()
                damage[(netid, prefab.strip())] = (cell, int(hp), int(maxhp))
    return state, damage

# Values that drift by their nature. Comparing them produces noise that buries
# the real findings, which is how a comparison stops being read at all.
NOISY = {
    'total_water', 'total_waste', 'total_gunk',      # fill levels move continuously
    'timeElapsed', 'time_since_meltdown', 'time_since_meltdown_emit',
    'emit_rads', 'water_meter_percent', 'temperature_meter_percent',
    'input_mass',
}

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('host'); ap.add_argument('client')
    ap.add_argument('--all', action='store_true', help='include continuously drifting values')
    args = ap.parse_args()

    hs, hd = read(args.host)
    cs, cd = read(args.client)

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
        elif h != c:    differs.append((k, h, c))

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

    # Damage is separate because it is what a player sees first.
    dkeys = set(hd) | set(cd)
    dbad = []
    for k in sorted(dkeys):
        h, c = hd.get(k), cd.get(k)
        if h != c:
            dbad.append((k, h, c))
    print(f'\ndamaged buildings: host {len(hd)}, client {len(cd)}, disagreeing {len(dbad)}')
    for (netid, prefab), h, c in dbad[:15]:
        fmt = lambda v: 'undamaged' if v is None else f'{v[1]}/{v[2]} at cell {v[0]}'
        print(f'    netid {netid:>12} {prefab}: host={fmt(h)} client={fmt(c)}')

    return 1 if (differs or dbad) else 0

if __name__ == '__main__':
    sys.exit(main())
