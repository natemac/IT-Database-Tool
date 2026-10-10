using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace ITDatabaseSetup
{
    public sealed class ConfirmedBackupRestore
    {
        public string Database {get;private set;}
        public string Path {get;private set;}
        public string SHA256 {get;private set;}
        internal string Server {get;private set;}
        internal int Port {get;private set;}
        internal DatabaseBackup Record {get;private set;}
        internal ConfirmedBackupRestore(Connection connection,string database,string path,DatabaseBackup record)
        {Database=database;Path=path;SHA256=record.sha256;Server=connection.server;Port=connection.port;Record=Engine.Json.Deserialize<DatabaseBackup>(Engine.Json.Serialize(record));}
    }
    public sealed partial class Engine
    {
        public ConfirmedBackupRestore PrepareRecordedRestore(Connection connection,string database,DatabaseBackup record)
        {return new ConfirmedBackupRestore(connection,database,ValidateRecordedBackup(connection,database,record),record);}
        public void ValidateConfirmedRestore(Connection connection,ConfirmedBackupRestore restore)
        {
            if(restore==null || ServerKey(connection.server)!=ServerKey(restore.Server) || connection.port!=restore.Port)throw new Exception("The restore connection changed. Choose the backup again and confirm the new destination.");
            string path=ValidateRecordedBackup(connection,restore.Database,restore.Record);
            ReloadDefinition();Game game=catalog.SingleOrDefault(item=>item.database==restore.Database);
            if(game==null || !String.Equals(path,restore.Path,StringComparison.OrdinalIgnoreCase) || !String.Equals(game.databaseFile,restore.Path,StringComparison.OrdinalIgnoreCase) || game.sha256!=restore.SHA256)throw new Exception("The selected restore file changed. Choose the backup again and confirm it.");
        }
        DatabaseBackup RecordedBackup(Connection connection,string database,DatabaseBackup selected)
        {
            if(selected==null)throw new Exception("Choose a backup first.");
            var record=BackupHistory(connection,database).SingleOrDefault(item=>Json.Serialize(item)==Json.Serialize(selected));
            if(record==null)throw new Exception("This backup history changed. Reopen the backup list.");
            return record;
        }
        void ValidateBackupFile(DatabaseBackup record)
        {
            EnsureBackupParents(record.file);
            string path=BackupRecordPath(record);
            if(!File.Exists(path) || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0 || new FileInfo(path).Length!=record.bytes || Hash(path)!=record.sha256)throw new Exception("The backup is missing or changed. Its file was kept.");
        }
        public void SelectRecordedBackup(Connection connection,string database,DatabaseBackup selected)
        {
            RememberBackup(database,ValidateRecordedBackup(connection,database,selected));
        }
        public string ValidateRecordedBackup(Connection connection,string database,DatabaseBackup selected)
        {var record=RecordedBackup(connection,database,selected);ValidateBackupFile(record);return BackupRecordPath(record);}
        public void DeleteRecordedBackup(Connection connection,string database,DatabaseBackup selected)
        {
            using(var gate=new Mutex(false,"Local\\ITDatabaseTool_"+HashText(ServerKey(connection.server)+":"+connection.port.ToString()))){
                bool owns=false;
                try{
                    try{owns=gate.WaitOne(0);}catch(AbandonedMutexException){owns=true;}
                    if(!owns)throw new Exception("A database operation is running. Wait for it to finish.");
                    var record=RecordedBackup(connection,database,selected);
                    var settings=ReadUserSettings();var records=ReadBackupRecords(settings);
                    if(!records.Any(item=>Json.Serialize(item)==Json.Serialize(record)))throw new Exception("This backup history changed. Reopen the backup list.");
                    // A path shared by another history entry must never be deleted.
                    if(records.Count(item=>String.Equals(BackupRecordPath(item),BackupRecordPath(record),StringComparison.OrdinalIgnoreCase))!=1)throw new Exception("Another backup record uses this file. Its file was kept.");
                    if(File.Exists(BackupRecordPath(record))){ValidateBackupFile(record);File.Delete(BackupRecordPath(record));}
                    else EnsureBackupParents(record.file);
                    records.RemoveAll(item=>Json.Serialize(item)==Json.Serialize(record));
                    settings["backups"]=records;SaveUserSettings(settings);
                    // If recording removal fails, the history remains as a missing
                    // file and can be removed again; unrelated records are retained.
                }finally{if(owns)gate.ReleaseMutex();}
            }
        }
        // Called only after a missing game's first successful installation. This
        // preserves the supplied archive, not a dump of the installed schema.
        void RecordInitialBackup(Game game,Entry entry,Connection connection,string password)
        {
            string partial=null,destination=null;
            bool published=false,recorded=false;
            try{
                var settings=ReadUserSettings();
                var records=ReadBackupRecords(settings);
                Func<DatabaseBackup,bool> sameGame=item=>ServerKey(item.server)==ServerKey(connection.server) && item.port==connection.port && item.requestedDatabase==game.database;
                if(records.Any(sameGame)){
                    DatabaseBackup initial=records.LastOrDefault(item=>sameGame(item) && item.kind=="initial" && item.sha256==entry.backupSHA256 && File.Exists(BackupRecordPath(item)));
                    if(initial!=null){destination=BackupRecordPath(initial);published=true;recorded=true;AdoptInitialBackup(game,entry,initial);}
                    return;
                }
                if(String.IsNullOrEmpty(entry.backup) || String.IsNullOrEmpty(entry.backupSHA256))throw new Exception("The original database file was not recorded for this install.");
                string source=Path.GetFullPath(entry.backup);
                if(!File.Exists(source) || !String.Equals(Hash(source),entry.backupSHA256,StringComparison.OrdinalIgnoreCase))throw new Exception("The original database file changed or disappeared after installation. Its initial backup was not saved.");

                DateTimeOffset captured=DateTimeOffset.Now;
                string relative=BackupRelativePath(game,captured.DateTime);
                for(int version=1;File.Exists(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar))) || Directory.Exists(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar))) || records.Any(item=>String.Equals(item.file,relative,StringComparison.OrdinalIgnoreCase));version++)relative=BackupRelativePath(game,captured.DateTime,version+1);
                destination=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));
                EnsureBackupParents(relative);
                partial=Path.Combine(Path.GetDirectoryName(destination),"."+Guid.NewGuid().ToString("N")+".partial");
                // Create our own file so read-only attributes on a supplied
                // original do not prevent removal of an incomplete private copy.
                using(FileStream input=File.OpenRead(source))
                using(FileStream output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None))input.CopyTo(output);
                if(new FileInfo(partial).Length==0 || !String.Equals(Hash(partial),entry.backupSHA256,StringComparison.OrdinalIgnoreCase))throw new Exception("The copied initial database file did not match the original archive.");
                ToolResult verify=RunTool(connection,password,"pg_restore.exe",new[]{"--file","NUL",partial},600000);
                if(verify.code!=0)throw new Exception("The initial database archive could not be read completely. "+verify.error.Trim());
                if(!String.Equals(Hash(source),entry.backupSHA256,StringComparison.OrdinalIgnoreCase))throw new Exception("The original database file changed while its initial backup was being verified.");
                string[] database=Sql(connection,password,"postgres","SELECT oid FROM pg_database WHERE datname="+QL(entry.database)+";");
                if(database.Length!=1 || database[0]!=entry.restoreDatabaseOid)throw new Exception("The installed database changed before its initial backup could be recorded.");

                // Re-read after the potentially long copy/verification to retain
                // unrelated preference changes and avoid a second first backup.
                settings=ReadUserSettings();records=ReadBackupRecords(settings);
                if(records.Any(sameGame)){
                    File.Delete(partial);partial=null;
                    DatabaseBackup initial=records.LastOrDefault(item=>sameGame(item) && item.kind=="initial" && item.sha256==entry.backupSHA256 && File.Exists(BackupRecordPath(item)));
                    if(initial!=null){destination=BackupRecordPath(initial);published=true;recorded=true;AdoptInitialBackup(game,entry,initial);}
                    return;
                }
                EnsureBackupParents(relative);
                if(File.Exists(destination) || Directory.Exists(destination) || records.Any(item=>String.Equals(item.file,relative,StringComparison.OrdinalIgnoreCase)))throw new Exception("The initial backup location is now occupied. The existing file was preserved.");
                var record=new DatabaseBackup {kind="initial",game=game.game,requestedDatabase=game.database,database=entry.database,databaseOid=database[0],server=connection.server,port=connection.port,encoding=game.encoding,createdUtc=captured.ToUniversalTime().ToString("o"),file=relative,sha256=entry.backupSHA256,bytes=new FileInfo(partial).Length};
                File.Move(partial,destination);partial=null;published=true;
                records.Add(record);settings["backups"]=records;SaveUserSettings(settings);recorded=true;
                AdoptInitialBackup(game,entry,record);
                entry.detail+=" Initial database source backed up to "+destination+" and selected for future restores. The original file was kept.";
            }catch(Exception failure){
                string message=recorded?"The verified initial database backup and its history were saved at "+destination+", but the copied archive could not be selected for future restores. Keep this file. ":published?"The verified initial database backup was saved at "+destination+", but its history could not be saved. Keep this file. ":"The initial database source backup was not saved. The original file was kept. ";
                message+=failure.Message;
                if(partial!=null && File.Exists(partial)){
                    try{File.Delete(partial);}
                    catch(Exception cleanup){message+=" The incomplete private copy could not be removed: "+partial+". "+cleanup.Message;}
                }
                if(!String.IsNullOrEmpty(password))message=message.Replace(password,"[redacted]");
                throw new Exception(message,failure);
            }
        }

        void AdoptInitialBackup(Game game,Entry entry,DatabaseBackup record)
        {
            string copied=BackupRecordPath(record);
            EnsureBackupParents(record.file);
            if(!File.Exists(copied) || (File.GetAttributes(copied)&FileAttributes.ReparsePoint)!=0 || !DatabaseArchive(copied) || Hash(copied)!=record.sha256)throw new Exception("The recorded initial backup is missing, changed, or is not a readable PostgreSQL archive. The current database file choice was kept.");
            if(record.kind!="initial" || record.requestedDatabase!=game.database || record.sha256!=entry.backupSHA256)throw new Exception("The initial backup does not match the archive used for this install. The current database file choice was kept.");

            // Preserve edits made while the restore/copy ran. In particular,
            // do not replace a user's newly selected source with an old choice.
            ReloadDefinition();
            Game current=catalog.SingleOrDefault(item=>item.database==game.database);
            if(current==null)throw new Exception("This game changed in DatabaseInfo.json. Its current database file choices were kept.");
            if(String.Equals(current.databaseFile,copied,StringComparison.OrdinalIgnoreCase) && String.Equals(current.sha256,record.sha256,StringComparison.OrdinalIgnoreCase))return;
            if(!String.Equals(current.databaseFile??"",game.databaseFile??"",StringComparison.OrdinalIgnoreCase) || !String.Equals(current.sha256??"",game.sha256??"",StringComparison.OrdinalIgnoreCase) || !String.Equals(current.backup,game.backup,StringComparison.OrdinalIgnoreCase) || !String.Equals(current.backupFolder,game.backupFolder,StringComparison.OrdinalIgnoreCase))throw new Exception("The database file choice changed during installation. The new choice was kept; select the saved initial backup manually if you want to use it.");
            current.databaseFile=copied;current.sha256=record.sha256;
            SaveDefinition();
        }
    }
}
