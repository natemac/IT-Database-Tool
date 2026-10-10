"""Backup round-trip checks called inside the native test's private cluster."""
import hashlib
import json
import os
from datetime import datetime
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess


def verify_backups(tool, test_root, stock, port, env, run, sql, pg, results):
    exe = tool / 'IT Database Builder.exe'
    catalog = tool / 'DatabaseInfo.json'
    settings = tool / 'UserSettings.json'
    catalog_before = catalog.read_bytes()
    preferences = json.loads(settings.read_text(encoding='utf-8-sig'))
    preferences['customPreference'] = {'keep': True}
    settings.write_text(json.dumps(preferences), encoding='utf-8')
    # A sequence, Unicode, another schema, and an altered encoding exercise
    # actual data preservation rather than just listing the dump archive.
    sql('backup-seed-extra', 'GoodBatch', "CREATE SCHEMA arcade; CREATE TABLE arcade.progress (id serial PRIMARY KEY, score integer, label text); INSERT INTO arcade.progress (score,label) VALUES (1200,'saved game');")
    game06_rows = sql('backup-game06-original-rows', 'GameDB06', 'SELECT count(*) FROM category2_attr;')
    base = ['--backup', '--root', tool, '--port', port, '--pg-bin', stock / 'bin']
    backup_dates = {}

    def backup(name, databases='GoodBatch', action=None):
        args = base + ['--only', databases]
        if action:
            args += ['--backup-existing', action]
        start = datetime.now().date()
        report = json.loads(run(name, exe, args))
        backup_dates[name] = {start, datetime.now().date()}
        return report

    def assert_layout(record, operation, version=1):
        parts = PurePosixPath(record['file']).parts
        assert len(parts) == 3 and parts[0] == 'pg_backup', record
        date = datetime.strptime(parts[1], '%Y-%m-%d').date()
        assert date in backup_dates[operation], record
        title = re.sub(r'\bLive\b', '', record['game'], flags=re.IGNORECASE)
        title = re.sub(r'[^A-Za-z0-9]', '', title.replace('&', 'And'))
        if not title:
            title = re.sub(r'[^A-Za-z0-9]', '', record['requestedDatabase'])
        suffix = '' if version == 1 else f'_{version}'
        assert parts[2] == f'{title}_{date:%y%m%d}{suffix}_DBbackup', record

    def history():
        document = json.loads(settings.read_text(encoding='utf-8-sig'))
        assert document['connection'] == preferences['connection']
        assert document['hideWelcome'] == preferences['hideWelcome']
        assert document['customPreference'] == preferences['customPreference']
        return document['backups']

    def digest(path):
        return hashlib.sha256(path.read_bytes()).hexdigest()

    baseline = history()
    initial = [r for r in baseline if r.get('kind') == 'initial']
    assert initial and all(r.get('kind') == 'initial' for r in baseline), baseline
    assert len({(r['server'], r['port'], r['requestedDatabase']) for r in initial}) == len(initial), 'Reinstalling must not add another initial archive'
    inventory = json.loads(run('initial-backup-original-inventory', exe, ['--inventory', '--root', tool]))
    current06 = next(r for r in inventory if r['requestedDatabase'] == 'GameDB06')
    initial06 = next(r for r in initial if r['requestedDatabase'] == 'GameDB06')
    initial06_path = tool / initial06['file']
    # Installation reports retain the actual immutable archive used for that
    # restore, while refreshed inventory uses the newly selected initial copy.
    first_install = json.loads((test_root / 'build-specific.stdout').read_text(encoding='utf-8-sig'))
    source06_path = Path(next(r for r in first_install['entries'] if r['requestedDatabase'] == 'GameDB06')['backup'])
    source06_digest = digest(source06_path)
    assert initial06_path.resolve() != source06_path.resolve()
    assert initial06_path.read_bytes() == source06_path.read_bytes()
    assert digest(initial06_path) == initial06['sha256']
    assert Path(current06['backup']).resolve() == initial06_path.resolve()
    selected06 = next(g for g in json.loads(catalog_before)['games'] if g['database'] == 'GameDB06')
    assert Path(selected06['databaseFile']).resolve() == initial06_path.resolve() and selected06['sha256'] == initial06['sha256']
    # A second process/refresh must resolve the persisted copy rather than the
    # original game-folder source, without needing to rediscover it.
    refreshed = json.loads(run('initial-backup-persisted-inventory', exe, ['--inventory', '--root', tool]))
    assert Path(next(r for r in refreshed if r['requestedDatabase'] == 'GameDB06')['backup']).resolve() == initial06_path.resolve()
    for record in initial:
        path = tool / record['file']
        assert path.is_file() and digest(path) == record['sha256'] and path.stat().st_size == record['bytes']
        assert PurePosixPath(record['file']).parts[0] == 'pg_backup'
    assert sum(r['requestedDatabase'] == 'Added_GameDB' for r in initial) == 1, 'Earlier remove/reinstall must preserve a single first-install archive'
    # Already present and Clean install retain the original initial snapshot.
    build_base = ['--build', '--root', tool, '--port', port, '--pg-bin', stock / 'bin']
    already = json.loads(run('initial-backup-already-present', exe, build_base + ['--only', 'GoodBatch']))
    assert already['entries'][0]['status'] == 'Already present' and history() == baseline
    run('initial-backup-select-game06', exe, ['--remember-backup', 'GameDB06', initial06_path, '--root', tool])
    initial_restore = json.loads(run('initial-backup-restore-game06', exe, build_base + ['--only', 'GameDB06', '--overwrite-confirmed', 'GameDB06']))
    assert initial_restore['entries'][0]['status'] == 'Clean installed', initial_restore
    assert initial_restore['entries'][0]['restoreExitCode'] == 0
    assert sql('initial-backup-game06-keys', 'GameDB06', "SELECT count(*) FROM pg_constraint WHERE contype='f' AND conname IN ('category1_attr_fk','category2_attr_fk','category3_attr_fk','category4_fk','category5_fk');") == '5'
    assert sql('initial-backup-game06-rows', 'GameDB06', 'SELECT count(*) FROM category2_attr;') == game06_rows
    assert history() == baseline and initial06_path.read_bytes() == source06_path.read_bytes()
    assert digest(source06_path) == source06_digest
    catalog.write_bytes(catalog_before)
    # The initial copy counts as an existing backup, so default export must ask.
    initial_settings = settings.read_bytes()
    initial_prompt = subprocess.run([str(exe), *map(str, base + ['--only', 'GoodBatch'])], env=env, capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
    assert initial_prompt.returncode != 0 and b'backup already exists' in initial_prompt.stderr
    assert settings.read_bytes() == initial_settings
    first = backup('backup-first', 'GoodBatch,GAMEDBPP13,GameDB06', 'keep')
    assert first['completed'] and all(e['status'] == 'Backed up' for e in first['entries']), first
    records = [r for r in history() if r.get('kind') != 'initial']
    assert len(records) == 3
    for record in records:
        path = tool / record['file']
        previous_versions = sum(r['requestedDatabase'] == record['requestedDatabase'] and PurePosixPath(r['file']).parent == PurePosixPath(record['file']).parent for r in baseline)
        assert_layout(record, 'backup-first', previous_versions + 1)
        assert path.resolve().is_relative_to((tool / 'pg_backup').resolve())
        assert digest(path) == record['sha256'] and path.stat().st_size == record['bytes']
        assert record['port'] == port and record['databaseOid'] and record['createdUtc']
        assert record['kind'] == 'export'
        assert 'password' not in record and 'username' not in record
    original = next(r for r in records if r['requestedDatabase'] == 'GoodBatch')
    assert next(r for r in records if r['requestedDatabase'] == 'GAMEDBPP13')['database'] == 'GameDBPP13'
    assert original['encoding'] == 'SQL_ASCII'
    first_path = tool / original['file']
    first_hash = digest(first_path)
    untouched_settings = settings.read_bytes()
    prompt = subprocess.run([str(exe), *map(str, base + ['--only', 'GoodBatch'])], env=env, capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
    assert prompt.returncode != 0 and b'backup already exists' in prompt.stderr
    assert settings.read_bytes() == untouched_settings and digest(first_path) == first_hash
    sql('backup-update-score', 'GoodBatch', 'UPDATE arcade.progress SET score=2400;')
    kept = backup('backup-keep', action='keep')
    assert kept['entries'][0]['status'] == 'Backed up', kept
    assert len(history()) == len(baseline) + 4 and digest(first_path) == first_hash
    latest = history()[-1]
    latest_path = tool / latest['file']
    prior_versions = sum(r['requestedDatabase'] == 'GoodBatch' and PurePosixPath(r['file']).parent == PurePosixPath(latest['file']).parent for r in baseline + records)
    assert_layout(latest, 'backup-keep', prior_versions + 1)
    sql('backup-update-latest-score', 'GoodBatch', 'UPDATE arcade.progress SET score=3600;')
    replaced = backup('backup-overwrite', action='overwrite')
    assert replaced['entries'][0]['status'] == 'Backed up', replaced
    assert len(history()) == len(baseline) + 4 and history()[-1]['file'] == latest['file']
    assert digest(first_path) == first_hash and digest(latest_path) != latest['sha256']
    pg('backup-restore-db', 'createdb', ['-h', '127.0.0.1', '-p', port, '-U', 'postgres', '-T', 'template0', '-E', 'SQL_ASCII', 'BackupRoundTrip'])
    pg('backup-restore-data', 'pg_restore', ['-h', '127.0.0.1', '-p', port, '-U', 'postgres', '-d', 'BackupRoundTrip', '--no-owner', '--no-acl', '--exit-on-error', '--single-transaction', latest_path])
    assert sql('backup-score-round-trip', 'BackupRoundTrip', 'SELECT score FROM arcade.progress;') == '3600'
    assert sql('backup-main-round-trip', 'BackupRoundTrip', 'SELECT value FROM test_data;') == '77'
    assert sql('backup-sequence-round-trip', 'BackupRoundTrip', "INSERT INTO arcade.progress (score,label) VALUES (4800,'next game') RETURNING id;") == '2'
    # The original version must still restore the earlier progress.
    pg('backup-old-db', 'createdb', ['-h', '127.0.0.1', '-p', port, '-U', 'postgres', '-T', 'template0', '-E', 'SQL_ASCII', 'BackupOldVersion'])
    pg('backup-old-data', 'pg_restore', ['-h', '127.0.0.1', '-p', port, '-U', 'postgres', '-d', 'BackupOldVersion', '--no-owner', '--no-acl', '--exit-on-error', first_path])
    assert sql('backup-old-score', 'BackupOldVersion', 'SELECT score FROM arcade.progress;') == '1200'
    assert catalog.read_bytes() == catalog_before
    # Exercise the same bound restore request used by picker/confirmation, with
    # an older chosen export rather than whichever file is newest in history.
    compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    restore_probe = tool / 'RecordedRestoreTest.exe'
    subprocess.run([str(compiler), '/nologo', '/target:exe', '/reference:System.Web.Extensions.dll', '/reference:'+str(exe), '/out:'+str(restore_probe), str(Path(__file__).with_name('test_recorded_restore.cs'))], check=True, capture_output=True)
    probe_args = [tool, stock / 'bin', port, 'GoodBatch', original['file']]
    assert 'changed source rejected' in run('backup-bound-restore-changed', restore_probe, probe_args + ['changed'])
    assert sql('backup-bound-rejection-data', 'GoodBatch', 'SELECT score FROM arcade.progress;') == '3600'
    chosen = json.loads(run('backup-bound-restore-old', restore_probe, probe_args + ['restore']))
    assert chosen['entries'][0]['status'] == 'Clean installed'
    assert sql('backup-bound-old-score', 'GoodBatch', 'SELECT score FROM arcade.progress;') == '1200'
    newest = next(r for r in reversed(history()) if r['requestedDatabase'] == 'GoodBatch')
    chosen = json.loads(run('backup-bound-restore-latest', restore_probe, [tool, stock / 'bin', port, 'GoodBatch', newest['file'], 'restore']))
    assert chosen['entries'][0]['status'] == 'Clean installed'
    assert sql('backup-bound-latest-score', 'GoodBatch', 'SELECT score FROM arcade.progress;') == '3600'
    catalog.write_bytes(catalog_before)
    # Restore the exported file through the app's normal staged installer too.
    definition = json.loads(catalog_before)
    definition['games'].append(dict(game='Backup restore fixture', database='BackupAppRestore', encoding='SQL_ASCII', backup='never-autoload.dump', databaseFile=str(latest_path), sha256=digest(latest_path)))
    catalog.write_text(json.dumps(definition), encoding='utf-8')
    restored = json.loads(run('backup-app-restore', exe, ['--build', '--root', tool, '--port', port, '--pg-bin', stock / 'bin', '--only', 'BackupAppRestore']))
    assert restored['entries'][0]['status'] == 'Imported', restored
    assert sql('backup-app-restored-score', 'BackupAppRestore', 'SELECT score FROM arcade.progress;') == '3600'
    restore_initial = next(r for r in history() if r['requestedDatabase'] == 'BackupAppRestore')
    assert restore_initial['kind'] == 'initial' and (tool / restore_initial['file']).read_bytes() == latest_path.read_bytes()
    restore_choice = next(g for g in json.loads(catalog.read_text(encoding='utf-8-sig'))['games'] if g['database'] == 'BackupAppRestore')
    assert Path(restore_choice['databaseFile']).resolve() == (tool / restore_initial['file']).resolve()
    assert restore_choice['sha256'] == restore_initial['sha256']
    restored_inventory = json.loads(run('initial-backup-new-install-inventory', exe, ['--inventory', '--root', tool]))
    assert Path(next(r for r in restored_inventory if r['requestedDatabase'] == 'BackupAppRestore')['backup']).resolve() == (tool / restore_initial['file']).resolve()
    catalog.write_bytes(catalog_before)
    # An original game profile must not be reapplied to its already-migrated
    # installed schema. Select and clean-restore our GameDB06 backup in the app.
    game06 = next(r for r in records if r['requestedDatabase'] == 'GameDB06')
    game06_path = tool / game06['file']
    run('backup-select-game06', exe, ['--remember-backup', 'GameDB06', game06_path, '--root', tool])
    clean06 = json.loads(run('backup-restore-game06', exe, ['--build', '--root', tool, '--port', port, '--pg-bin', stock / 'bin', '--only', 'GameDB06', '--overwrite-confirmed', 'GameDB06']))
    assert clean06['entries'][0]['status'] == 'Clean installed', clean06
    assert sql('backup-game06-keys', 'GameDB06', "SELECT count(*) FROM pg_constraint WHERE contype='f' AND conname IN ('category1_attr_fk','category2_attr_fk','category3_attr_fk','category4_fk','category5_fk');") == '5'
    assert sql('backup-game06-restored-rows', 'GameDB06', 'SELECT count(*) FROM category2_attr;') == game06_rows
    catalog.write_bytes(catalog_before)
    power_putt = next(r for r in records if r['requestedDatabase'] == 'GAMEDBPP13')
    run('backup-select-utf8-game', exe, ['--remember-backup', 'GAMEDBPP13', tool / power_putt['file'], '--root', tool])
    utf8_restore = json.loads(run('backup-restore-utf8-game', exe, ['--build', '--root', tool, '--port', port, '--pg-bin', stock / 'bin', '--only', 'GAMEDBPP13', '--overwrite-confirmed', 'GAMEDBPP13']))
    assert utf8_restore['entries'][0]['status'] == 'Clean installed', utf8_restore
    assert sql('backup-utf8-encoding-preserved', 'GameDBPP13', 'SHOW server_encoding;') == power_putt['encoding'] == 'UTF8'
    assert sql('backup-utf8-score-preserved', 'GameDBPP13', 'SELECT value FROM preserve_me;') == '42'
    catalog.write_bytes(catalog_before)
    # Managed files are ignored even when Auto-Find starts directly at their
    # date folder. Do not exclude the entire folder: original game archives can
    # also live beside these exports and must remain discoverable in place.
    for date_folder in {tool / PurePosixPath(r['file']).parent for r in records}:
        scan = json.loads(run('backup-no-autofind-' + date_folder.name, exe, ['--auto-load-files', date_folder, '--root', tool]))
        assert scan['filesFound'] == 0 and catalog.read_bytes() == catalog_before
    original_archive = latest_path.parent / 'original-postgresql_database-GameDB-backup'
    original_archive.write_bytes(latest_path.read_bytes())
    original_digest = digest(original_archive)
    original_catalog = dict(schemaVersion=1, games=[dict(game='Original archive fixture', database='OriginalArchive', encoding='SQL_ASCII', backup=original_archive.name, sha256=original_digest)])
    catalog.write_text(json.dumps(original_catalog), encoding='utf-8')
    try:
        original_scan = json.loads(run('backup-autofind-preserves-original', exe, ['--auto-load-files', latest_path.parent, '--root', tool]))
        assert original_scan['filesFound'] == 1, original_scan
        original_entry = original_scan['entries'][0]
        assert original_entry['status'] in ('Already available', 'Loaded'), original_scan
        assert (tool / original_entry['destinationFile']).resolve() == original_archive.resolve()
        assert digest(original_archive) == original_digest
    finally:
        catalog.write_bytes(catalog_before)
    definition = json.loads(catalog_before)
    next(g for g in definition['games'] if g['database'] == 'GoodBatch')['databaseFile'] = str(latest_path)
    catalog.write_text(json.dumps(definition), encoding='utf-8')
    linked = backup('backup-preserve-linked-source', action='overwrite')
    assert linked['entries'][0]['status'] == 'Needs review' and 'restore source' in linked['entries'][0]['detail']
    catalog.write_bytes(catalog_before)
    assert sql('backup-source-unchanged', 'GoodBatch', 'SELECT score FROM arcade.progress;') == '3600'
    # Deny new connections only on this owned fixture to force pg_dump failure.
    before_hash = digest(latest_path)
    before_settings = settings.read_bytes()
    sql('backup-disable-fixture', 'postgres', "UPDATE pg_database SET datallowconn=false WHERE datname='GoodBatch';")
    try:
        failed = backup('backup-failed-dump', action='overwrite')
        assert failed['entries'][0]['status'] == 'Needs review', failed
        assert 'No new backup was saved' in failed['entries'][0]['detail']
        assert digest(latest_path) == before_hash and settings.read_bytes() == before_settings
        assert not list((tool / 'pg_backup').rglob('*.partial'))
    finally:
        sql('backup-enable-fixture', 'postgres', "UPDATE pg_database SET datallowconn=true WHERE datname='GoodBatch';")
    # Tampering must block overwrite instead of destroying a changed file.
    content = latest_path.read_bytes()
    latest_path.write_bytes(content + b'changed fixture')
    tampered = backup('backup-changed-file', action='overwrite')
    assert tampered['entries'][0]['status'] == 'Needs review'
    assert latest_path.read_bytes() == content + b'changed fixture'
    assert settings.read_bytes() == before_settings
    latest_path.write_bytes(content)
    # Missing games are skipped; a batch still backs up installed games.
    prior_records = history()
    absent = backup('backup-absent-game', 'GoodBatch,BadBatch', 'keep')
    assert [e['status'] for e in absent['entries']] == ['Not installed', 'Backed up'], absent
    records = history()
    third = records[-1]
    prior_versions_today = sum(r['requestedDatabase'] == 'GoodBatch' and PurePosixPath(r['file']).parent == PurePosixPath(third['file']).parent for r in prior_records)
    assert_layout(third, 'backup-absent-game', prior_versions_today + 1)
    # Existing history stays usable. Overwriting a legacy or previous-day
    # export publishes the requested current layout and only then removes the
    # old file; unrelated archives are retained.
    for database, old_relative, operation in [
        ('GameDB06', 'pg_backup/2000-01-01/GoldenTee2006_000101_DBbackup', 'backup-overwrite-previous-date'),
        ('GAMEDBPP13', 'PG_backup/User Backups/GAMEDBPP13/legacy-fixture.dump', 'backup-overwrite-legacy')
    ]:
        document = json.loads(settings.read_text(encoding='utf-8-sig'))
        prior = next(r for r in document['backups'] if r['requestedDatabase'] == database and r.get('kind') != 'initial')
        existing_path = tool / prior['file']
        old_path = tool / old_relative
        old_path.parent.mkdir(parents=True, exist_ok=True)
        existing_path.rename(old_path)  # This test owns both paths.
        prior['file'] = old_relative
        settings.write_text(json.dumps(document), encoding='utf-8')
        before_count = len(document['backups'])
        before_relocation = settings.read_bytes()
        old_digest = digest(old_path)
        actual_database = prior['database']
        sql(operation + '-deny-dump', 'postgres', f"UPDATE pg_database SET datallowconn=false WHERE datname='{actual_database}';")
        try:
            rejected_relocation = backup(operation + '-failure', database, 'overwrite')
            assert rejected_relocation['entries'][0]['status'] == 'Needs review', rejected_relocation
            assert settings.read_bytes() == before_relocation and digest(old_path) == old_digest
            assert not existing_path.exists() and not list((tool / 'pg_backup').rglob('*.partial'))
        finally:
            sql(operation + '-allow-dump', 'postgres', f"UPDATE pg_database SET datallowconn=true WHERE datname='{actual_database}';")
        legacy_scan = json.loads(run(operation + '-no-autofind', exe, ['--auto-load-files', old_path.parent, '--root', tool]))
        assert legacy_scan['filesFound'] == 0 and catalog.read_bytes() == catalog_before
        relocated = backup(operation, database, 'overwrite')
        assert relocated['entries'][0]['status'] == 'Backed up', relocated
        new_records = history()
        replacement = next(r for r in new_records if r['requestedDatabase'] == database and r.get('kind') != 'initial')
        assert len(new_records) == before_count and replacement['file'] != old_relative
        previous_versions = sum(r['requestedDatabase'] == database and r['file'] != replacement['file'] and PurePosixPath(r['file']).parent == PurePosixPath(replacement['file']).parent for r in new_records)
        assert_layout(replacement, operation, previous_versions + 1)
        assert (tool / replacement['file']).is_file() and not old_path.exists()
        assert digest(original_archive) == original_digest
    records = history()
    # A backup-copy failure happens after database activation and must not turn
    # a successful first install into a failure or alter the supplied archive.
    failure_tool = test_root / 'initial-copy-failure-tool'
    failure_tool.mkdir()
    failure_exe = failure_tool / exe.name
    shutil.copyfile(exe, failure_exe)
    failure_game = next(g.copy() for g in json.loads(catalog_before)['games'] if g['database'] == 'GameDB06')
    failure_game.update(game='Initial copy failure fixture', database='InitialCopyFailure', databaseFile=str(source06_path), sha256=digest(source06_path))
    failure_catalog = failure_tool / 'DatabaseInfo.json'
    failure_catalog.write_text(json.dumps(dict(schemaVersion=1, games=[failure_game])), encoding='utf-8')
    failure_settings = failure_tool / 'UserSettings.json'
    failure_connection = dict(preferences['connection'], port=port, pgBin=str(stock / 'bin'))
    failure_settings.write_text(json.dumps(dict(hideWelcome=True, connection=failure_connection, customPreference={'keep': 'copy failure fixture'})), encoding='utf-8')
    failure_settings_before = failure_settings.read_bytes()
    failure_catalog_before = failure_catalog.read_bytes()
    failure_source_before = digest(source06_path)
    failure_folder = failure_tool / 'pg_backup'
    failure_folder.mkdir()
    blocking_file = failure_folder / datetime.now().date().isoformat()
    blocking_content = b'Owned fixture: this file blocks the dated backup directory.'
    blocking_file.write_bytes(blocking_content)
    copy_failed = json.loads(run('initial-copy-failure-install', failure_exe, ['--build', '--root', failure_tool, '--port', port, '--pg-bin', stock / 'bin', '--only', 'InitialCopyFailure']))
    failure_entry = copy_failed['entries'][0]
    assert copy_failed['completed'] and failure_entry['status'] == 'Imported', copy_failed
    assert failure_entry['databaseInstalled'] and failure_entry['settingsVerified'] and failure_entry['sourceUnchanged']
    assert 'initial backup could not be completed' in failure_entry['detail'].lower(), copy_failed
    assert sql('initial-copy-failure-activated-data', 'InitialCopyFailure', 'SELECT count(*) FROM category2_attr;') == game06_rows
    assert sql('initial-copy-failure-activated-keys', 'InitialCopyFailure', "SELECT count(*) FROM pg_constraint WHERE contype='f' AND conname IN ('category1_attr_fk','category2_attr_fk','category3_attr_fk','category4_fk','category5_fk');") == '5'
    assert digest(source06_path) == failure_source_before
    assert failure_settings.read_bytes() == failure_settings_before and failure_catalog.read_bytes() == failure_catalog_before
    assert blocking_file.read_bytes() == blocking_content and not list(failure_tool.rglob('*.partial'))
    results['checks'].append('first-install backup copy failure after activation retains Imported success, complete migrated database, unchanged original archive/settings/catalog/blocking file, and leaves no partial backup')
    saved_settings = settings.read_bytes()
    invalid = json.loads(saved_settings)
    invalid['backups'][0]['file'] = '../outside.dump'
    settings.write_text(json.dumps(invalid), encoding='utf-8')
    invalid_bytes = settings.read_bytes()
    rejected = subprocess.run([str(exe), *map(str, base + ['--only', 'GoodBatch', '--backup-existing', 'overwrite'])], env=env, capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
    assert rejected.returncode != 0 and settings.read_bytes() == invalid_bytes
    settings.write_bytes(saved_settings)
    assert catalog.read_bytes() == catalog_before
    assert digest(source06_path) == source06_digest
    assert env['PGPASSWORD'].encode() not in json.dumps(first).encode()
    results['checks'].append('v1.2.0 backups: first-install original copied byte-for-byte with kind initial and adopted as persistent databaseFile/SHA; fresh inventory resolves the selected copy while reports retain the original restore source; Already present, Clean install and remove/reinstall retain one initial archive; initial legacy restore still migrates five category foreign keys; compact game/date filenames with no extension and numbered versions; previous-date and legacy overwrite; date-root discovery excludes managed exports while preserving original archives; custom archives round-trip data, sequence and other schemas; encoding and SHA-256 recorded; default asks even after initial snapshot; atomic overwrite; failed pg_dump and changed files preserve prior backups; batch skips absent games; invalid history paths rejected; explicit backup leaves catalogue unchanged; credentials and unrelated preferences preserved')
