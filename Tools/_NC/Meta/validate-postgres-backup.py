# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
"""Restore the recorded, isolated local acceptance cluster into a fresh owned DB.
Run with WSL Python beside PostgreSQL 18. Source rows are read only; no existing
database is overwritten. Reports contain counts and hashes, never row contents.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[3]
ARTIFACTS = (ROOT / 'artifacts').resolve()
BIN = Path('/usr/lib/postgresql/18/bin')
REQUIRED = ('nc_aggregate_snapshot', 'nc_event_journal', 'nc_command_receipt', 'nc_outbox_message', 'nc_meta_blob')


def owned(path):
    result = Path(path).resolve()
    if not result.is_relative_to(ARTIFACTS):
        raise ValueError('Only an explicitly owned artifact path is supported.')
    return result


def identifier(name):
    if not re.fullmatch(r'[A-Za-z_][A-Za-z_0-9]{0,127}', name):
        raise ValueError('Unexpected acceptance schema identifier.')
    return '"' + name + '"'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--state', type=Path, default=ARTIFACTS / 'meta-postgres-owned.json')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    state = json.loads(owned(args.state).read_text(encoding='utf-8-sig'))
    cluster_id = str(uuid.UUID(hex=state['Id'])).replace('-', '')
    if state['Id'] != cluster_id or state['DataDirectory'] != '/tmp/nc-meta-pg-' + cluster_id or state['Port'] != 55432 or state['User'] != 'astro':
        raise ValueError('The recorded isolated cluster identity does not match.')
    source = state['Database']
    if not re.fullmatch(r'nc_meta_[a-z0-9_]{1,48}', source):
        raise ValueError('An isolated META database is required.')
    run_id = uuid.uuid4().hex
    output = owned(args.output or ARTIFACTS / 'meta-postgres-backup' / run_id)
    output.mkdir(parents=True, exist_ok=False)
    restored = 'nc_meta_restore_' + run_id
    env = os.environ.copy()
    env.update(PGHOST='127.0.0.1', PGPORT='55432', PGUSER='astro', PGTZ='UTC', PGCONNECT_TIMEOUT='5',
               PGOPTIONS='-c statement_timeout=15000 -c lock_timeout=5000 -c timezone=UTC')
    # Do not inherit a connection override or password for any unrelated service.
    for name in ('PGDATABASE', 'PGSERVICE', 'PGSERVICEFILE', 'PGPASSWORD', 'PGPASSFILE'):
        env.pop(name, None)
    report = {'version': 1, 'source': source, 'restored': restored, 'clusterId': cluster_id,
              'sourceReadOnly': True, 'backupVerified': False, 'restoreVerified': False}

    def command(program, arguments, timeout=120):
        result = subprocess.run([str(BIN / program), *arguments], env=env, capture_output=True, timeout=timeout)
        if result.returncode:
            (output / (program + '.stderr.txt')).write_bytes(result.stderr[-16384:])
            raise RuntimeError('An isolated PostgreSQL step failed: ' + program)
        return result.stdout.decode('utf-8').strip()

    def sql(database, query):
        return command('psql', ['-X', '-q', '-A', '-t', '-v', 'ON_ERROR_STOP=1', '-d', database, '-c', query])

    def inventory(database):
        if int(sql(database, 'SELECT pg_database_size(current_database())')) > 512 * 1024 * 1024:
            raise ValueError('The acceptance database exceeds the backup bound.')
        names = json.loads(sql(database, "SELECT coalesce(json_agg(tablename ORDER BY tablename),'[]') FROM pg_tables WHERE schemaname='public'"))
        if not 1 <= len(names) <= 512 or any(name not in names for name in REQUIRED):
            raise ValueError('The acceptance schema inventory is missing or oversized.')
        # Logical pg_dump removes physical holes left by dropped columns. Preserve
        # the complete visible column order, rather than comparing those attnums.
        schema = sql(database, """SELECT coalesce(jsonb_agg(to_jsonb(v) ORDER BY v.table_name,v.ordinal_position),'[]')
            FROM (SELECT table_name,column_name,row_number() OVER (PARTITION BY table_name ORDER BY ordinal_position) AS ordinal_position,
                         data_type,udt_name,is_nullable,column_default,
                         character_maximum_length,numeric_precision,numeric_scale,is_identity,is_generated,generation_expression
                  FROM information_schema.columns WHERE table_schema='public') v""")
        constraints = sql(database, """SELECT coalesce(jsonb_agg(to_jsonb(v) ORDER BY v.table_name,v.name),'[]') FROM
            (SELECT c.relname AS table_name,k.conname AS name,k.convalidated AS validated,
                    pg_get_constraintdef(k.oid,true) AS definition FROM pg_constraint k
             JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace
             WHERE n.nspname='public') v""")
        if int(sql(database, "SELECT count(*) FROM pg_constraint k JOIN pg_namespace n ON n.oid=k.connamespace WHERE n.nspname='public' AND NOT k.convalidated")):
            raise ValueError('Unvalidated acceptance database constraints.')
        sequences = sql(database, """SELECT coalesce(jsonb_agg(to_jsonb(v) ORDER BY v.sequencename),'[]') FROM
            (SELECT sequencename,start_value,min_value,max_value,increment_by,cycle,cache_size,last_value
             FROM pg_sequences WHERE schemaname='public') v""")
        sequence_values = {}
        for sequence in json.loads(sequences):
            name = sequence['sequencename']
            sequence_values[name] = json.loads(sql(database,
                "SELECT json_build_object('lastValue',last_value,'isCalled',is_called) FROM public." + identifier(name)))
        indexes = sql(database, """SELECT coalesce(jsonb_agg(to_jsonb(v) ORDER BY v.table_name,v.name),'[]') FROM
            (SELECT t.relname AS table_name,c.relname AS name,i.indisvalid AS valid,i.indisready AS ready,
                    pg_get_indexdef(i.indexrelid) AS definition FROM pg_index i
             JOIN pg_class t ON t.oid=i.indrelid JOIN pg_class c ON c.oid=i.indexrelid
             JOIN pg_namespace n ON n.oid=t.relnamespace WHERE n.nspname='public') v""")
        tables = {}
        for name in names:
            table = 'public.' + identifier(name)
            if int(sql(database, 'SELECT pg_total_relation_size(' + "'" + table + "'" + ')')) > 64 * 1024 * 1024:
                raise ValueError('An acceptance table exceeds the projection bound.')
            projection = sql(database, "SELECT json_build_object('rows',count(*),'sha256',encode(sha256(convert_to("
                "coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text)::text,'[]'),'UTF8')),'hex')) FROM " + table + ' t')
            tables[name] = json.loads(projection)
        return {'schemaSha256': hashlib.sha256(schema.encode()).hexdigest(),
                'constraintsSha256': hashlib.sha256(constraints.encode()).hexdigest(),
                'sequencesSha256': hashlib.sha256(sequences.encode()).hexdigest(), 'tables': tables,
                'indexesSha256': hashlib.sha256(indexes.encode()).hexdigest(),
                'schema': json.loads(schema), 'constraints': json.loads(constraints), 'sequences': json.loads(sequences),
                'sequenceValues': sequence_values, 'indexes': json.loads(indexes)}

    try:
        if sql(source, 'SHOW data_directory') != state['DataDirectory']:
            raise ValueError('The running PostgreSQL server is not the owned cluster.')
        before = inventory(source)
        backup = output / 'acceptance.dump'
        command('pg_dump', ['--format=custom', '--no-owner', '--no-acl', '--file', str(backup), '--dbname', source])
        if not 0 < backup.stat().st_size <= 512 * 1024 * 1024:
            raise ValueError('The owned backup size is invalid.')
        if inventory(source) != before:
            raise RuntimeError('Source data changed while backing up the acceptance database.')
        report['backupVerified'] = True
        command('createdb', ['--maintenance-db', source, restored])
        command('pg_restore', ['--no-owner', '--no-acl', '--exit-on-error', '--dbname', restored, str(backup)])
        after = inventory(restored)
        if after != before:
            report['differences'] = [key for key in before if before[key] != after[key]]
            (output / 'source-inventory.json').write_text(json.dumps(before, indent=2), encoding='utf-8')
            (output / 'restored-inventory.json').write_text(json.dumps(after, indent=2), encoding='utf-8')
            raise RuntimeError('Restored schema, constraints, sequences or durable rows differ.')
        report.update(restoreVerified=True, tables=len(before['tables']), inventory=before,
                      backupSha256=hashlib.sha256(backup.read_bytes()).hexdigest(), backupBytes=backup.stat().st_size,
                      requiredCounts={name: before['tables'][name]['rows'] for name in REQUIRED})
    except Exception as error:
        report['error'] = type(error).__name__ + ': ' + str(error)[:512]
        raise
    finally:
        (output / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(json.dumps({key: value for key, value in report.items() if key != 'inventory'}))


if __name__ == '__main__':
    main()
