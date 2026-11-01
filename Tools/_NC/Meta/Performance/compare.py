# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""Compare sealed balanced baseline/candidate observations; never sum percentiles or invent CPU."""
import argparse
import hashlib
import json
from pathlib import Path
from reduce import reduce


def compare(baseline, candidate):
    left = reduce(baseline); right = reduce(candidate)
    left_manifest = json.loads((baseline / 'manifest.json').read_bytes())
    right_manifest = json.loads((candidate / 'manifest.json').read_bytes())
    if not left.get('baseline') or right.get('baseline') or left.get('workload') != right.get('workload') or left.get('mode') != right.get('mode'):
        raise ValueError('Select an actual baseline and candidate of the same workload/mode.')
    for key in ('sourceHash', 'serverAssemblyHash'):
        if not left.get(key) or left[key] != right.get(key):
            raise ValueError('Native source semantics/loaded server assembly differ.')
    for key in ('resourceProvenance',):
        if left_manifest[key]['sha256'] != right_manifest[key]['sha256']:
            raise ValueError('Declared immutable source/resource inventories differ.')
    left_identity = left_manifest.get('buildIdentity'); right_identity = right_manifest.get('buildIdentity')
    left_hash = left_identity.get('manifestSha256') if isinstance(left_identity, dict) else None
    right_hash = right_identity.get('manifestSha256') if isinstance(right_identity, dict) else None
    if not isinstance(left_hash, str) or len(left_hash) != 64 or not all(character in '0123456789abcdef' for character in left_hash) or left_hash != right_hash:
        raise ValueError('A balanced acceptance comparison requires the identical verified cohort manifest.')
    for key in ('seed', 'warmup', 'seconds'):
        if left_manifest['requested'][key] != right_manifest['requested'][key]:
            raise ValueError('Requested seed/warmup/capture differ.')
    lt = left.get('fullEngineTickElapsedMs'); rt = right.get('fullEngineTickElapsedMs')
    delta = {key: rt[key] - lt[key] for key in ('p50', 'p95', 'p99', 'max')} if lt and rt else None
    return {'schemaVersion': 1, 'baseline': str(baseline), 'candidate': str(candidate), 'workload': right['workload'],
        'mode': right['mode'], 'baselineFullTickElapsedMs': lt, 'candidateFullTickElapsedMs': rt,
        'differenceOfFullTickDistributionQuantilesMs': delta,
        'candidateObservedMetaScopeUnionMs': right.get('observedScopeUnionPerTickMs'),
        'coverage': 'two-independent-runs;difference-of-quantiles-not-quantile-of-paired-differences;elapsed-not-CPU',
        'additionalTickP99TargetMetForThisPair': delta['p99'] <= 2 if delta else None,
        'finalAcceptance': None, 'reason': 'At least three balanced repetitions plus complete native work/latency/memory/CPU/supplement evidence are required; a single pair is not final acceptance.'}


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('baseline', type=Path); parser.add_argument('candidate', type=Path); parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args(); result = compare(args.baseline.resolve(), args.candidate.resolve())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(result, indent=2).encode('utf-8'); args.output.write_bytes(encoded)
    args.output.with_suffix(args.output.suffix + '.sha256').write_text(hashlib.sha256(encoded).hexdigest() + '\n', encoding='ascii')
    print(json.dumps({'comparison': str(args.output), 'additionalTickP99TargetMetForThisPair': result['additionalTickP99TargetMetForThisPair']}))


if __name__ == '__main__':
    main()
