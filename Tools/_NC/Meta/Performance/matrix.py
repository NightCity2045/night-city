# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""Serial balanced baseline/candidate matrix over real native A/B/C/C-resident runs.
Never builds, shares live DBs between runs or reduces a rejected scale to fit.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys


def seal(path, value):
    encoded = json.dumps(value, indent=2, default=str).encode('utf-8'); path.write_bytes(encoded)
    path.with_suffix(path.suffix + '.sha256').write_text(hashlib.sha256(encoded).hexdigest() + '\n', encoding='ascii')


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--workspace', type=Path, default=Path(__file__).resolve().parents[4])
    parser.add_argument('--cli', type=Path, required=True); parser.add_argument('--build-id', required=True)
    parser.add_argument('--output', type=Path, required=True); parser.add_argument('--repeats', type=int, default=3)
    parser.add_argument('--workloads', nargs='+', choices=['A', 'B', 'C', 'C-resident'], default=['A', 'B', 'C', 'C-resident'])
    parser.add_argument('--modes', nargs='+', choices=['Normal', 'Burst', 'Heavy', 'Hostile'], default=['Normal'])
    parser.add_argument('--seconds', type=int, default=60); parser.add_argument('--warmup', type=int, default=30)
    parser.add_argument('--prepare-timeout', type=int, default=3600); parser.add_argument('--seed', type=int, default=90210)
    args = parser.parse_args()
    if not 1 <= args.repeats <= 3 or len(set(args.workloads)) != len(args.workloads) or len(set(args.modes)) != len(args.modes) or not 1 <= args.seconds <= 60 or not 0 <= args.warmup <= 120 or not 60 <= args.prepare_timeout <= 7200:
        parser.error('Use1..3 repeats, unique workloads/modes, capture1..60s,warmup0..120s, preparation60..7200s.')
    root = args.workspace.resolve(); output = args.output.resolve()
    if not output.is_relative_to(root / 'artifacts'):
        parser.error('Matrix output must remain under this workspace/artifacts.')
    output.mkdir(parents=True, exist_ok=False); tools = Path(__file__).resolve().parent
    manifest = {'schemaVersion': 1, 'startedAt': datetime.now(timezone.utc).isoformat(), 'requested': vars(args), 'runs': [], 'result': 'in-progress'}
    try:
        for workload in args.workloads:
            for mode in args.modes:
                for repeat in range(args.repeats):
                    pair = {}
                    for baseline in ([True, False] if repeat % 2 == 0 else [False, True]):
                        label = '%s-%s-%02d-%s' % (workload, mode, repeat + 1, 'baseline' if baseline else 'candidate')
                        folder = output / label
                        command = [sys.executable, str(tools / 'run.py'), '--workspace', str(root), '--cli', str(args.cli.resolve()),
                            '--build-id', args.build_id, '--workload', workload, '--mode', mode, '--seed', str(args.seed + repeat),
                            '--warmup', str(args.warmup), '--seconds', str(args.seconds), '--prepare-timeout', str(args.prepare_timeout), '--output', str(folder)]
                        if baseline:
                            command.append('--baseline')
                        with (output / (label + '.log')).open('wb') as log:
                            result = subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT,
                                timeout=args.prepare_timeout + args.warmup + args.seconds + 600)
                        manifest['runs'].append({'label': label, 'command': command, 'exitCode': result.returncode})
                        seal(output / 'matrix.json', manifest)
                        if result.returncode:
                            raise RuntimeError('Actual requested native run failed: ' + label)
                        subprocess.run([sys.executable, str(tools / 'reduce.py'), str(folder)], cwd=root, check=True, timeout=60)
                        pair[baseline] = folder
                    subprocess.run([sys.executable, str(tools / 'compare.py'), str(pair[True]), str(pair[False]), '--output', str(output / ('%s-%s-%02d-comparison.json' % (workload, mode, repeat + 1)))],
                        cwd=root, check=True, timeout=60)
        manifest['result'] = 'recorded-no-final-acceptance'
        manifest['coverage'] = 'serial-real-native-runs;balanced-order;separate-fresh-isolated-DB-per-run;no-supplement-substitution'
    except BaseException as error:
        manifest['result'] = 'failed-or-incomplete'; manifest['failure'] = type(error).__name__ + ':' + str(error)
        raise
    finally:
        manifest['finishedAt'] = datetime.now(timezone.utc).isoformat(); seal(output / 'matrix.json', manifest)


if __name__ == '__main__':
    main()
