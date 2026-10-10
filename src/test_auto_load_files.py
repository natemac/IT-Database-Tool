"""Exercise folder discovery and read-only file linking. No PostgreSQL connection or restore."""
import hashlib
import io
import json
from pathlib import Path
import secrets
import subprocess
import tarfile
import msvcrt

ROOT = Path(__file__).resolve().parents[1]
EXE = ROOT / 'app/IT Database Builder.exe'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def game(name, database, filename, sha=None):
    return dict(game=name, database=database, encoding='UTF8',
                backup=filename, backupFolder=name, sha256=sha)


def put(root, name, data):
    path = root / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def run(tool, folder):
    result = subprocess.run([str(EXE), '--root', str(tool), '--auto-load-files', str(folder)],
                            capture_output=True, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
    return json.loads(result.stdout.decode('utf-8-sig'))


def main():
    fixture = ROOT / 'tmp' / ('database-files-' + secrets.token_hex(6))
    tool, sources = fixture / 'tool', fixture / 'Games' / 'Teknoparrot'
    tool.mkdir(parents=True)
    originals = {}

    def custom(name, label):
        data = b'PGDMP' + label.encode() + b'\0' * 600
        path = put(sources, name, data)
        originals[path] = digest(data)
        return data

    sixteen = custom('golden_tee_2016/GameData/deep/1942-postgresql_database-GameDB-backup', '2016')
    eighteen = custom('Golden Tee Live (2018)/database/1942-postgresql_database-GameDB-backup.dump', '2018')
    custom('Golden Tee Live (2018)/duplicate/1942-postgresql_database-GameDB-backup', '2018')
    known = custom('misc/nested/renamed-original.bin', 'known-original')
    pp = custom('POWER_PUTT_2012/data/renamed-file', 'power-putt')
    ssb = custom('SSB Live/data/any-file', 'silver-strike')
    occ = custom('OCC Pinball/data/db.file', 'choppers')
    bags = custom('Target Toss Bags/data/db', 'bags')
    darts = custom('Target Toss Lawn Darts and Bags/data/db', 'darts')
    future = custom('Future Arcade 2031/data/db', 'future')
    custom('Golden Tee 2019/a/db', 'version-one')
    custom('Golden Tee 2019/b/db', 'version-two')
    custom('Golden Tee 2020/db', 'wrong-fingerprint')
    custom('Golden Tee Live 20180/db', 'wrong-year')
    custom('Unrelated/db', 'unrelated')
    custom('loose/shared-file', 'shared-and-unclear')
    dated = custom('loose/2026-10-08/shared-dated', 'unique-date')
    tar_buffer = io.BytesIO()
    with tarfile.open(fileobj=tar_buffer, mode='w', format=tarfile.USTAR_FORMAT) as archive:
        data = b'PGDMPtar-fixture'
        info = tarfile.TarInfo('toc.dat'); info.size = len(data)
        archive.addfile(info, io.BytesIO(data))
    tar_data = tar_buffer.getvalue()
    path = put(sources, 'Tar Arcade/db.tar', tar_data); originals[path] = digest(tar_data)
    unrelated_tar = io.BytesIO()
    with tarfile.open(fileobj=unrelated_tar, mode='w') as archive:
        info = tarfile.TarInfo('asset.txt'); info.size = 5
        archive.addfile(info, io.BytesIO(b'asset'))
    path = put(sources, 'Tar Arcade/asset.tar', unrelated_tar.getvalue())
    originals[path] = digest(path.read_bytes())
    put(sources, 'Golden Tee 2016/not-a-database.txt', b'ordinary text')

    games = [
        game('Golden Tee Live 2016', 'GameDB16', '2018-01-07/1942-postgresql_database-GameDB-backup'),
        game('Golden Tee Live 2018', 'GameDB18', '2018-06-21/1942-postgresql_database-GameDB-backup'),
        game('Golden Tee Live 2017', 'GameDB17', '2018-01-30/original', digest(known)),
        game('Power Putt Live 2012', 'GameDBPP12', 'original-pp'),
        game('Silver Strike Bowling Live', 'GameDBSSB', 'original-ssb'),
        game('Orange County Choppers Pinball', 'GameDBOCC', 'original-occ'),
        game('Target Toss Pro Bags', 'GameDBBags', 'original-bags'),
        game('Target Toss Pro Lawn Darts & Bags', 'GameDBDarts', 'original-darts'),
        game('Future Arcade 2031', 'GameDBFuture', '2031-01-01/original-future'),
        game('Golden Tee Live 2019', 'GameDB19', 'original-19'),
        game('Golden Tee Live 2020', 'GameDB20', 'original-20', digest(b'expected-original')),
        game('Year Guard', 'GameDBMissing', 'missing-file'),
        game('Unclear A', 'GameDBUnclearA', 'shared-file'),
        game('Unclear B', 'GameDBUnclearB', 'shared-file'),
        game('Date A', 'GameDBDateA', '2026-10-08/shared-dated'),
        game('Date B', 'GameDBDateB', '2026-10-09/shared-dated'),
        game('Tar Arcade', 'GameDBTar', 'db.tar'),
    ]
    # Intentionally unlistened port; discovery must never depend on a server.
    definition = dict(schemaVersion=1, connection=dict(server='127.0.0.1', port=15584,
                      username='postgres', password='generic-fixture-password', pgBin=None), games=games)
    (tool / 'DatabaseInfo.json').write_text(json.dumps(definition), encoding='utf-8')
    first = run(tool, sources)
    states = {entry['database']: entry for entry in first['entries']}
    expected = {'GameDB16': sixteen, 'GameDB18': eighteen, 'GameDB17': known,
                'GameDBPP12': pp, 'GameDBSSB': ssb, 'GameDBOCC': occ,
                'GameDBBags': bags, 'GameDBDarts': darts, 'GameDBFuture': future,
                'GameDBDateA': dated, 'GameDBTar': tar_data}
    updated = json.loads((tool / 'DatabaseInfo.json').read_text(encoding='utf-8-sig'))
    for item in updated['games']:
        database = item['database']
        if database in expected:
            assert states[database]['status'] == 'Loaded', states[database]
            target = Path(item['databaseFile'])
            assert str(target) == states[database]['sourceFile'] == states[database]['destinationFile']
            assert target.is_relative_to(sources)
            assert target.read_bytes() == expected[database]
            assert item['sha256'] == digest(expected[database])
        else:
            assert states[database]['status'] in ('Not found', 'Choose file'), states[database]
    for database in ('GameDB19', 'GameDB20', 'GameDBUnclearA', 'GameDBUnclearB'):
        assert states[database]['status'] == 'Choose file'
    assert 'connection' not in updated, 'Database catalogue must not retain local connection settings'
    settings_path = tool / 'UserSettings.json'
    settings = json.loads(settings_path.read_text(encoding='utf-8-sig'))
    assert settings['connection'] == definition['connection']
    settings_before = settings_path.read_bytes()
    assert not first['error'] and not first['canceled']
    assert Path(first['reportPath']).is_file()
    assert not list(tool.rglob('*.loading'))
    for path, sha in originals.items():
        assert digest(path.read_bytes()) == sha
    second = run(tool, sources)
    assert settings_path.read_bytes() == settings_before, 'File discovery must preserve user settings'
    assert sum(entry['status'] == 'Already available' for entry in second['entries']) == len(expected)
    assert not any(entry['status'] == 'Loaded' for entry in second['entries'])
    assert not (tool/'PG_backup').exists(), 'Discovery must not copy files or create folders'
    (tool/'PG_backup').mkdir()  # Test-owned fixtures below.
    # Local scanning must ignore unrelated tar files and our unfinished copies,
    # and one locked archive must not prevent the other games from being listed.
    (tool/'PG_backup/asset.tar').write_bytes(unrelated_tar.getvalue())
    incomplete=tool/'PG_backup'/('incoming.'+'0'*32+'.loading')
    incomplete.write_bytes(b'PGDMPunfinished')
    updated['games']+=[game('Unrelated tar','GameDBAsset','asset.tar'),game('Unfinished copy','GameDBIncomplete',incomplete.name)]
    (tool/'DatabaseInfo.json').write_text(json.dumps(updated),encoding='utf-8')
    inventory_args=[str(EXE),'--root',str(tool),'--inventory']
    normal=json.loads(subprocess.run(inventory_args,capture_output=True,check=True,creationflags=subprocess.CREATE_NO_WINDOW).stdout.decode('utf-8-sig'))
    assert sum(entry['backup'] is not None for entry in normal)==len(expected)
    definition_before=(tool/'DatabaseInfo.json').read_bytes()
    invalid_tar=subprocess.run([str(EXE),'--root',str(tool),'--remember-backup','GameDBTar',str(tool/'PG_backup/asset.tar')],capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
    assert invalid_tar.returncode!=0 and (tool/'DatabaseInfo.json').read_bytes()==definition_before
    locked=Path(states['GameDB16']['destinationFile'])
    temporarily_missing=locked.with_name(locked.name+'.test-missing')
    locked.rename(temporarily_missing)  # Test-owned original; simulate a moved file.
    try:
        missing=json.loads(subprocess.run(inventory_args,capture_output=True,check=True,creationflags=subprocess.CREATE_NO_WINDOW).stdout.decode('utf-8-sig'))
        assert next(entry for entry in missing if entry['database']=='GameDB16')['backup'] is None
    finally:
        temporarily_missing.rename(locked)
    assert not (tool/'PG_backup'/(updated['games'][0]['sha256']+'.backup')).exists()
    with locked.open('r+b') as stream:
        msvcrt.locking(stream.fileno(),msvcrt.LK_NBLCK,517)
        try:
            partial=json.loads(subprocess.run(inventory_args,capture_output=True,check=True,creationflags=subprocess.CREATE_NO_WINDOW).stdout.decode('utf-8-sig'))
            assert sum(entry['backup'] is not None for entry in partial)>=len(expected)-1
        finally:
            stream.seek(0);msvcrt.locking(stream.fileno(),msvcrt.LK_UNLCK,517)

    # A folder year must be a complete year, never the prefix of 20180.
    guard = fixture / 'year-guard'; guard.mkdir()
    definition['games'] = [game('Golden Tee Live 2018', 'GameDB18', 'original-guard')]
    (guard / 'DatabaseInfo.json').write_text(json.dumps(definition), encoding='utf-8')
    year_sources = fixture / 'year-sources'
    put(year_sources, 'Golden Tee Live 20180/db', b'PGDMPyear-guard')
    assert run(guard, year_sources)['entries'][0]['status'] == 'Not found'

    # Real release-pack names: title years must win over build/release dates.
    # Use no fingerprints so folder ownership must work on its own. Every folder
    # contains an older file too; the expected filename must select the original.
    metadata_tool = fixture / 'metadata-tool'; metadata_tool.mkdir()
    metadata_sources = fixture / 'metadata-sources'
    releases = {2006: ('1.00.48', '2007-12-19'), 2007: ('2.00.20', '2008-02-11'),
                2008: ('3.00.12', '2010-03-29'), 2009: ('4.00.07', '2011-11-14'),
                2010: ('5.00.10', '2011-11-14'), 2011: ('6.00.09', '2011-11-14'),
                2012: ('7.00.10', '2012-04-06'), 2013: ('8.05.09', '2014-11-05'),
                2014: ('9.04.06', '2014-02-05'), 2015: ('10.04.06', '2016-04-05'),
                2016: ('11.04.10', '2016-08-05'), 2017: ('12.04.05', '2017-07-19'),
                2018: ('13.04.24', '2018-04-05'), 2019: ('14.04.13', '2019-08-01'),
                2020: ('15.04.08', '2020-06-15')}
    metadata_games = []
    expected_metadata = {}
    for year, (version, date) in releases.items():
        name = f'Golden Tee Live {year}'
        folder = f'{name} ({version})({date})(USA)[IT Nighthawk][TP]'
        # 2016 / 2018 deliberately share a basename, as in the real catalog.
        filename = '1942-postgresql_database-GameDB-backup' if year in (2016, 2018) else f'original-{year}'
        item = game(name, f'GameDB{year % 100:02}', f'2018-01-01/{filename}')
        metadata_games.append(item)
        original = b'PGDMP' + name.encode() + b'\0' * 600
        put(metadata_sources, f'{folder}/PG_backup/deep/{filename}', original)
        put(metadata_sources, f'{folder}/PG_backup/older-file', b'PGDMPolder' + name.encode())
        expected_metadata[item['database']] = original
    for name, database, date in [('Power Putt Live 2012', 'GameDBPP12', '2013-03-14'),
                                 ('Power Putt Live 2013', 'GameDBPP13', '2015-02-05'),
                                 ('Silver Strike Bowling Live', 'GameDBSSB', '2017-05-10'),
                                 ('Orange County Choppers Pinball', 'GameDBOCC', '2005-08-24'),
                                 ('Target Toss Pro Bags', 'GameDBBags', '2007-03-27'),
                                 ('Target Toss Pro Lawn Darts & Bags', 'GameDBDarts', '2010-04-05')]:
        item = game(name, database, f'original-{database}')
        metadata_games.append(item)
        folder = f'{name} (1.00.10)({date})(USA)[IT Nighthawk][TP]'
        original = b'PGDMP' + name.encode() + b'\0' * 600
        put(metadata_sources, f'{folder}/PG_backup/deep/{item["backup"]}', original)
        put(metadata_sources, f'{folder}/PG_backup/older-file', b'PGDMPolder' + name.encode())
        expected_metadata[database] = original
    definition['games'] = metadata_games
    (metadata_tool / 'DatabaseInfo.json').write_text(json.dumps(definition), encoding='utf-8')
    metadata_result = run(metadata_tool, metadata_sources)
    assert len(metadata_result['entries']) == 21
    for entry in metadata_result['entries']:
        assert entry['status'] == 'Loaded', entry
        assert entry['sourceFile'] == entry['destinationFile']
        assert Path(entry['destinationFile']).read_bytes() == expected_metadata[entry['database']]

    # A release date alone must never identify a different year's renamed file.
    for index, folder in enumerate(['Golden Tee Live 2006 (1.00.48)(2007-12-19)(USA)[IT Nighthawk][TP]',
                                    'Golden Tee Live 2006 2007-12-19',
                                    'Golden Tee Live (1.00.48)(2007-12-19)(USA)[IT Nighthawk][TP]']):
        mismatch_tool = fixture / f'metadata-mismatch-tool-{index}'; mismatch_tool.mkdir()
        definition['games'] = [game('Golden Tee Live 2007', 'GameDB07', 'expected-original')]
        (mismatch_tool / 'DatabaseInfo.json').write_text(json.dumps(definition), encoding='utf-8')
        mismatch_sources = fixture / f'metadata-mismatch-sources-{index}'
        put(mismatch_sources, f'{folder}/data/renamed-file', b'PGDMPwrong-game')
        assert run(mismatch_tool, mismatch_sources)['entries'][0]['status'] == 'Not found'

    # Preserve common title-year shorthand with appended release metadata.
    for index, folder in enumerate(['Golden_Tee_[2018] (13.04.24)(2019-04-05)[TP]',
                                    'Golden Tee Live (2018) (13.04.24)(2019-04-05)[TP]',
                                    'Golden_Tee_2018 2019-04-05']):
        shorthand_tool = fixture / f'shorthand-tool-{index}'; shorthand_tool.mkdir()
        definition['games'] = [game('Golden Tee Live 2018', 'GameDB18', 'expected-original')]
        (shorthand_tool / 'DatabaseInfo.json').write_text(json.dumps(definition), encoding='utf-8')
        shorthand_sources = fixture / f'shorthand-sources-{index}'
        put(shorthand_sources, f'{folder}/data/renamed-file', b'PGDMPcorrect-game')
        assert run(shorthand_tool, shorthand_sources)['entries'][0]['status'] == 'Loaded'
    print(json.dumps({'passed': True, 'files_loaded': len(expected), 'database_connections': 0,
                     'checks': ['recursive discovery, variable game names and complete years',
                                'all 21 release-pack folder names, without recorded fingerprints',
                                'title year separated from build dates; wrong-year renamed files rejected',
                                'parenthesized and bracketed title years; loose release dates',
                                'shared filenames and unique date folders', 'known fingerprints and renamed files',
                                'identical duplicates accepted; differing versions require choice',
                                'custom and PostgreSQL tar files; unrelated tar excluded',
                                'future JSON game without rebuilding', 'source files and generic password preserved',
                                'repeat search skips available files',
                                'original paths saved with no copies; missing original is unavailable',
                                'local inventory excludes unrelated tar files and unfinished copies',
                                'a locked archive does not block other games; rejected files leave JSON unchanged']}, indent=2))


if __name__ == '__main__':
    main()
