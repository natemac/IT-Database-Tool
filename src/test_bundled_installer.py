"""Check bundled installer discovery and packaging without launching any installer."""
import hashlib
import json
from pathlib import Path
import secrets
import subprocess
import zipfile

ROOT=Path(__file__).resolve().parents[1]
TOOL=ROOT/'app'
EXE=TOOL/'IT Database Builder.exe'
FILES=('SETUP.bat','postgresql-8.3.msi','postgresql-8.3-int.msi','vcredist_x86.exe')


def check(folder,pg_bin=None):
    args=[str(EXE),'--installer-check','--root',str(folder)]
    if pg_bin is not None:
        args+=['--pg-bin',str(pg_bin)]
    result=subprocess.run(args,capture_output=True,check=True,creationflags=subprocess.CREATE_NO_WINDOW)
    return json.loads(result.stdout.decode('utf-8-sig'))


def main():
    source=json.loads((TOOL/'DatabaseInfo.json').read_text(encoding='utf-8-sig'))
    actual=check(TOOL)
    assert actual['filesPresent'] and not actual['missingFiles']
    assert actual['startFile']==str(TOOL/'installs/PostgreSQL v8.3/SETUP.bat')
    assert actual['workingDirectory']==str(TOOL/'installs/PostgreSQL v8.3')
    assert actual['elevation']=='runas' and actual['windowStyle']=='Hidden'
    fixture=ROOT/'tmp'/('installer-check-'+secrets.token_hex(6)+' [literal]')
    fixture.mkdir()
    # Exercise the actual setup controls in memory; no UI/input automation or
    # installer/service actions. Copy the assembly beside the test executable.
    import os
    import shutil
    shutil.copyfile(EXE,fixture/EXE.name)
    password_fixture=fixture/'password-settings'
    password_fixture.mkdir()
    password_definition=json.loads(json.dumps(source))
    password_definition.pop('connection',None)
    (password_fixture/'DatabaseInfo.json').write_text(json.dumps(password_definition),encoding='utf-8')
    compiler=Path(os.environ['WINDIR'])/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    probe=fixture/'SetupGuidanceTest.exe'
    subprocess.run([str(compiler),'/nologo','/target:exe','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Web.Extensions.dll',
                    '/reference:'+str(EXE),'/out:'+str(probe),str(ROOT/'src/test_setup_guidance.cs')],check=True,capture_output=True)
    guidance=subprocess.run([str(probe),str(fixture)],check=True,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
    print(guidance.stdout.decode('utf-8-sig').strip())
    history_probe=fixture/'BackupHistoryTest.exe'
    subprocess.run([str(compiler),'/nologo','/target:exe','/reference:System.Web.Extensions.dll',
                    '/reference:'+str(EXE),'/out:'+str(history_probe),str(ROOT/'src/test_backup_history.cs')],check=True,capture_output=True)
    history=subprocess.run([str(history_probe),str(fixture)],check=True,capture_output=True,creationflags=subprocess.CREATE_NO_WINDOW)
    print(history.stdout.decode('utf-8-sig').strip())
    source.pop('connection',None)
    (fixture/'DatabaseInfo.json').write_text(json.dumps(source),encoding='utf-8')
    missing=check(fixture,fixture/'missing-postgresql-tools')
    assert not missing['filesPresent'] and set(missing['missingFiles'])==set(FILES)
    assert not missing['toolsPresent']
    bundle=fixture/'installs/PostgreSQL v8.3'
    bundle.mkdir(parents=True)
    for name in FILES:
        (bundle/name).write_bytes(b'non-executable fixture; never launched')
    found=check(fixture,fixture/'missing-postgresql-tools')
    assert found['filesPresent'] and not found['toolsPresent']
    assert found['workingDirectory']==str(bundle) and found['startFile']==str(bundle/'SETUP.bat')
    (bundle/'postgresql-8.3-int.msi').write_bytes(b'')
    incomplete=check(fixture,fixture/'missing-postgresql-tools')
    assert not incomplete['filesPresent'] and incomplete['missingFiles']==['postgresql-8.3-int.msi']
    with zipfile.ZipFile(ROOT/'output/IT-Database-Setup.zip') as archive:
        assert 'UserSettings.json' not in archive.namelist()
        assert 'InstallHistory.json' not in archive.namelist()
        assert [name for name in archive.namelist() if name.replace('\\','/').lower().startswith('pg_backup/')] == ['PG_backup/'], 'Shared ZIP must contain an empty backup folder only'
        assert not any(name.lower().endswith(('.dump','.partial','backup-report.json')) for name in archive.namelist()), 'Personal backups and reports must stay private'
        for name in FILES:
            packed=archive.read('installs/PostgreSQL v8.3/'+name)
            assert hashlib.sha256(packed).digest()==hashlib.sha256((TOOL/'installs/PostgreSQL v8.3'/name).read_bytes()).digest()
        definition=json.loads(archive.read('DatabaseInfo.json').decode('utf-8-sig'))
        original=json.loads((TOOL/'DatabaseInfo.json').read_text(encoding='utf-8-sig'))
        assert 'connection' not in definition, 'Shared catalogue must not contain local connection settings'
        expected_games=[dict(game, databaseFile=None, **({'sha256':None} if game.get('databaseFile') else {})) for game in original['games']]
        assert definition['games']==expected_games
        readme=archive.read('README.txt').decode('utf-8-sig')
        credits=archive.read('CREDITS.txt').decode('utf-8-sig')
        assert 'Info courtesy of Eggmansworld' in credits and 'https://github.com/Eggmansworld/tp-it-guides' in credits
        assert credits==(TOOL/'CREDITS.txt').read_text(encoding='utf-8-sig')
        assert len(readme.splitlines())<=80
        assert readme==(TOOL/'README.txt').read_text(encoding='utf-8-sig')
        assert hashlib.sha256(archive.read('IT Database Builder.exe')).digest()==hashlib.sha256(EXE.read_bytes()).digest()
    print(json.dumps({'passed':True,'installer_launched':False,'checks':['complete, missing and incomplete installer bundles detected','literal paths, elevated launch and hidden helper configured','all four installer files included unchanged in ZIP','shared ZIP omits UserSettings and catalogue connection; fresh settings default to a blank password','legacy settings migration, user precedence, independent catalogue updates and malformed-file preservation','README has '+str(len(readme.splitlines()))+' lines','guide author and GitHub credits included in ZIP','ZIP contains current compiled app']},indent=2))


if __name__=='__main__':
    main()
