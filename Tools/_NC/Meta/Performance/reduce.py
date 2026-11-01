# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""Offline reduction of sealed native samples. Never turns elapsed scopes into CPU or full ticks."""
import argparse
from collections import defaultdict
import hashlib
import json
import math
from pathlib import Path


def quantiles(values):
    if not values:
        return None
    values = sorted(values)
    def at(p):
        return values[max(0, math.ceil(len(values) * p) - 1)]
    return {'count': len(values), 'p50': at(.5), 'p95': at(.95), 'p99': at(.99), 'max': values[-1]}


def union_duration(intervals):
    end = None
    total = 0
    for first, last in sorted(intervals):
        if end is None or first > end:
            total += last - first
        elif last > end:
            total += last - end
        end = last if end is None else max(end, last)
    return total


def reduce(folder):
    manifest_bytes = (folder / 'manifest.json').read_bytes()
    if hashlib.sha256(manifest_bytes).hexdigest() != (folder / 'manifest.sha256').read_text().strip():
        raise ValueError('Run manifest seal does not match.')
    manifest = json.loads(manifest_bytes)
    if manifest.get('failure') or not manifest.get('measurementResults'):
        return {'status': 'not-measured', 'failure': manifest.get('failure'), 'acceptance': None}
    raw_bytes = (folder / 'samples.jsonl').read_bytes()
    if hashlib.sha256(raw_bytes).hexdigest() != manifest['measurementResults']['rawSha256']:
        raise ValueError('Raw sample seal does not match.')
    status = manifest['finalStatus']; run = status['run']; info = run['capture']; frequency = info['frequency']
    labels = status['metricLabels']; by_metric = defaultdict(list); tick_intervals = defaultdict(list)
    # These roots are inclusive. Interval union prevents double counting the instrumentation
    # scope and native network ingress if future hooks nest them. This remains observed scope
    # coverage, not the engine's complete tick or every META subsystem.
    root_scopes = {'RuntimeElapsed', 'NativeNetworkElapsed', 'InstrumentationElapsed', 'NativeSensorElapsed', 'NativeActuatorElapsed', 'ApplicationElapsed'}
    for line in raw_bytes.splitlines():
        item = json.loads(line); label = labels[item['metric']]
        by_metric[label].append(item['value'])
        if label in root_scopes and item['context'] > 0:
            tick_intervals[item['context']].append((item['timestamp'] - item['value'], item['timestamp']))
    complete = info['droppedSamples'] == 0 and sum(len(v) for v in by_metric.values()) == info['reservedSamples']
    metrics = {}
    for summary in info['summaries']:
        label = labels[summary['metric']]; is_time = summary['unit'] == 0
        scale = 1000 / frequency if is_time else 1
        samples = by_metric.get(label, [])
        metrics[label] = {'unit': 'ms elapsed' if is_time else 'bytes' if summary['unit'] == 2 else 'count',
            'observations': summary['count'], 'sum': summary['sum'] * scale, 'min': summary['minimum'] * scale,
            'max': summary['maximum'] * scale, 'raw': quantiles([v * scale for v in samples]) if complete else None,
            'rawCoverageComplete': complete}
    observed_ticks = [union_duration(v) * 1000 / frequency for v in tick_intervals.values()]
    event_count = metrics.get('EventsStarted', {}).get('sum', 0)
    gas = metrics.get('GasUsed', {}).get('sum', 0)
    elapsed_seconds = manifest['requested']['seconds']
    engine = run.get('engineTicks') or {}
    engine_complete = complete and engine.get('providerObserved') and engine.get('missingPairs') == 0 and engine.get('completed', 0) > 0 and len(by_metric.get('EngineTickElapsed', [])) == engine['completed']
    return {'schemaVersion': 1, 'status': 'recorded' if complete else 'recorded-incomplete-raw', 'workload': run['workload'],
        'mode': run['mode'], 'baseline': run['baseline'], 'sourceHash': run['sourceHash'], 'serverAssemblyHash': run['serverAssemblyHash'],
        'artifactSetHash': run['artifactSetHash'], 'captureSecondsRequested': elapsed_seconds, 'nativeStartTick': run['startTick'], 'nativeEndTick': run['endTick'],
        'actualInstalled': run['actualInstalled'], 'loadedAtPreparation': run['actualLoaded'], 'physicalActors': run['physicalActors'],
        'physicalBotStepsDuringCapture': run['nativeBotSteps'], 'nativeStimuliAccepted': run['stimuliAccepted'], 'nativeStimuliRejected': run['stimuliRejected'],
        'rawCoverageComplete': complete, 'droppedSamples': info['droppedSamples'], 'metrics': metrics,
        'observedScopeUnionPerTickMs': quantiles(observed_ticks) if complete else None,
        'observedScopeCoverage': sorted(root_scopes & by_metric.keys()),
        'engineTickCoverage': engine,
        'fullEngineTickElapsedMs': metrics.get('EngineTickElapsed', {}).get('raw') if engine_complete else None,
        'actualGasPerStartedHandler': gas / event_count if event_count else None,
        'acceptance': {'totalMetaMainThreadP95P99': None, 'incrementalFullTickP99': None, 'perSystemCPU': None,
            'fiftyFrontendSessions': None, 'reason': 'Complete META-originated native work, balanced baseline tick comparison, external CPU and the separate actual frontend fleet remain required.'},
        'claimsNotMade': ['Elapsed spans are not CPU.', 'No full tick latency inferred from a META span.', 'No64KiB memory assumption used.',
            'No500-service claim from C.', 'No p99 computed from truncated raw samples.', 'Physical completion duration is not admission latency.']}


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('folder', type=Path); args = parser.parse_args()
    folder = args.folder.resolve(); result = reduce(folder)
    encoded = json.dumps(result, indent=2).encode('utf-8')
    (folder / 'summary.json').write_bytes(encoded)
    (folder / 'summary.sha256').write_text(hashlib.sha256(encoded).hexdigest() + '\n', encoding='ascii')
    print(json.dumps({'status': result['status'], 'summary': str(folder / 'summary.json')}))


if __name__ == '__main__':
    main()
