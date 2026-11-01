# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""Back up an owned artifact DB and compare its original durable data after migrations.
Never opens a source DB for writing or overwrites an existing backup.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3
import struct
import uuid

ROOT = Path(__file__).resolve().parents[3]
ARTIFACTS = (ROOT / 'artifacts').resolve()
REQUIRED = ('nc_aggregate_snapshot', 'nc_event_journal', 'nc_command_receipt', 'nc_outbox_message', 'nc_meta_blob')


def owned(path):
    result = Path(path).resolve()
    if not result.is_relative_to(ARTIFACTS):
        raise ValueError('Only an explicitly selected owned artifact database/report is supported.')
    return result


def identifier(value):
    return '"' + value.replace('"', '""') + '"'


def projection(connection, table, columns=None):
    info = connection.execute('PRAGMA table_info(' + identifier(table) + ')').fetchall()
    if not info:
        return None
    columns = columns or [row[1] for row in info]
    keys = [row[1] for row in sorted(info, key=lambda row: row[5]) if row[5]]
    query = 'SELECT ' + ','.join(map(identifier, columns)) + ' FROM ' + identifier(table)
    query += ' ORDER BY ' + ','.join(map(identifier, keys or columns))
    digest = hashlib.sha256()
    count = 0
    for row in connection.execute(query):
        count += 1
        for value in row:
            if value is None:
                data, kind = b'', b'N'
            elif isinstance(value, bytes):
                data, kind = value, b'B'
            elif isinstance(value, str):
                data, kind = value.encode('utf-8'), b'T'
            elif isinstance(value, int):
                data, kind = str(value).encode('ascii'), b'I'
            else:
                data, kind = repr(value).encode('ascii'), b'F'
            digest.update(kind + struct.pack('<Q', len(data)) + data)
    return {'columns': columns, 'rows': count, 'sha256': digest.hexdigest()}


def read_only(path):
    return sqlite3.connect(path.as_uri() + '?mode=ro', uri=True)


def check_integrity(connection):
    if connection.execute('PRAGMA integrity_check').fetchall() != [('ok',)]:
        raise RuntimeError('SQLite integrity check failed.')
    if connection.execute('PRAGMA foreign_key_check').fetchall():
        raise RuntimeError('SQLite foreign-key check failed.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--source', type=Path)
    group.add_argument('--compare', type=Path, help='Report from an earlier owned backup, after migrating its restored.db.')
    args = parser.parse_args()
    if args.source:
        source = owned(args.source)
        if source.stat().st_size > 512 * 1024 * 1024:
            raise ValueError('This bounded acceptance probe supports at most a 512MiB fixture DB.')
        target = ARTIFACTS / 'meta-backup-restore' / uuid.uuid4().hex
        target.mkdir(parents=True, exist_ok=False)
        restored = target / 'restored.db'
        with read_only(source) as original:
            check_integrity(original)
            tables = [row[0] for row in original.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY name")]
            if not set(REQUIRED).issubset(tables):
                raise RuntimeError('The selected fixture does not contain the complete META persistence foundation.')
            before = {table: projection(original, table) for table in tables}
            migrations_before = [row[0] for row in original.execute('SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId')]
            with sqlite3.connect(restored) as destination:
                original.backup(destination)
                check_integrity(destination)
                for table, expected in before.items():
                    if projection(destination, table) != expected:
                        raise RuntimeError('Backup changed durable data: ' + table)
        report = {'version': 1, 'source': str(source), 'restored': str(restored),
                  'before': before, 'migrationsBefore': migrations_before, 'backupVerified': True, 'migrationVerified': False}
        path = target / 'report.json'
    else:
        path = owned(args.compare)
        report = json.loads(path.read_text(encoding='utf-8'))
        restored = owned(report['restored'])
        with read_only(restored) as database:
            check_integrity(database)
            for table, expected in report['before'].items():
                actual = projection(database, table, expected['columns']) if expected else projection(database, table)
                if expected != actual:
                    raise RuntimeError('Migration changed original durable data: ' + table)
            report['migrations'] = [row[0] for row in database.execute('SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId')]
        report['migrationVerified'] = True
    path.write_text(json.dumps(report, ensure_ascii=True, indent=2), encoding='utf-8')
    print(json.dumps({'report': str(path), 'restored': report['restored'], 'backupVerified': report['backupVerified'],
                      'migrationVerified': report['migrationVerified'], 'verifiedTables': len(report['before']),
                      'rows': {key: report['before'][key]['rows'] for key in REQUIRED}}))


if __name__ == '__main__':
    main()
