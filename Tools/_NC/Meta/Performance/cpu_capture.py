# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""Run the already built owned-PID EventPipe collector; no system-wide ETW or installation."""
import argparse
import os
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--collector', type=Path, required=True, help='Already built TraceCollector.dll.')
    parser.add_argument('--processes', type=Path, required=True, help='Sealed fleet processes.json with exact owned server lifetime.')
    parser.add_argument('--seconds', type=int, default=60)
    parser.add_argument('--max-trace-mib', type=int, default=512)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if not args.collector.is_file() or not 1 <= args.seconds <= 120 or not 64 <= args.max_trace_mib <= 2048:
        parser.error('Use an already built collector, duration1..120s and trace64..2048MiB.')
    arguments = ['dotnet', str(args.collector.resolve()), '--processes', str(args.processes.resolve()), '--seconds', str(args.seconds),
        '--max-trace-mib', str(args.max_trace_mib), '--output', str(args.output.resolve())]
    result = subprocess.run(arguments, timeout=args.seconds + 120, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    raise SystemExit(result.returncode)


if __name__ == '__main__':
    main()
