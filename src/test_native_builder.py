"""Integration verification on an owned, isolated PostgreSQL 8.3 cluster.

Never connects to or changes the installed server on port 5432.
Uses original backups as read-only sources and publishes counts/status only.
"""
import datetime
import hashlib
import json
import os
import re
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import sys
import tarfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'test-support'))
from stock_runtime import validate_stock, STOCK
from inspect_dump import parse_toc
from verify_backup_feature import verify_backups


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def original_file(definition, database):
    """Read a fixture from either app storage or the user's release-pack samples."""
    game = next(item for item in definition['games'] if item['database'] == database)
    filename = Path(game['backup']).name
    candidates = [path for folder in ('PG_backup', 'games')
                  for path in (ROOT / 'app' / folder).rglob('*')
                  if path.is_file() and path.name == filename]
    if game.get('sha256'):
        candidates = [path for path in candidates if sha(path) == game['sha256']]
    assert candidates and len({sha(path) for path in candidates}) == 1, (database, candidates)
    return candidates[0]


def main():
    validate_stock()
    port = 15583
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', port))
    test_root = ROOT / 'tmp' / ('it-builder-test-' + secrets.token_hex(6) + ' [literal]')
    test_root.mkdir()
    identity = subprocess.run(['whoami'], capture_output=True, check=True, text=True).stdout.strip()
    subprocess.run(['icacls', str(test_root), '/inheritance:r', '/grant:r', f'{identity}:(OI)(CI)F'], capture_output=True, check=True)
    tool = test_root / 'IT_DatabaseTool'
    tool.mkdir()
    shutil.copyfile(ROOT / 'app/IT Database Builder.exe', tool / 'IT Database Builder.exe')
    # Generate a legacy fixture from the authoritative JSON; users may remove TXT.
    source_definition=json.loads((ROOT/'app/DatabaseInfo.json').read_text(encoding='utf-8-sig'))
    # Synthetic legacy settings only; never copy the user's installed-server
    # credentials into a test fixture. CLI/environment select the private cluster.
    connection=dict(server='127.0.0.1',port=15584,username='postgres',
                    password='generic-legacy-fixture-password',pgBin=None)
    legacy_lines=['Address: '+connection['server'],'Port: '+str(connection['port']),
                  'Username: '+connection['username'],'Password: '+connection['password']]
    if connection.get('pgBin'):
        legacy_lines.append('Postgres Path: '+connection['pgBin'])
    for game in source_definition['games']:
        backup=Path(game['backup'])
        legacy_lines+=['',game['game'],'DB name: '+game['database'],'Encoding: '+game['encoding'],
                       'db backup location: pg_backup/'+(str(backup.parent).replace('\\','/') if backup.parent!=Path('.') else ''),
                       'db filename: '+backup.name,'Backup folder: '+game.get('backupFolder',game['game'])]
        if game.get('sha256') and game['database']!='GameDB07':
            legacy_lines.append('Backup SHA256: '+game['sha256'])
        if game.get('restoreProfile'):
            legacy_lines.append('Restore profile: '+game['restoreProfile'])
    definition='\n'.join(legacy_lines)+'\n'
    migration_password = 'generic "test" \\ password [literal] Ω'
    definition = re.sub(r'(?mi)^Password:[^\r\n]*', lambda _: 'Password: '+migration_password, definition)
    # One game tests filename discovery without a registered fingerprint.
    definition = re.sub(r'(?m)(DB name: GameDB07[\s\S]*?db filename:[^\r\n]+\r?\n)Backup SHA256:[^\r\n]*\r?\n', r'\1', definition)
    (tool / 'DatabaseInfo.txt').write_text(definition, encoding='utf-8')
    entry_count = len(re.findall(r'(?mi)^DB name:', definition))
    hashes = {}
    fixtures = {database: original_file(source_definition, database)
                for database in ('GameDB06', 'GameDB07', 'GameDB20', 'GameDB13')}
    destinations = {
        'GameDB06': 'PG_backup/mixed folders/nested/renamed-anything.bin',
        'GameDB07': 'PG_backup/2043-postgresql_database-GameDB-backup.dump',
        'GameDB20': 'PG_backup/a/b/c/2228-postgresql_database-GameDB-backup',
    }
    for name in destinations:
        source = fixtures[name]
        target = tool / destinations[name]
        target.parent.mkdir(parents=True, exist_ok=True)
        hashes[str(source)] = sha(source)
        shutil.copyfile(source, target)
    duplicate = tool / 'pg_backups/any duplicate/2043-postgresql_database-GameDB-backup'
    duplicate.parent.mkdir(parents=True)
    shutil.copyfile(tool / destinations['GameDB07'], duplicate)
    password = secrets.token_urlsafe(32)
    password_file = test_root / 'init-password'
    password_file.write_text(password + '\n', encoding='ascii')
    env = os.environ.copy()
    for key in ('PGSERVICE', 'PGOPTIONS', 'PGHOST', 'PGPORT', 'PGDATABASE', 'PGUSER', 'PGPASSFILE', 'PSQLRC', 'PAGER', 'PSQL_PAGER'):
        env.pop(key, None)
    env.update(PGPASSWORD=password, PGCONNECT_TIMEOUT='10', PGUSER='postgres', PGHOST='127.0.0.1', PGPORT=str(port))
    data = test_root / 'data'
    started = False
    results = {'port': port, 'root': str(test_root), 'installed_server_untouched': True, 'checks': []}

    def run(name, exe, args, timeout=120, use_env=env):
        out = test_root / (name + '.stdout')
        err = test_root / (name + '.stderr')
        with out.open('wb') as stdout, err.open('wb') as stderr:
            process = subprocess.run([str(exe), *map(str, args)], env=use_env, stdout=stdout, stderr=stderr,
                                     cwd=test_root, timeout=timeout, creationflags=subprocess.CREATE_NO_WINDOW)
        if process.returncode != 0:
            raise RuntimeError(f'{name} failed with exit {process.returncode}; see restricted test logs')
        return out.read_text(encoding='utf-8-sig').strip()

    def pg(name, command, args):
        return run(name, STOCK / 'bin' / (command + '.exe'), args)

    def sql(name, database, query):
        return pg(name, 'psql', ['-X', '-q', '-A', '-t', '-P', 'pager=off', '-h', '127.0.0.1', '-p', port,
                                '-U', 'postgres', '-d', database, '-v', 'ON_ERROR_STOP=1', '-c', query])

    try:
        pg('initdb', 'initdb', ['-D', data, '-E', 'UTF8', '--locale=C', '-U', 'postgres', '-A', 'md5', '--pwfile=' + str(password_file)])
        password_file.unlink()
        started = True
        pg('start', 'pg_ctl', ['-D', data, '-l', test_root / 'server.log', '-o', f'-p {port} -h 127.0.0.1', '-w', 'start'])
        sql('create-existing', 'postgres', 'CREATE DATABASE "GameDBPP13" WITH TEMPLATE=template0;')
        sql('seed-existing', 'GameDBPP13', 'CREATE TABLE preserve_me (value integer); INSERT INTO preserve_me VALUES (42);')
        installed_args=['--check-installed','--root',tool,'--port',port,'--pg-bin',STOCK/'bin']
        names_before=sql('before-installed-check','postgres','SELECT datname FROM pg_database ORDER BY datname;')
        definition_before=(tool/'DatabaseInfo.txt').read_bytes()
        installed=json.loads(run('check-installed',tool/'IT Database Builder.exe',installed_args))
        assert 'GameDBPP13' in installed and 'GameDB06' not in installed
        assert names_before==sql('after-installed-check','postgres','SELECT datname FROM pg_database ORDER BY datname;')
        assert sql('installed-check-preserves-data','GameDBPP13','SELECT value FROM preserve_me;')=='42'
        assert not (tool/'reports').exists() and (tool/'DatabaseInfo.txt').read_bytes()==definition_before
        wrong_env=env.copy();wrong_env['PGPASSWORD']='incorrect-test-password'
        failed=subprocess.run([str(tool/'IT Database Builder.exe'),*map(str,installed_args)],env=wrong_env,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
        assert failed.returncode!=0 and b'rejected the password' in failed.stderr
        assert wrong_env['PGPASSWORD'].encode() not in failed.stderr
        closed_args=[str(tool/'IT Database Builder.exe'),'--check-installed','--root',str(tool),'--port','15584','--pg-bin',str(STOCK/'bin')]
        failed=subprocess.run(closed_args,env=env,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
        assert failed.returncode!=0 and b'Cannot reach PostgreSQL' in failed.stderr
        results['checks'].append('installed database check lists actual names without creating databases, changing data, or writing restore reports; rejected password and unavailable server report errors')
        verify_args=['--verify-setup','--root',tool,'--port',port,'--pg-bin',STOCK/'bin']
        setup=json.loads(run('verify-first-time-setup',tool/'IT Database Builder.exe',verify_args))
        assert setup['serverVersion'].startswith('8.3.') and setup['encoding']=='UTF8'
        assert setup['collation']=='C' and setup['characterType']=='C' and setup['guideDefaultsMatch']
        assert names_before==sql('after-setup-verification','postgres','SELECT datname FROM pg_database ORDER BY datname;')
        assert sql('setup-verification-preserves-data','GameDBPP13','SELECT value FROM preserve_me;')=='42'
        assert not (tool/'reports').exists()
        for bad_args,bad_env,expected_error in (
            (verify_args,wrong_env,b'rejected the password'),
            (['--verify-setup','--root',tool,'--port','15584','--pg-bin',STOCK/'bin'],env,b'Cannot reach PostgreSQL'),
        ):
            rejected=subprocess.run([str(tool/'IT Database Builder.exe'),*map(str,bad_args)],env=bad_env,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
            assert rejected.returncode!=0 and expected_error in rejected.stderr
            assert wrong_env['PGPASSWORD'].encode() not in rejected.stderr
        results['checks'].append('first-time setup verification checks the real PostgreSQL 8.3 server, UTF8 encoding and C locales without changing databases; wrong database password and unavailable service produce actionable errors')
        args = ['--build', '--root', tool, '--port', port, '--pg-bin', STOCK / 'bin']
        single = json.loads(run('build-specific', tool / 'IT Database Builder.exe', args + ['--only', 'GameDB06']))
        manifest_path = tool / 'DatabaseInfo.json'
        migrated = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
        expected = json.loads((ROOT/'app/DatabaseInfo.json').read_text(encoding='utf-8-sig'))
        expected.pop('connection', None)
        next(g for g in expected['games'] if g['database']=='GameDB07')['sha256'] = None
        migrated_settings = json.loads((tool/'UserSettings.json').read_text(encoding='utf-8-sig'))
        first_initial = next(record for record in migrated_settings['backups'] if record['requestedDatabase']=='GameDB06' and record['kind']=='initial')
        initial_file = tool / first_initial['file']
        expected06 = next(g for g in expected['games'] if g['database']=='GameDB06')
        expected06['databaseFile'] = str(initial_file)
        expected06['sha256'] = first_initial['sha256']
        assert initial_file.read_bytes() == (tool/destinations['GameDB06']).read_bytes()
        assert migrated == expected
        assert migrated_settings['connection'] == dict(connection, password=migration_password)
        assert (tool/'DatabaseInfo.txt').read_text(encoding='utf-8-sig') == definition
        (tool/'DatabaseInfo.txt').write_text('Edited old TXT must not override JSON.\n', encoding='utf-8')
        results['checks'].append('one-time TXT migration splits the catalogue from user settings, preserving entries, hashes, connection fields and escaped password; JSON remains authoritative')
        assert len(single['entries']) == 1 and single['entries'][0]['database'] == 'GameDB06'
        fixed06=single['entries'][0]
        assert fixed06['status']=='Imported' and fixed06['restoreExitCode']==0 and fixed06['encoding']=='SQL_ASCII', fixed06
        source06=fixtures['GameDB06']
        with tarfile.open(source06,'r:') as archive:
            _,toc06=parse_toc(archive.extractfile('toc.dat').read())
            attr06=next(item for item in toc06 if item['kind']=='TABLE DATA' and item['tag']=='category2_attr')
            rows06=sum(line not in (b'',b'\\.') for line in archive.extractfile(attr06['member']).read().splitlines())
        assert int(sql('complete-category2-data','GameDB06','SELECT count(*) FROM category2_attr;'))==rows06
        assert sql('verified-06-foreign-keys','GameDB06',"SELECT count(*) FROM pg_constraint WHERE contype='f' AND conname IN ('category1_attr_fk','category2_attr_fk','category3_attr_fk','category4_fk','category5_fk');")=='5'
        health_args=['--check-games','--root',tool,'--port',port,'--pg-bin',STOCK/'bin']
        health=json.loads(run('health-complete-06',tool/'IT Database Builder.exe',health_args))
        assert not next(entry for entry in health if entry['requestedDatabase']=='GameDB06')['databaseNeedsRepair']
        results['checks'].append({'corrected_2006_restore':'zero errors; original SQL_ASCII data and all five foreign keys restored','category2_rows':rows06})
        assert sql('unselected-not-created', 'postgres', "SELECT count(*) FROM pg_database WHERE datname IN ('GameDB07','GameDB20');") == '0'
        results['checks'].append('specific-game build leaves unchecked games absent')
        first = json.loads(run('build-first', tool / 'IT Database Builder.exe', args, timeout=300))
        assert first['completed'] and first['port'] == port
        selected = {entry['requestedDatabase']: entry for entry in first['entries']}
        for database in ('GameDB06', 'GameDB07', 'GameDB20'):
            entry = single['entries'][0] if database == 'GameDB06' else selected[database]
            assert entry['status'] == 'Imported' and entry['restoreExitCode'] == 0, entry
            assert entry['settingsVerified'] and entry['publicTables'] == 24
            assert sql('verified-foreign-keys-'+database,database,"SELECT count(*) FROM pg_constraint WHERE contype='f' AND conname IN ('category1_attr_fk','category2_attr_fk','category3_attr_fk','category4_fk','category5_fk');") == '5'
            results['checks'].append({'database': database, 'status': entry['status'], 'tables': entry['publicTables'], 'settings_verified': True})
        assert selected['GAMEDBPP13']['status'] == 'Already present'
        assert selected['GameDB06']['status'] == 'Already present'
        assert selected['GAMEDBPP13']['database'] == 'GameDBPP13'
        assert sum(e['status'] == 'Waiting for backup' for e in first['entries']) == entry_count-4
        assert sql('preserved-existing', 'GameDBPP13', 'SELECT value FROM preserve_me;') == '42'
        before = sql('databases-before-rerun', 'postgres', 'SELECT datname FROM pg_database ORDER BY datname;')
        second = json.loads(run('build-rerun', tool / 'IT Database Builder.exe', args, timeout=120))
        assert second['completed'] and sum(e['status'] == 'Already present' for e in second['entries']) == 4
        after = sql('databases-after-rerun', 'postgres', 'SELECT datname FROM pg_database ORDER BY datname;')
        assert before == after
        assert sql('preserved-after-rerun', 'GameDBPP13', 'SELECT value FROM preserve_me;') == '42'
        # Reproduce 2020's incomplete install, then retry through Clean install.
        # A null restoreProfile must still detect and repair the known IT layout.
        assert next(game for game in migrated['games'] if game['database']=='GameDB20')['restoreProfile'] is None
        keys={1:'category1_attr_fk',2:'category2_attr_fk',3:'category3_attr_fk',4:'category4_fk',5:'category5_fk'}
        sql('partial-2020','GameDB20',''.join(f'ALTER TABLE category{i}_attr DROP CONSTRAINT {key};' for i,key in keys.items())+'CREATE TABLE preserve_partial_2020 (value integer); INSERT INTO preserve_partial_2020 VALUES (2020);')
        partial_receipt=tool/'reports/partial-2020-fixture';partial_receipt.mkdir()
        (partial_receipt/'setup-report.json').write_text(json.dumps({'server':'127.0.0.1','port':port,'plan':False,'entries':[{'requestedDatabase':'GameDB20','database':'GameDB20','restoreDatabase':'GameDB20','restoreDatabaseOid':sql('partial-2020-oid','postgres',"SELECT oid FROM pg_database WHERE datname='GameDB20';"),'status':'Imported; review errors','restoreExitCode':1,'detail':'Restore reported five foreign key errors.'}]}),encoding='utf-8')
        health=json.loads(run('health-partial-2020',tool/'IT Database Builder.exe',health_args))
        assert next(entry for entry in health if entry['requestedDatabase']=='GameDB20')['databaseNeedsRepair']
        repaired20=json.loads(run('clean-corrected-2020',tool/'IT Database Builder.exe',args+['--only','GameDB20','--overwrite-confirmed','GameDB20'],timeout=300))['entries'][0]
        assert repaired20['status']=='Clean installed' and repaired20['restoreExitCode']==0 and repaired20['publicTables']==24, repaired20
        assert sql('clean-2020-keeps-old',repaired20['recoveryDatabase'],'SELECT value FROM preserve_partial_2020;')=='2020'
        assert sql('clean-2020-foreign-keys','GameDB20',"SELECT count(*) FROM pg_constraint WHERE contype='f' AND conname IN ('category1_attr_fk','category2_attr_fk','category3_attr_fk','category4_fk','category5_fk');")=='5'
        source20=fixtures['GameDB20']
        counts20={}
        with tarfile.open(source20,'r:') as archive:
            _,toc20=parse_toc(archive.extractfile('toc.dat').read())
            for item in toc20:
                if item['kind']=='TABLE DATA':
                    table=item['tag'];assert re.fullmatch('[a-z0-9_]+',table)
                    counts20[table]=sum(line not in (b'',b'\\.') for line in archive.extractfile(item['member']).read().splitlines())
        query=' UNION ALL '.join(f"SELECT '{table}'||'|'||count(*) FROM public.{table}" for table in counts20)+';'
        restored20=dict(line.split('|',1) for line in sql('all-2020-table-counts','GameDB20',query).splitlines())
        assert {table:int(count) for table,count in restored20.items()}==counts20
        health=json.loads(run('health-repaired-2020',tool/'IT Database Builder.exe',health_args))
        assert not next(entry for entry in health if entry['requestedDatabase']=='GameDB20')['databaseNeedsRepair']
        results['checks'].append({'corrected_2020_restore':'automatic layout detection, zero errors, all five foreign keys, every table row count matches the original; clean retry preserves the incomplete database as a recovery copy','table_rows':counts20})
        assert all(sha(Path(source)) == value for source, value in hashes.items())
        results['checks'].extend(['different-case existing database and contents preserved', 'missing backups skipped', 'rerun created no duplicates', 'all three source archives unchanged', 'flat and arbitrarily nested backups discovered', 'renamed backup recognized by fingerprint', 'extension added to filename recognized', 'identical duplicate archives accepted'])
        # A late arrival must be discovered on the next run, using its actual date.
        source = fixtures['GameDB13']
        source_hash = sha(source)
        target = tool / 'PG_backup/incoming/2018-01-29/late-renamed.backup'
        target.parent.mkdir(parents=True)
        shutil.copyfile(source, target)
        third = json.loads(run('build-late-backup', tool / 'IT Database Builder.exe', args, timeout=120))
        late = next(e for e in third['entries'] if e['database'] == 'GameDB13')
        assert late['status']=='Imported' and late['restoreExitCode']==0 and late['publicTables'] == 24 and late['settingsVerified'] and '2018-01-29' in late['backup']
        assert sha(source) == source_hash
        results['checks'].append('newly arrived backup built on rerun despite different guide date')
        # The CLI uses the same save/validation operation as the native editor.
        new_game = {'game':'Added Test Game','database':'Added_GameDB','encoding':'SQL_ASCII','backup':'future.backup','backupFolder':'Added Test Game','sha256':None,'restoreProfile':None}
        game_file = test_root/'game-entry.json'
        game_file.write_text(json.dumps(new_game),encoding='utf-8')
        run('editor-add-game',tool/'IT Database Builder.exe',['--root',tool,'--save-game',game_file])
        inventory = json.loads(run('inventory-new-entry', tool / 'IT Database Builder.exe', ['--inventory', '--root', tool]))
        assert len(inventory) == entry_count+1
        added = next(e for e in inventory if e['database'] == 'Added_GameDB')
        assert added['status'] == 'Waiting for backup'
        sql('create-future-seed', 'postgres', 'CREATE DATABASE fixture_seed WITH TEMPLATE=template0;')
        sql('seed-future', 'fixture_seed', 'CREATE TABLE test_data (value integer); INSERT INTO test_data VALUES (99);')
        new_backup = test_root / 'external backups/future-renamed.file'
        new_backup.parent.mkdir()
        pg('dump-future', 'pg_dump', ['-h', '127.0.0.1', '-p', port, '-U', 'postgres', '-F', 'c', '-f', new_backup, 'fixture_seed'])
        external_hash = sha(new_backup)
        before_remember = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
        run('remember-external', tool / 'IT Database Builder.exe', ['--root', tool, '--remember-backup', 'Added_GameDB', new_backup])
        saved_definition = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
        next(g for g in before_remember['games'] if g['database']=='Added_GameDB')['sha256']=external_hash
        next(g for g in before_remember['games'] if g['database']=='Added_GameDB')['databaseFile']=str(new_backup)
        assert saved_definition == before_remember
        assert not (tool / 'PG_backup' / (external_hash + '.backup')).exists()
        assert sha(new_backup) == external_hash
        moved = new_backup  # Keep the original path for preservation assertions.
        fourth = json.loads(run('build-new-entry', tool / 'IT Database Builder.exe', args, timeout=120))
        added = next(e for e in fourth['entries'] if e['database'] == 'Added_GameDB')
        assert added['status'] == 'Imported' and added['publicTables'] == 1 and added['settingsVerified']
        assert added['backup'] == str(new_backup)
        assert not (tool / 'PG_backup' / (external_hash + '.backup')).exists()
        assert sql('verify-future-data', 'Added_GameDB', 'SELECT value FROM test_data;') == '99'
        results['checks'].append('new game saved through the editor operation and restored without recompiling')
        results['checks'].append('external database file restored directly from its original path, then copied into a verified initial backup while the original remains untouched')
        # Reinstallation requires an explicit confirmation and protects the old DB.
        sql('edit-installed-test','Added_GameDB','UPDATE test_data SET value=88;')
        unchanged=json.loads(run('existing-no-confirmation',tool/'IT Database Builder.exe',args+['--only','Added_GameDB']))
        assert unchanged['entries'][0]['status']=='Already present'
        assert sql('existing-still-88','Added_GameDB','SELECT value FROM test_data;')=='88'
        clean_args=args+['--only','Added_GameDB','--overwrite-confirmed','Added_GameDB']
        planned=json.loads(run('clean-plan',tool/'IT Database Builder.exe',[str(a).replace('--build','--plan') for a in clean_args]))
        assert planned['entries'][0]['status']=='Would clean install'
        assert sql('plan-preserves-old','Added_GameDB','SELECT value FROM test_data;')=='88'
        # An idle client connection must prevent activation, without forced disconnects.
        client=subprocess.Popen([str(STOCK/'bin/psql.exe'),'-X','-q','-h','127.0.0.1','-p',str(port),'-U','postgres','-d','Added_GameDB'],env=env,stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            import time
            for attempt in range(30):
                if int(sql('check-idle-client','postgres',"SELECT count(*) FROM pg_stat_activity WHERE datname='Added_GameDB';"))>0:
                    break
                time.sleep(0.1)
            else:
                raise AssertionError('Owned idle test client did not connect')
            held=json.loads(run('clean-in-use',tool/'IT Database Builder.exe',clean_args))
            assert held['entries'][0]['status']=='Needs review' and held['entries'][0]['recoveryDatabase'] is None
            assert sql('in-use-preserves-old','Added_GameDB','SELECT value FROM test_data;')=='88'
        finally:
            client.stdin.close()
            client.wait(timeout=10)
        reinstalled=json.loads(run('clean-confirmed',tool/'IT Database Builder.exe',clean_args))
        clean_entry=reinstalled['entries'][0]
        assert clean_entry['status']=='Clean installed' and clean_entry['database']=='Added_GameDB'
        assert clean_entry['recoveryDatabase'] and clean_entry['settingsVerified']
        assert sql('clean-data-reset','Added_GameDB','SELECT value FROM test_data;')=='99'
        assert sql('clean-old-copy-kept',clean_entry['recoveryDatabase'],'SELECT value FROM test_data;')=='88'
        assert sha(new_backup)==external_hash
        sql('original-error-marker','GameDB06','CREATE TABLE preserve_failed_clean (value integer); INSERT INTO preserve_failed_clean VALUES (42);')
        # Reproduce the old UTF8 failure while checking original-data protection.
        good_definition=manifest_path.read_bytes()
        broken=json.loads(good_definition)
        broken06=next(game for game in broken['games'] if game['database']=='GameDB06')
        broken06['encoding']='UTF8';broken06['restoreProfile']=None
        manifest_path.write_text(json.dumps(broken),encoding='utf-8')
        failed_clean=json.loads(run('clean-restore-errors',tool/'IT Database Builder.exe',args+['--only','GameDB06','--overwrite-confirmed','GameDB06']))
        manifest_path.write_bytes(good_definition)
        error_entry=failed_clean['entries'][0]
        assert error_entry['status']=='Needs review' and error_entry['restoreDatabase'] and error_entry['recoveryDatabase'] is None
        assert sql('restore-errors-preserve-old','GameDB06','SELECT value FROM preserve_failed_clean;')=='42'
        assert error_entry['restoreExitCode']!=0
        health=json.loads(run('failed-staging-does-not-mark-original-bad',tool/'IT Database Builder.exe',health_args))
        assert not next(entry for entry in health if entry['requestedDatabase']=='GameDB06')['databaseNeedsRepair']
        repaired=json.loads(run('clean-corrected-2006',tool/'IT Database Builder.exe',args+['--only','GameDB06','--overwrite-confirmed','GameDB06']))['entries'][0]
        assert repaired['status']=='Clean installed' and repaired['restoreExitCode']==0
        assert sql('corrected-clean-keeps-old-marker',repaired['recoveryDatabase'],'SELECT value FROM preserve_failed_clean;')=='42'
        assert int(sql('corrected-clean-complete-rows','GameDB06','SELECT count(*) FROM category2_attr;'))==rows06
        remove_args=['--remove','--root',tool,'--port',port,'--pg-bin',STOCK/'bin','--only','Added_GameDB']
        unconfirmed=subprocess.run([str(tool/'IT Database Builder.exe'),*map(str,remove_args)],env=env,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
        assert unconfirmed.returncode!=0 and b'explicit confirmation' in unconfirmed.stderr
        assert sql('unconfirmed-remove-keeps-data','Added_GameDB','SELECT value FROM test_data;')=='99'
        preview=json.loads(run('remove-preview',tool/'IT Database Builder.exe',remove_args+['--plan']))
        assert preview['entries'][0]['status']=='Would remove'
        assert sql('remove-preview-keeps-data','Added_GameDB','SELECT value FROM test_data;')=='99'
        client=subprocess.Popen([str(STOCK/'bin/psql.exe'),'-X','-q','-h','127.0.0.1','-p',str(port),'-U','postgres','-d','Added_GameDB'],env=env,stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            for attempt in range(30):
                if int(sql('check-remove-client','postgres',"SELECT count(*) FROM pg_stat_activity WHERE datname='Added_GameDB';"))>0:break
                time.sleep(0.1)
            else:raise AssertionError('Owned removal test client did not connect')
            blocked=json.loads(run('remove-in-use',tool/'IT Database Builder.exe',remove_args+['--remove-confirmed']))
            assert blocked['entries'][0]['status']=='Needs review'
            assert sql('in-use-remove-keeps-data','Added_GameDB','SELECT value FROM test_data;')=='99'
        finally:
            client.stdin.close();client.wait(timeout=10)
        removed=json.loads(run('remove-confirmed',tool/'IT Database Builder.exe',remove_args+['--remove-confirmed']))
        assert removed['entries'][0]['status']=='Removed'
        assert sql('only-selected-removed','postgres',"SELECT count(*) FROM pg_database WHERE datname='Added_GameDB';")=='0'
        assert sql('remove-preserves-unselected','GameDBPP13','SELECT value FROM preserve_me;')=='42'
        assert sql('remove-keeps-recovery-copy',clean_entry['recoveryDatabase'],'SELECT value FROM test_data;')=='88'
        assert sha(new_backup)==external_hash and sha(moved)==external_hash
        readded=json.loads(run('reinstall-after-removal',tool/'IT Database Builder.exe',args+['--only','Added_GameDB']))
        assert readded['entries'][0]['status']=='Imported'
        assert sql('reinstalled-after-removal-data','Added_GameDB','SELECT value FROM test_data;')=='99'
        results['checks'].append('Remove requires confirmation, previews without changes, blocks active connections, removes only checked databases, preserves original files and recovery copies; Install restores it afterward')
        results['checks'].append('clean install requires confirmation, previews without changing data, blocks active connections and restore errors, restores original contents, and retains the previous database as a recovery copy')
        # Edit one game's name, preserving every other entry and connection setting.
        new_game['sha256']=external_hash
        new_game['databaseFile']=str(new_backup)
        new_game['game']='Updated game name'
        game_file.write_text(json.dumps(new_game),encoding='utf-8')
        run('editor-edit-game',tool/'IT Database Builder.exe',['--root',tool,'--save-game',game_file,'--replace','Added_GameDB'])
        expected_edit = saved_definition
        next(g for g in expected_edit['games'] if g['database']=='Added_GameDB')['game']='Updated game name'
        assert json.loads(manifest_path.read_text(encoding='utf-8-sig'))==expected_edit
        prior_file = manifest_path.read_bytes()
        duplicate_result = subprocess.run([str(tool/'IT Database Builder.exe'),'--root',str(tool),'--save-game',str(game_file)],env=env,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
        assert duplicate_result.returncode!=0 and manifest_path.read_bytes()==prior_file
        results['checks'].append('game edit preserves other definitions and password; duplicate save rejected without altering JSON')
        # Ambiguous names must never create either game's database automatically.
        manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
        for name in ('CollisionA','CollisionB'):
            manifest['games'].append({'game':name,'database':name,'encoding':'SQL_ASCII','backup':'future.backup'})
        manifest_path.write_text(json.dumps(manifest,indent=2),encoding='utf-8')
        shutil.copyfile(new_backup, tool / 'PG_backup/future.backup')
        ambiguous = json.loads(run('ambiguous-build', tool / 'IT Database Builder.exe', args + ['--only', 'CollisionA,CollisionB']))
        assert ambiguous['completed'] and all(e['status'] == 'Choose backup' for e in ambiguous['entries'])
        assert sql('ambiguous-not-created', 'postgres', "SELECT count(*) FROM pg_database WHERE datname IN ('CollisionA','CollisionB');") == '0'
        # Different content under the same unique filename also needs a choice.
        manifest['games'].append({'game':'Conflict','database':'Conflict','encoding':'SQL_ASCII','backup':'conflicting.backup'})
        manifest_path.write_text(json.dumps(manifest,indent=2),encoding='utf-8')
        shutil.copyfile(new_backup, tool / 'PG_backup/conflicting.backup')
        shutil.copyfile(tool / destinations['GameDB06'], tool / 'pg_backups/conflicting.backup')
        conflict = json.loads(run('conflicting-build', tool / 'IT Database Builder.exe', args + ['--only', 'Conflict']))
        assert conflict['entries'][0]['status'] == 'Choose backup'
        results['checks'].append('ambiguous filenames and different matching backups skipped before database creation')
        # Startup health checks must flag partial, empty and wrong-encoding DBs.
        for name in ('HealthEmpty','HealthWrongEncoding','HealthFailedReport'):
            manifest['games'].append({'game':name,'database':name,'encoding':'SQL_ASCII','backup':'unavailable.file'})
            encoding='UTF8' if name=='HealthWrongEncoding' else 'SQL_ASCII'
            sql('create-'+name,'postgres','CREATE DATABASE "'+name+'" WITH TEMPLATE=template0 ENCODING=\''+encoding+'\';')
            if name!='HealthEmpty':sql('seed-'+name,name,'CREATE TABLE game_data (id integer);')
        manifest['games'].append({'game':'Manual IT install','database':'HealthManualIT','encoding':'SQL_ASCII','backup':'unavailable.file'})
        sql('create-manual-it','postgres',"CREATE DATABASE \"HealthManualIT\" WITH TEMPLATE=template0 ENCODING='SQL_ASCII';")
        sql('seed-manual-it','HealthManualIT',''.join(f'CREATE TABLE category{i}_object (object_id integer); CREATE TABLE category{i}_attr (object_id numeric(38,0)); INSERT INTO category{i}_object VALUES ({i});' for i in range(1,6)))
        manifest_path.write_text(json.dumps(manifest),encoding='utf-8')
        failed_report=tool/'reports/health-failed-fixture';failed_report.mkdir()
        (failed_report/'setup-report.json').write_text(json.dumps({'server':'127.0.0.1','port':port,'plan':False,'entries':[{'requestedDatabase':'HealthFailedReport','database':'HealthFailedReport','restoreDatabase':'HealthFailedReport','restoreDatabaseOid':sql('failed-report-oid','postgres',"SELECT oid FROM pg_database WHERE datname='HealthFailedReport';"),'status':'Imported; review errors','detail':'COPY failed for game_data'}]}),encoding='utf-8')
        health_before=sql('health-check-before','postgres','SELECT datname FROM pg_database ORDER BY datname;')
        states={entry['requestedDatabase']:entry for entry in json.loads(run('health-flags-incomplete',tool/'IT Database Builder.exe',health_args))}
        for name in ('HealthEmpty','HealthFailedReport'):
            assert states[name]['databaseInstalled'] and states[name]['databaseNeedsRepair'],states[name]
        assert not states['HealthWrongEncoding']['databaseNeedsRepair'] and states['HealthWrongEncoding']['databaseCheckWarning'],states['HealthWrongEncoding']
        assert not states['GameDB06']['databaseNeedsRepair']
        assert states['HealthManualIT']['databaseInstalled'] and not states['HealthManualIT']['databaseNeedsRepair'],states['HealthManualIT']
        assert sql('manual-it-preserved','HealthManualIT','SELECT object_id FROM category5_object;')=='5'
        assert health_before==sql('health-check-after','postgres','SELECT datname FROM pg_database ORDER BY datname;')
        results['checks'].append('read-only startup health flags empty databases and identified incomplete app restores; encoding differences are informational; manual IT installs without foreign keys remain installed and untouched; failed staged restores do not taint the original')
        # The same IT table names with already-compatible keys must stay intact.
        sql('create-compatible-seed','postgres','CREATE DATABASE compatible_seed WITH TEMPLATE=template0;')
        compatible_sql=''
        for category,key in keys.items():
            compatible_sql+=f'CREATE TABLE category{category}_object (object_id integer PRIMARY KEY); CREATE TABLE category{category}_attr (object_id integer, value text, CONSTRAINT {key} FOREIGN KEY (object_id) REFERENCES category{category}_object(object_id)); INSERT INTO category{category}_object VALUES ({category}); INSERT INTO category{category}_attr VALUES ({category},\'compatible\');'
        sql('seed-compatible-it','compatible_seed',compatible_sql)
        compatible_file=tool/'PG_backup/compatible-it.dump'
        pg('dump-compatible-it','pg_dump',['-h','127.0.0.1','-p',port,'-U','postgres','-F','c','-f',compatible_file,'compatible_seed'])
        compatible_hash=sha(compatible_file)
        manifest['games'].append({'game':'Compatible IT','database':'CompatibleIT','encoding':'SQL_ASCII','backup':'compatible-it.dump','restoreProfile':None})
        manifest_path.write_text(json.dumps(manifest),encoding='utf-8')
        compatible=json.loads(run('build-compatible-it',tool/'IT Database Builder.exe',args+['--only','CompatibleIT']))['entries'][0]
        assert compatible['status']=='Imported' and compatible['restoreExitCode']==0 and compatible['publicTables']==10,compatible
        assert sql('compatible-types-retained','CompatibleIT',"SELECT count(*) FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_type t ON t.oid=a.atttypid WHERE n.nspname='public' AND c.relname ~ '^category[1-5]_(object|attr)$' AND a.attname='object_id' AND t.typname='int4';")=='10'
        assert sql('compatible-data-retained','CompatibleIT',"SELECT count(*) FROM category5_attr WHERE object_id=5 AND value='compatible';")=='1'
        assert sha(compatible_file)==compatible_hash
        results['checks'].append('IT layout detection preserves already-compatible integer keys and original data; unrelated custom database schemas also restore unchanged')
        sql('create-other-schema-seed','postgres',"CREATE DATABASE other_schema_seed WITH TEMPLATE=template0 ENCODING='SQL_ASCII';")
        sql('seed-other-schema','other_schema_seed','CREATE SCHEMA arcade; CREATE TABLE arcade.game_data (value integer); INSERT INTO arcade.game_data VALUES (83);')
        other_schema_file=tool/'PG_backup/other-schema.dump'
        pg('dump-other-schema','pg_dump',['-h','127.0.0.1','-p',port,'-U','postgres','-F','c','-f',other_schema_file,'other_schema_seed'])
        manifest['games'].append({'game':'Other schema game','database':'OtherSchemaGame','encoding':'SQL_ASCII','backup':'other-schema.dump'})
        manifest_path.write_text(json.dumps(manifest),encoding='utf-8')
        other_schema=json.loads(run('install-other-schema',tool/'IT Database Builder.exe',args+['--only','OtherSchemaGame']))['entries'][0]
        assert other_schema['status']=='Imported' and other_schema['publicTables']==0 and other_schema['gameTables']==1,other_schema
        state=next(entry for entry in json.loads(run('other-schema-health',tool/'IT Database Builder.exe',health_args)) if entry['requestedDatabase']=='OtherSchemaGame')
        assert state['installVerified'] and not state['databaseNeedsRepair']
        assert sql('other-schema-data-preserved','OtherSchemaGame','SELECT value FROM arcade.game_data;')=='83'
        results['checks'].append('game tables in another schema restore and validate correctly; a database with no public tables is not mistaken for an empty database')
        # One damaged data stream must not publish an incomplete game DB or stop
        # the next checked game. Both are temporary fixture archives.
        batch_tar=test_root/'batch-source.tar'
        pg('dump-batch-tar','pg_dump',['-h','127.0.0.1','-p',port,'-U','postgres','-F','t','-f',batch_tar,'fixture_seed'])
        damaged=tool/'PG_backup/bad-batch.tar'
        with tarfile.open(batch_tar,'r:') as source_tar,tarfile.open(damaged,'w',format=tarfile.USTAR_FORMAT) as destination_tar:
            changed_data=False
            for member in source_tar.getmembers():
                payload=source_tar.extractfile(member).read()
                if member.name.endswith('.dat') and member.name!='toc.dat':
                    payload=b'not-an-integer\n\\.\n';changed_data=True
                member.size=len(payload)
                import io
                destination_tar.addfile(member,io.BytesIO(payload))
            assert changed_data
        manifest['games']+=[{'game':'Bad batch game','database':'BadBatch','encoding':'SQL_ASCII','backup':'bad-batch.tar'},
                            {'game':'Good batch game','database':'GoodBatch','encoding':'SQL_ASCII','backup':'good-batch.dump','sha256':external_hash}]
        manifest_path.write_text(json.dumps(manifest),encoding='utf-8')
        batch=json.loads(run('mixed-batch',tool/'IT Database Builder.exe',args+['--only','BadBatch,GoodBatch']))
        bad,good=batch['entries']
        assert batch['completed'] and bad['status']=='Needs review' and bad['restoreExitCode']!=0 and not bad['databaseInstalled'],bad
        assert good['status']=='Imported' and good['databaseInstalled'] and good['restoreDatabase']=='GoodBatch',good
        assert sql('bad-batch-not-published','postgres',"SELECT count(*) FROM pg_database WHERE datname='BadBatch';")=='0'
        assert sql('good-batch-data','GoodBatch','SELECT value FROM test_data;')=='99'
        assert sql('bad-staging-transaction-rolled-back',bad['restoreDatabase'],"SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind='r';")=='0'
        states={entry['requestedDatabase']:entry for entry in json.loads(run('batch-health',tool/'IT Database Builder.exe',health_args))}
        assert not states['BadBatch']['databaseInstalled'] and states['GoodBatch']['installVerified']
        # Reports are disposable. Keep local verification independently, and
        # never attach an old report to a manually recreated database's new OID.
        history_path=tool/'InstallHistory.json'
        assert history_path.is_file()
        retained_reports=test_root/'retained-reports'
        assert (tool/'reports').resolve().is_relative_to(test_root.resolve()) and retained_reports.resolve().is_relative_to(test_root.resolve())
        (tool/'reports').rename(retained_reports)
        states={entry['requestedDatabase']:entry for entry in json.loads(run('health-without-reports',tool/'IT Database Builder.exe',health_args))}
        assert states['GoodBatch']['installVerified'] and not states['BadBatch']['databaseInstalled']
        old_oid=good['restoreDatabaseOid']
        sql('manual-recreate-good-batch','postgres','DROP DATABASE "GoodBatch";')
        sql('manual-create-good-batch','postgres',"CREATE DATABASE \"GoodBatch\" WITH TEMPLATE=template0 ENCODING='SQL_ASCII';")
        sql('manual-good-batch-data','GoodBatch','CREATE TABLE test_data (value integer); INSERT INTO test_data VALUES (77);')
        stale_folder=tool/'reports/stale-database-fixture';stale_folder.mkdir(parents=True)
        (stale_folder/'setup-report.json').write_text(json.dumps({'server':'127.0.0.1','port':port,'entries':[{'requestedDatabase':'GoodBatch','database':'GoodBatch','restoreDatabase':'GoodBatch','restoreDatabaseOid':old_oid,'status':'Imported; review errors','detail':'Failed old database instance'}]}),encoding='utf-8')
        foreign_folder=tool/'reports/other-server-fixture';foreign_folder.mkdir()
        (foreign_folder/'setup-report.json').write_text(json.dumps({'server':'different-server','port':port,'entries':[{'requestedDatabase':'GoodBatch','database':'GoodBatch','restoreDatabase':'GoodBatch','restoreDatabaseOid':sql('new-good-batch-oid','postgres',"SELECT oid FROM pg_database WHERE datname='GoodBatch';"),'status':'Imported; review errors','detail':'Failure on a different server'}]}),encoding='utf-8')
        states={entry['requestedDatabase']:entry for entry in json.loads(run('recreated-db-health',tool/'IT Database Builder.exe',health_args))}
        assert states['GoodBatch']['databaseInstalled'] and not states['GoodBatch']['databaseNeedsRepair'] and not states['GoodBatch']['installVerified'],states['GoodBatch']
        assert json.loads(run('preserve-manual-good-batch',tool/'IT Database Builder.exe',args+['--only','GoodBatch']))['entries'][0]['status']=='Already present'
        assert sql('manual-good-batch-preserved','GoodBatch','SELECT value FROM test_data;')=='77'
        results['checks'].append('new installs publish only after successful transactional restore; damaged archive stays temporary and the next checked game installs; deleted reports retain local verification; recreated manual databases and other-server receipts cannot inherit stale failures')
        verify_backups(tool, test_root, STOCK, port, env, run, sql, pg, results)
        protected=json.loads(manifest_path.read_text(encoding='utf-8-sig'))
        protected['games'].append({'game':'Protected','database':'postgres','encoding':'UTF8','backup':'never.file'})
        manifest_path.write_text(json.dumps(protected),encoding='utf-8')
        system_remove=subprocess.run([str(tool/'IT Database Builder.exe'),'--remove','--remove-confirmed','--root',str(tool),'--port',str(port),'--pg-bin',str(STOCK/'bin'),'--only','postgres'],env=env,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
        assert system_remove.returncode!=0 and b'System databases cannot be removed' in system_remove.stderr
        manifest_path.write_text(json.dumps(manifest),encoding='utf-8')
        # Invalid definitions must stop before creating any database.
        manifest['games'].append({'game':'Invalid','database':'Bad;Name','encoding':'UTF8','backup':'invalid.backup'})
        manifest_path.write_text(json.dumps(manifest),encoding='utf-8')
        prior = sql('before-invalid', 'postgres', 'SELECT datname FROM pg_database ORDER BY datname;')
        invalid = subprocess.run([str(tool/'IT Database Builder.exe'), *map(str,args)], env=env, capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
        assert invalid.returncode != 0
        assert prior == sql('after-invalid', 'postgres', 'SELECT datname FROM pg_database ORDER BY datname;')
        results['checks'].append('invalid definition rejected before database changes')
        manifest['games'].pop()
        good_json=json.dumps(manifest,indent=2)
        bad_json=[('{"schemaVersion":', 'malformed JSON'),(good_json.replace('"schemaVersion": 1','"schemaVersion": 99'),'unsupported schema'),(good_json.replace('"backup":','"backupp":',1),'unknown field')]
        for content,label in bad_json:
            manifest_path.write_text(content,encoding='utf-8')
            invalid=subprocess.run([str(tool/'IT Database Builder.exe'),*map(str,args)],env=env,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
            assert invalid.returncode!=0
            assert prior==sql('after-'+label.replace(' ','-'),'postgres','SELECT datname FROM pg_database ORDER BY datname;')
        manifest_path.write_text(good_json,encoding='utf-8')
        results['checks'].append('malformed JSON, unknown fields and unsupported schema rejected before database changes')
        results['passed'] = True
    finally:
        if started and (data / 'postmaster.pid').exists():
            pg('stop', 'pg_ctl', ['-D', data, '-m', 'fast', '-w', 'stop'])
            results['test_server_stopped'] = True
        if password_file.exists():
            password_file.unlink()
        results['source_hashes'] = hashes
        destination = ROOT / 'output/native-integration-verification-v1.2.0.json'
        destination.write_text(json.dumps(results, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(results, indent=2))


if __name__ == '__main__':
    main()
