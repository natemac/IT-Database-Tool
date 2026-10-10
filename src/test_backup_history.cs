using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ITDatabaseSetup;

class BackupHistoryTest
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Write(string path,object document){File.WriteAllText(path,Engine.PrettyJson(document),new UTF8Encoding(false));}
    static DatabaseBackup Record(string database,string filename,string created,int port=15583,string server="127.0.0.1")
    {
        return new DatabaseBackup {game="Fixture arcade",requestedDatabase=database,database=database,databaseOid="12345",
            server=server,port=port,encoding="SQL_ASCII",createdUtc=created,
            file="PG_backup/User Backups/"+database+"/"+filename+".dump",sha256=new string('a',64),bytes=1};
    }
    static string Digest(string path)
    {
        using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant();
    }
    sealed class AdoptionFixture
    {
        public string root,catalogPath,settingsPath,sourcePath,copyPath;
        public Engine engine;
        public Game original;
        public Entry entry;
        public DatabaseBackup record;
        public byte[] settingsBefore,sourceBefore;
        public AdoptionFixture(string parent,string name)
        {
            root=Path.Combine(parent,"initial-adoption-"+name);Directory.CreateDirectory(root);
            catalogPath=Path.Combine(root,"DatabaseInfo.json");settingsPath=Path.Combine(root,"UserSettings.json");
            sourcePath=Path.Combine(root,"original.backup");
            byte[] archive=new byte[640];Encoding.ASCII.GetBytes("PGDMP owned synthetic PostgreSQL archive").CopyTo(archive,0);
            File.WriteAllBytes(sourcePath,archive);sourceBefore=archive;
            string fingerprint=Digest(sourcePath);
            original=new Game {game="Golden Tee Live 2020",database="FixtureDB",encoding="SQL_ASCII",backup="original.backup",backupFolder="Original folder",databaseFile=sourcePath,sha256=fingerprint,restoreProfile="it-category-keys-v1"};
            Write(catalogPath,new {schemaVersion=1,games=new[]{original,new Game {game="Unrelated arcade",database="UnrelatedDB",encoding="UTF8",backup="other.backup",backupFolder="Other folder"}}});
            record=Record("FixtureDB","unused","2026-10-09T12:00:00Z");record.kind="initial";record.game=original.game;
            record.file="pg_backup/2026-10-09/GoldenTee2020_261009_DBbackup";record.sha256=fingerprint;record.bytes=archive.Length;
            copyPath=Path.Combine(root,record.file.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(copyPath));File.WriteAllBytes(copyPath,archive);
            Write(settingsPath,new {hideWelcome=true,connection=new {server="localhost",port=15583,username="fixture_user",password="fixture_password",pgBin=@"C:\unused tools"},unrelatedPreference="keep settings",backups=new[]{record}});
            settingsBefore=File.ReadAllBytes(settingsPath);
            engine=new Engine(root);original=engine.catalog.Single(item=>item.database=="FixtureDB");
            entry=new Entry {game=original.game,requestedDatabase=original.database,database=original.database,backup=sourcePath,backupSHA256=fingerprint,status="Imported",databaseInstalled=true,sourceUnchanged=true};
        }
        public DatabaseDefinition ReadCatalog(){return Engine.Json.Deserialize<DatabaseDefinition>(File.ReadAllText(catalogPath));}
        public Game EditCatalog(Action<Game> edit)
        {
            var definition=ReadCatalog();Game edited=definition.games.Single(item=>item.database=="FixtureDB");edit(edited);Write(catalogPath,new {schemaVersion=1,games=definition.games});return edited;
        }
        public void Adopt()
        {
            MethodInfo method=typeof(Engine).GetMethod("AdoptInitialBackup",BindingFlags.Instance|BindingFlags.NonPublic);
            Require(method!=null,"Initial backup source adoption helper is missing");
            try{method.Invoke(engine,new object[]{original,entry,record});}
            catch(TargetInvocationException ex){throw ex.InnerException??ex;}
        }
        public void RequireUnchangedOutsideCatalog()
        {
            Require(File.ReadAllBytes(settingsPath).SequenceEqual(settingsBefore),"Adopting an initial source must preserve settings/history");
            Require(File.ReadAllBytes(sourcePath).SequenceEqual(sourceBefore),"Adopting a copy must preserve the original archive");
            Require(entry.backup==sourcePath,"The install report must retain the actual archive used for restore");
        }
        public void RequireRejectedWithoutWriting(string message)
        {
            byte[] before=File.ReadAllBytes(catalogPath);bool rejected=false;
            try{Adopt();}catch(Exception){rejected=true;}
            Require(rejected,message);Require(File.ReadAllBytes(catalogPath).SequenceEqual(before),"A rejected source adoption rewrote the catalog");RequireUnchangedOutsideCatalog();
        }
    }
    static void VerifyInitialSourceAdoption(string parent)
    {
        var unchanged=new AdoptionFixture(parent,"unchanged");unchanged.Adopt();
        Game adopted=unchanged.ReadCatalog().games.Single(item=>item.database=="FixtureDB");
        Require(adopted.databaseFile==unchanged.copyPath && adopted.sha256==unchanged.record.sha256,"An unchanged original choice must adopt the copied file and its SHA-256");
        Require(unchanged.engine.Inventory().Single(item=>item.requestedDatabase=="FixtureDB").backup==unchanged.copyPath,"The refreshed file column must resolve to the managed initial copy");
        unchanged.RequireUnchangedOutsideCatalog();byte[] adoptedCatalog=File.ReadAllBytes(unchanged.catalogPath);unchanged.Adopt();
        Require(File.ReadAllBytes(unchanged.catalogPath).SequenceEqual(adoptedCatalog),"Adopting an already selected initial copy must be a no-op");

        var titleEdit=new AdoptionFixture(parent,"unrelated-edit");titleEdit.EditCatalog(game=>{game.game="Renamed arcade title";game.encoding="UTF8";});
        var editedDefinition=titleEdit.ReadCatalog();editedDefinition.games.Single(item=>item.database=="UnrelatedDB").game="Other title edited externally";
        Write(titleEdit.catalogPath,new {schemaVersion=1,games=editedDefinition.games});titleEdit.Adopt();
        adopted=titleEdit.ReadCatalog().games.Single(item=>item.database=="FixtureDB");
        Require(adopted.game=="Renamed arcade title" && adopted.encoding=="UTF8" && adopted.databaseFile==titleEdit.copyPath,"Source adoption must preserve unrelated edits to the same game");
        Require(titleEdit.ReadCatalog().games.Single(item=>item.database=="UnrelatedDB").game=="Other title edited externally","Source adoption must preserve edits to another game");titleEdit.RequireUnchangedOutsideCatalog();

        var pathEdit=new AdoptionFixture(parent,"path-edited");string alternate=Path.Combine(pathEdit.root,"alternate.backup");File.WriteAllBytes(alternate,pathEdit.sourceBefore);
        pathEdit.EditCatalog(game=>game.databaseFile=alternate);pathEdit.RequireRejectedWithoutWriting("An externally changed source path must not be replaced");
        var hashEdit=new AdoptionFixture(parent,"sha-edited");hashEdit.EditCatalog(game=>game.sha256=new string('b',64));hashEdit.RequireRejectedWithoutWriting("An externally changed source fingerprint must not be replaced");
        var missing=new AdoptionFixture(parent,"missing-copy");File.Delete(missing.copyPath);missing.RequireRejectedWithoutWriting("A missing copied archive must not become the selected source");
        var malformed=new AdoptionFixture(parent,"malformed-copy");File.WriteAllText(malformed.copyPath,"ordinary text without a PostgreSQL archive header");malformed.record.sha256=Digest(malformed.copyPath);malformed.record.bytes=new FileInfo(malformed.copyPath).Length;
        malformed.RequireRejectedWithoutWriting("An invalid copied archive must be rejected even when its fingerprint matches");
        var corrupt=new AdoptionFixture(parent,"changed-copy");File.AppendAllText(corrupt.copyPath,"changed after verification");corrupt.RequireRejectedWithoutWriting("A copied archive whose fingerprint changed must not be adopted");
    }
    static void Main(string[] args)
    {
        var localDate=new DateTime(2026,10,9,23,45,0,DateTimeKind.Local);
        var goldenTee=new Game {game="Golden Tee Live 2020",database="GameDB20"};
        Require(Engine.BackupRelativePath(goldenTee,localDate)=="pg_backup/2026-10-09/GoldenTee2020_261009_DBbackup","Backup naming must use the compact game title and local calendar date, without an extension");
        Require(Engine.BackupRelativePath(goldenTee,localDate,2)=="pg_backup/2026-10-09/GoldenTee2020_261009_2_DBbackup","Second version must add _2 before DBbackup");
        Require(Engine.BackupRelativePath(goldenTee,localDate,3)=="pg_backup/2026-10-09/GoldenTee2020_261009_3_DBbackup","Third version must add _3 before DBbackup");
        Require(Engine.BackupRelativePath(new Game {game="Target Toss Pro Lawn Darts & Bags",database="GameDBDarts"},localDate)=="pg_backup/2026-10-09/TargetTossProLawnDartsAndBags_261009_DBbackup","Other game titles must produce readable filenames");
        Require(Engine.BackupRelativePath(new Game {game="?!",database="FixtureDB"},localDate)=="pg_backup/2026-10-09/FixtureDB_261009_DBbackup","An empty compact title must fall back to the database name");
        string root=Path.Combine(Path.GetFullPath(args[0]),"backup-history");Directory.CreateDirectory(root);
        Write(Path.Combine(root,"DatabaseInfo.json"),new {schemaVersion=1,games=new[]{new Game {
            game="Fixture arcade",database="FixtureDB",encoding="SQL_ASCII",backup="original.backup"}}});
        string settingsPath=Path.Combine(root,"UserSettings.json");
        var connection=new Connection {server="localhost",port=15583};
        var old=Record("FixtureDB","old","2026-10-08T12:00:00.0000000+00:00");
        var newest=Record("FixtureDB","newest","2026-10-09T12:00:00Z");
        var tie=Record("FixtureDB","same-instant-later-record","2026-10-09T14:00:00+02:00");
        newest.kind="export";
        tie.kind="initial";
        newest.file="pg_backup/2026-10-09/GoldenTee2020_261009_DBbackup";
        tie.file="pg_backup/2026-10-09/GoldenTee2020_261009_2_DBbackup";
        var wrongPort=Record("FixtureDB","other-port","2026-10-10T12:00:00Z",15584);
        var wrongDatabase=Record("OtherDB","other-database","2026-10-10T12:00:00Z");
        var wrongServer=Record("FixtureDB","other-server","2026-10-10T12:00:00Z",15583,"fixture.example");
        var records=new[]{newest,wrongPort,old,wrongDatabase,wrongServer,tie};
        var settings=new Dictionary<string,object> {{"hideWelcome",true},{"unrelatedPreference","keep this"},
            {"connection",new {server="localhost",port=15583,username="fixture_user",password="fixture_password",pgBin=@"C:\unused tools"}},
            {"backups",records}};
        Write(settingsPath,settings);
        byte[] originalSettings=File.ReadAllBytes(settingsPath);
        var engine=new Engine(root);
        var history=engine.BackupHistory(connection,"FixtureDB");
        Require(history.Length==3,"History leaked another server, port, or game");
        Require(history.Any(item=>item.kind==null) && history.Any(item=>item.kind=="initial") && history.Any(item=>item.kind=="export"),"Initial, generated export and omitted legacy kinds must share backup history");
        Require(engine.BackupRecordPath(old)==Path.Combine(root,"PG_backup","User Backups","FixtureDB","old.dump"),"Existing v1.2.0 backup records must remain readable in their original location");
        Require(engine.BackupRecordPath(newest)==Path.Combine(root,"pg_backup","2026-10-09","GoldenTee2020_261009_DBbackup"),"New backup records must resolve the date-folder layout");
        Require(history[0].file==tie.file && history[1].file==newest.file && history[2].file==old.file,"History must sort actual dates; ties use newest insertion");
        string expected=new DateTimeOffset(2026,10,9,12,0,0,TimeSpan.Zero).ToLocalTime().ToString("MMM d, yyyy",CultureInfo.CurrentCulture);
        Require(engine.BackupStatus(connection,"FixtureDB")==expected,"Missing files must retain their recorded snapshot date");
        Require(engine.BackupStatus(connection,"NoDB")=="Never","Unrecorded game must show Never");
        Require(engine.LatestBackup(connection,"FixtureDB")==null,"Latest usable backup cannot refer to missing files");
        Directory.CreateDirectory(Path.GetDirectoryName(engine.BackupRecordPath(old)));
        File.WriteAllText(engine.BackupRecordPath(old),"owned test fixture");
        Require(engine.LatestBackup(connection,"FixtureDB").file==old.file,"Latest usable backup must fall back past missing files");
        Directory.CreateDirectory(Path.GetDirectoryName(engine.BackupRecordPath(newest)));
        File.WriteAllText(engine.BackupRecordPath(newest),"owned test fixture");
        Require(engine.LatestBackup(connection,"FixtureDB").file==newest.file,"Latest usable backup must use dates rather than file order");
        Require(engine.BackupHistory(new Connection {server="::1",port=15583},"FixtureDB").Length==3,"Local endpoint aliases must share history");
        Require(File.ReadAllBytes(settingsPath).SequenceEqual(originalSettings),"Reading history must not change settings");
        foreach(string badDate in new[]{"not-a-date","2026-10-09","2026-10-09T12:00:00","2026-02-30T12:00:00Z"}){
            tie.createdUtc=badDate;Write(settingsPath,settings);byte[] invalidSettings=File.ReadAllBytes(settingsPath);
            bool rejected=false;try{engine.BackupHistory(connection,"FixtureDB");}catch(Exception ex){rejected=ex.Message.Contains("UserSettings.json");}
            Require(rejected,"Malformed or ambiguous backup date was accepted: "+badDate);
            Require(File.ReadAllBytes(settingsPath).SequenceEqual(invalidSettings),"Invalid history was replaced");
        }
        tie.createdUtc="2026-10-09T14:00:00+02:00";
        foreach(string badPath in new[]{"../outside.dump","pg_backup/2026-10-09/../GoldenTee2020_261009_DBbackup","pg_backup/2026-10-09/GoldenTee2020_261009_DBbackup/child","pg_backup/2026-10-09/GoldenTee2020_261009_DBbackup.dump","pg_backup/2026-02-30/GoldenTee2020_260230_DBbackup"}){
            tie.file=badPath;Write(settingsPath,settings);byte[] invalidSettings=File.ReadAllBytes(settingsPath);
            bool rejected=false;try{engine.BackupHistory(connection,"FixtureDB");}catch(Exception ex){rejected=ex.Message.Contains("UserSettings.json");}
            Require(rejected,"Invalid backup path was accepted: "+badPath);
            Require(File.ReadAllBytes(settingsPath).SequenceEqual(invalidSettings),"Invalid backup paths were replaced");
        }
        tie.file="pg_backup/2026-10-09/GoldenTee2020_261009_2_DBbackup";tie.kind="unknown";Write(settingsPath,settings);
        byte[] invalidKindSettings=File.ReadAllBytes(settingsPath);bool invalidKindRejected=false;
        try{engine.BackupHistory(connection,"FixtureDB");}catch(Exception ex){invalidKindRejected=ex.Message.Contains("UserSettings.json");}
        Require(invalidKindRejected,"An unknown backup kind was accepted");
        Require(File.ReadAllBytes(settingsPath).SequenceEqual(invalidKindSettings),"Invalid backup kinds were replaced");
        VerifyInitialSourceAdoption(Path.GetFullPath(args[0]));
        var selection=new AdoptionFixture(Path.GetFullPath(args[0]),"restore-selection");
        var endpoint=new Connection {server="localhost",port=15583};
        selection.engine.SelectRecordedBackup(endpoint,"FixtureDB",selection.record);
        var confirmed=selection.engine.PrepareRecordedRestore(endpoint,"FixtureDB",selection.record);
        selection.engine.ValidateConfirmedRestore(endpoint,confirmed);
        selection.EditCatalog(game=>game.databaseFile=selection.sourcePath);
        bool sourceChanged=false;try{selection.engine.ValidateConfirmedRestore(endpoint,confirmed);}catch{sourceChanged=true;}
        Require(sourceChanged,"A confirmed restore must reject a source changed during refresh");
        selection.engine.SelectRecordedBackup(endpoint,"FixtureDB",selection.record);
        bool endpointChanged=false;try{selection.engine.ValidateConfirmedRestore(new Connection {server="localhost",port=15584},confirmed);}catch{endpointChanged=true;}
        Require(endpointChanged,"A confirmed restore must reject a destination changed during refresh");
        Require(selection.ReadCatalog().games[0].databaseFile==selection.copyPath,"Restore selection must persist the chosen verified archive");
        bool denied=false;try{selection.engine.DeleteRecordedBackup(new Connection {server="localhost",port=15584},"FixtureDB",selection.record);}catch{denied=true;}
        Require(denied && File.Exists(selection.copyPath),"Wrong endpoint must not delete a backup");
        File.AppendAllText(selection.copyPath,"changed");denied=false;
        try{selection.engine.DeleteRecordedBackup(endpoint,"FixtureDB",selection.record);}catch{denied=true;}
        Require(denied && File.Exists(selection.copyPath),"Changed backup contents must not be deleted");
        File.WriteAllBytes(selection.copyPath,selection.sourceBefore);
        selection.engine.DeleteRecordedBackup(endpoint,"FixtureDB",selection.record);
        Require(!File.Exists(selection.copyPath) && selection.engine.BackupHistory(endpoint,"FixtureDB").Length==0 && File.Exists(selection.sourcePath),"Deletion must remove only the tracked copy and history, keeping the original");
        var missing=new AdoptionFixture(Path.GetFullPath(args[0]),"delete-missing");File.Delete(missing.copyPath);
        missing.engine.DeleteRecordedBackup(endpoint,"FixtureDB",missing.record);
        Require(missing.engine.BackupHistory(endpoint,"FixtureDB").Length==0,"Missing backup records must be removable");
        Console.WriteLine("Backup history passed: compact game/date filenames and numbered versions, initial/export/legacy history, endpoint/date scope, missing files, invalid history preservation, and initial source adoption preserving concurrent edits while rejecting changed choices or invalid copies; no database connection.");
    }
}
