"""Export bounded, identifier-free performance summaries; never copy user data."""
import argparse
import hashlib
import json
import pathlib

p = argparse.ArgumentParser()
p.add_argument('--bench-dir', default='artifacts/bench')
p.add_argument('--publish', default='artifacts/release/ServerTray-win-x64')
p.add_argument('--output', default='docs/measurements/summary.json')
a = p.parse_args()
bench = pathlib.Path(a.bench_dir)
publish = pathlib.Path(a.publish)

def percentile(values, fraction=.95):
    rows = sorted(values)
    return rows[min(len(rows)-1, int((len(rows)-1)*fraction))] if rows else None

records = {}
for name in ('final-tray-1', 'final-tray-5', 'final-tray-20', 'final-view-20', 'outage-20', 'final-soak-8h'):
    source = bench / (name + '.json')
    if not source.exists():
        continue
    raw = json.loads(source.read_text(encoding='utf-8-sig'))
    keep = ('complete', 'requestedSeconds', 'elapsedSeconds', 'activeCoverage', 'warmupSeconds', 'servers', 'intervalSeconds',
            'startupMs', 'cpuSingleCorePercent', 'peakPrivateMiB', 'privateGrowthMiB')
    record = {k: raw.get(k) for k in keep}
    samples = raw.get('samples', [])
    steady = [s for s in samples if s['Seconds'] >= raw.get('warmupSeconds', 60)]
    record['p95CollectionSnapshotMs'] = percentile([s['P95CollectionMs'] for s in steady])
    record['p95SampleAgeSnapshotSeconds'] = percentile([s['P95SampleAgeSeconds'] for s in steady])
    record['p95SchedulingDelaySnapshotMs'] = percentile([s['P95SchedulingDelayMs'] for s in steady if 'P95SchedulingDelayMs' in s])
    record['peakHandles'] = max((s['Handles'] for s in samples), default=0)
    record['lastHandles'] = samples[-1]['Handles'] if samples else 0
    record['lastSshConnections'] = samples[-1].get('SshConnections') if samples else None
    record['lastOnline'] = samples[-1]['Online'] if samples else None
    if name == 'outage-20':
        record['note'] = 'Completed software-rendering baseline before final theme adjustment; not a final-binary soak.'
        record['minimumOnline'] = min((s['Online'] for s in samples), default=0)
    if name == 'final-soak-8h':
        record['note'] = 'Progress snapshot only. A full active eight-hour run and trend review are required.'
    records[name] = record

identities = {name: hashlib.sha256((publish/name).read_bytes()).hexdigest() for name in ('ServerTray.exe', 'ServerTray.dll', 'ServerTray.Core.dll')}
result = dict(schemaVersion=1, version='0.1.0-preview', binaries=identities, reports=records)
target = pathlib.Path(a.output)
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
print('Wrote '+str(target))
