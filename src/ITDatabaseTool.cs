using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]

namespace ITDatabaseSetup
{
    public sealed class Game
    {
        public string game { get; set; }
        public string database { get; set; }
        public string encoding { get; set; }
        public string backup { get; set; }
        public string backupFolder { get; set; }
        public string sha256 { get; set; }
        public string restoreProfile { get; set; }
        public string databaseFile { get; set; }
    }
    public sealed class Connection
    {
        public string server = "127.0.0.1";
        public int port = 5432;
        public string username = "postgres";
        public string pgBin = @"C:\Program Files (x86)\PostgreSQL\8.3\bin";
    }
    public sealed class DefinitionConnection
    {
        public string server = "127.0.0.1";
        public int port = 5432;
        public string username = "postgres";
        public string password = "";
        public string pgBin;
    }
    public sealed class DatabaseDefinition
    {
        public int schemaVersion;
        public DefinitionConnection connection;
        public Game[] games;
    }
    public sealed class Entry
    {
        public string game;
        public string requestedDatabase;
        public string database;
        public string encoding;
        public string backup;
        public string backupSHA256;
        public string status;
        public string detail;
        public string restoreLog;
        public int? restoreExitCode;
        public int? publicTables;
        public int? gameTables;
        public int? expectedTables;
        public bool? sourceUnchanged;
        public bool settingsVerified;
        public bool databaseInstalled;
        public string restoreDatabase;
        public string restoreDatabaseOid;
        public string recoveryDatabase;
        public bool databaseNeedsRepair;
        public string databaseHealthDetail;
        public string databaseCheckWarning;
        public string initialBackupError;
        public bool installVerified;
    }
    public sealed class Report
    {
        public string started = DateTimeOffset.Now.ToString("o");
        public string server;
        public int port;
        public string serverVersion;
        public bool plan;
        public bool completed;
        public string error;
        public List<Entry> entries = new List<Entry>();
    }
    public sealed class FileLoadEntry
    {
        public string game,database,status,sourceFile,destinationFile,detail;
    }
    public sealed class FileLoadResult
    {
        public string sourceFolder,reportPath,error;
        public int filesFound;
        public bool canceled;
        public List<FileLoadEntry> entries=new List<FileLoadEntry>();
        public List<string> skippedPaths=new List<string>();
    }
    internal sealed class ToolResult { public int code; public string output; public string error; }
    internal sealed class BackupChoiceException : Exception { public BackupChoiceException(string text):base(text){} }
    public sealed class UserSettings { public bool hideWelcome; public DefinitionConnection connection; }
    public sealed class DatabaseBackup
    {
        public string game,requestedDatabase,database,databaseOid,server,encoding,createdUtc,file,sha256,kind;
        public int port;
        public long bytes;
    }
    public sealed class InstallRecord
    {
        public string server,database,databaseOid,status,detail,reportPath;
        public int port;
    }

    public sealed partial class Engine
    {
        public readonly string root;
        public Game[] catalog;
        DatabaseDefinition definition;
        public string DefinitionPath;
        public string SettingsPath { get { return Path.Combine(root,"UserSettings.json"); } }
        public string LastReportPath;
        string[] backupFiles;
        Dictionary<string,string> hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> FileScanWarnings=new List<string>();
        public string BackupDirectory { get { return Path.Combine(root,"PG_backup"); } }
        public string UserBackupDirectory { get { return BackupDirectory; } }
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        public Engine(string directory)
        {
            root = Path.GetFullPath(directory);
            ReloadDefinition();
        }
        public static string ExpectedBackupPath(Game game) {return "\\"+game.backup.Replace('/','\\');}
        public bool HideWelcome()
        {
            var settings=ReadUserSettings();
            return settings.ContainsKey("hideWelcome") && (bool)settings["hideWelcome"];
        }
        public void SaveWelcomePreference(bool hide)
        {
            var settings=ReadUserSettings();settings["hideWelcome"]=hide;SaveUserSettings(settings);
        }
        Dictionary<string,object> ReadUserSettings()
        {
            if(!File.Exists(SettingsPath))return new Dictionary<string,object>();
            try{
                var settings=Json.DeserializeObject(File.ReadAllText(SettingsPath)) as Dictionary<string,object>;
                if(settings==null || (settings.ContainsKey("hideWelcome") && !(settings["hideWelcome"] is bool)))throw new Exception();
                if(settings.ContainsKey("connection"))SettingsConnection(settings);
                ReadBackupRecords(settings);
                return settings;
            }catch{throw new Exception("Cannot read UserSettings.json. Check its connection fields and JSON syntax, or restore a valid copy. Your settings have not been replaced.");}
        }
        static DefinitionConnection SettingsConnection(Dictionary<string,object> settings)
        {
            object raw;
            if(!settings.TryGetValue("connection",out raw))return new DefinitionConnection();
            var fields=raw as Dictionary<string,object>;
            // Values written in this process may already be typed; loaded JSON is a dictionary.
            DefinitionConnection connection=raw as DefinitionConnection;
            if(connection==null){
                CheckKeys(fields,new[]{"server","port","username","password","pgBin"},new string[0]);
                foreach(string key in new[]{"server","username","password","pgBin"})
                    if(fields.ContainsKey(key) && fields[key]!=null && !(fields[key] is string))throw new Exception("Invalid connection field in UserSettings.json.");
                if(fields.ContainsKey("port") && !(fields["port"] is int))throw new Exception("Invalid connection port in UserSettings.json.");
                connection=Json.Deserialize<DefinitionConnection>(Json.Serialize(fields));
            }
            ValidateStoredConnection(connection);
            return connection;
        }
        static void ValidateStoredConnection(DefinitionConnection connection)
        {
            if(connection==null || (connection.server!="127.0.0.1" && connection.server!="localhost" && connection.server!="::1"))throw new Exception("Use a local server address in UserSettings.json: 127.0.0.1, localhost, or ::1.");
            if(connection.port<1 || connection.port>65535 || String.IsNullOrWhiteSpace(connection.username))throw new Exception("Check connection.port and connection.username in UserSettings.json.");
        }
        void SaveUserSettings(Dictionary<string,object> settings)
        {
            SettingsConnection(settings);AtomicText(SettingsPath,PrettyJson(settings));
        }
        List<DatabaseBackup> ReadBackupRecords(Dictionary<string,object> settings)
        {
            if(!settings.ContainsKey("backups"))return new List<DatabaseBackup>();
            var records=Json.Deserialize<List<DatabaseBackup>>(Json.Serialize(settings["backups"]));
            if(records==null || records.Any(item=>item==null))throw new Exception("Invalid backup history in UserSettings.json.");
            foreach(var record in records){
                if(!Regex.IsMatch(record.requestedDatabase??"","^[A-Za-z_][A-Za-z0-9_]{0,62}$") || String.IsNullOrEmpty(record.server) || record.port<1 || record.port>65535 || !Regex.IsMatch(record.sha256??"","^[a-f0-9]{64}$") || !Regex.IsMatch(record.encoding??"","^[A-Za-z0-9_]+$") || record.bytes<1)throw new Exception("Invalid backup history in UserSettings.json.");
                if(record.kind!=null && record.kind!="initial" && record.kind!="export")throw new Exception("Invalid backup type in UserSettings.json.");
                BackupCreatedUtc(record);
                BackupRecordPath(record);
            }
            return records;
        }
        static DateTimeOffset BackupCreatedUtc(DatabaseBackup record)
        {
            DateTimeOffset created;
            // Existing exports use the round-trip format. Accept equivalent ISO
            // timestamps, but require a timezone so UI dates cannot be ambiguous.
            string[] formats={"o","yyyy-MM-dd'T'HH:mm:ss'Z'","yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'","yyyy-MM-dd'T'HH:mm:sszzz","yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"};
            if(!DateTimeOffset.TryParseExact(record.createdUtc,formats,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out created))throw new Exception("Invalid backup date in UserSettings.json.");
            return created;
        }
        public string BackupRecordPath(DatabaseBackup record)
        {
            string prefix="PG_backup/User Backups/"+record.requestedDatabase+"/";
            string file=record.file??"";
            bool legacy=file.StartsWith(prefix,StringComparison.Ordinal) && file.Substring(prefix.Length).IndexOfAny(new[]{'/', '\\'})<0 && Regex.IsMatch(file.Substring(prefix.Length),@"^[A-Za-z0-9_.-]+\.dump$");
            if(!legacy && !DatedBackupPath(file))throw new Exception("Invalid saved backup path in UserSettings.json.");
            return Path.Combine(root,file.Replace('/',Path.DirectorySeparatorChar));
        }
        static bool DatedBackupPath(string file)
        {
            Match match=Regex.Match(file??"",@"\Apg_backup/(?<day>[0-9]{4}-[0-9]{2}-[0-9]{2})/[A-Za-z0-9]+_(?<short>[0-9]{6})(?:_(?:[2-9]|[1-9][0-9]+))?_DBbackup\z",RegexOptions.IgnoreCase);
            DateTime day;
            return match.Success && DateTime.TryParseExact(match.Groups["day"].Value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out day) && day.ToString("yyMMdd",CultureInfo.InvariantCulture)==match.Groups["short"].Value;
        }
        public static string BackupRelativePath(Game game,DateTime localDate,int version=1)
        {
            if(game==null)throw new ArgumentNullException("game");
            if(version<1)throw new ArgumentOutOfRangeException("version");
            string name=Regex.Replace(game.game??"",@"\bLive\b","",RegexOptions.IgnoreCase).Replace("&","And");
            name=Regex.Replace(name,"[^A-Za-z0-9]","");
            if(name.Length==0)name=Regex.Replace(game.database??"","[^A-Za-z0-9]","");
            if(name.Length==0)throw new Exception("Choose a game name for its backup filename.");
            if(name.Length>100)name=name.Substring(0,100);
            return "pg_backup/"+localDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"/"+name+"_"+localDate.ToString("yyMMdd",CultureInfo.InvariantCulture)+(version==1?"":"_"+version.ToString(CultureInfo.InvariantCulture))+"_DBbackup";
        }
        void EnsureBackupParents(string relative)
        {
            string folder=root;
            string[] parts=relative.Split('/');
            foreach(string part in parts.Take(parts.Length-1)){folder=Path.Combine(folder,part);EnsureNormalFolder(folder);}
        }
        public DatabaseBackup LatestBackup(Connection connection,string database)
        {
            return BackupHistory(connection,database).FirstOrDefault(item=>File.Exists(BackupRecordPath(item)));
        }
        public DatabaseBackup[] BackupHistory(Connection connection,string database)
        {
            if(connection==null)throw new ArgumentNullException("connection");
            return ReadBackupRecords(ReadUserSettings()).Select((item,index)=>new{record=item,index=index})
                .Where(item=>ServerKey(item.record.server)==ServerKey(connection.server) && item.record.port==connection.port && item.record.requestedDatabase==database)
                .OrderByDescending(item=>BackupCreatedUtc(item.record)).ThenByDescending(item=>item.index)
                .Select(item=>item.record).ToArray();
        }
        public string BackupStatus(Connection connection,string database)
        {
            DatabaseBackup latest=BackupHistory(connection,database).FirstOrDefault();
            // This is the last recorded snapshot date, even if its file was moved
            // or removed. File availability belongs in the details, not this date.
            return latest==null?"Never":BackupCreatedUtc(latest).ToLocalTime().ToString("MMM d, yyyy",CultureInfo.CurrentCulture);
        }
        bool IsUserBackup(string file)
        {
            string full=Path.GetFullPath(file);
            if(full.StartsWith(Path.Combine(BackupDirectory,"User Backups")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))return true;
            string prefix=BackupDirectory+Path.DirectorySeparatorChar;
            return full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) && DatedBackupPath("pg_backup/"+full.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar,'/'));
        }
        public string PrepareBackupFolder(Game game)
        {
            string folder=BackupDirectory;
            EnsureNormalFolder(folder);
            foreach(string part in new[]{game.backupFolder}.Concat(game.backup.Split('/').Take(game.backup.Split('/').Length-1))){folder=Path.Combine(folder,part);EnsureNormalFolder(folder);}
            return folder;
        }
        static void EnsureNormalFolder(string folder)
        {
            if(Directory.Exists(folder) && (File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new Exception("Use a normal database file folder, rather than a link: "+folder);
            Directory.CreateDirectory(folder);
        }
        public void PrepareBackupFolders() {ReloadDefinition();foreach(Game game in catalog)PrepareBackupFolder(game);}
        public void ReloadDefinition()
        {
            DefinitionPath=Path.Combine(root,"DatabaseInfo.json");
            if(!File.Exists(DefinitionPath)){
                string legacy=Path.Combine(root,"DatabaseInfo.txt");
                if(!File.Exists(legacy))legacy=Path.Combine(root,"Database Info.txt");
                if(!File.Exists(legacy))throw new Exception("Place DatabaseInfo.json beside IT Database Builder.exe. An existing DatabaseInfo.txt can also be imported once.");
                DatabaseDefinition imported=ReadLegacy(legacy);ValidateDefinition(imported);
                MigrateConnection(imported);AtomicText(DefinitionPath,DefinitionJson(imported));
            }
            DatabaseDefinition loaded;
            try{string text=File.ReadAllText(DefinitionPath);ValidateJsonKeys(text);loaded=Json.Deserialize<DatabaseDefinition>(text);}
            catch{throw new Exception("Cannot read DatabaseInfo.json. Check its field names and JSON syntax (double quotes, commas and brackets), or restore a valid copy. The old TXT file is not used when JSON exists.");}
            ValidateDefinition(loaded);MigrateConnection(loaded);definition=loaded;catalog=loaded.games;
            if(loaded.connection!=null){AtomicText(DefinitionPath,DefinitionJson(loaded));loaded.connection=null;}
        }
        static string DefinitionJson(DatabaseDefinition value) {return PrettyJson(new {schemaVersion=value.schemaVersion,games=value.games});}
        void MigrateConnection(DatabaseDefinition loaded)
        {
            var settings=ReadUserSettings();
            if(settings.ContainsKey("connection"))return;
            DefinitionConnection connection=loaded.connection??new DefinitionConnection();
            ValidateStoredConnection(connection);
            settings["connection"]=connection;
            if(!settings.ContainsKey("hideWelcome"))settings["hideWelcome"]=false;
            // Commit preferences first. A failed catalogue write can safely retry without
            // replacing existing settings, including an intentionally cleared password.
            SaveUserSettings(settings);
        }
        static void ValidateJsonKeys(string text)
        {
            var document=Json.DeserializeObject(text) as Dictionary<string,object>;
            CheckKeys(document,new[]{"schemaVersion","connection","games"},new[]{"schemaVersion","games"});
            if(document.ContainsKey("connection"))CheckKeys(document["connection"] as Dictionary<string,object>,new[]{"server","port","username","password","pgBin"},new string[0]);
            var games=document["games"] as object[];if(games==null)throw new Exception();
            foreach(object game in games)CheckKeys(game as Dictionary<string,object>,new[]{"game","database","encoding","backup","backupFolder","sha256","restoreProfile","databaseFile"},new[]{"game","database","encoding","backup"});
        }
        static void CheckKeys(Dictionary<string,object> value,string[] allowed,string[] required)
        {
            if(value==null || value.Keys.Any(key=>!allowed.Contains(key)) || required.Any(key=>!value.ContainsKey(key)))throw new Exception();
        }
        static DatabaseDefinition ReadLegacy(string legacy)
        {
            List<Game> games=new List<Game>();
            Dictionary<string,string> globals=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string,string> block=null;
            string heading=null;
            int lineNumber=0;
            foreach(string raw in File.ReadAllLines(legacy))
            {
                lineNumber++;
                string line=raw.Trim();
                if(line.Length==0 || line.StartsWith("#") || line.StartsWith(";"))continue;
                int colon=line.IndexOf(':');
                if(colon<0)
                {
                    if(block!=null){games.Add(ParseGame(heading,block));block=null;}
                    heading=line;continue;
                }
                string key=Regex.Replace(line.Substring(0,colon).Trim(),@"\s+"," ");
                string value=line.Substring(colon+1).Trim();
                if(key.Equals("Game",StringComparison.OrdinalIgnoreCase))
                {
                    if(block!=null){games.Add(ParseGame(heading,block));block=null;}
                    heading=value;continue;
                }
                if(key.Equals("DB name",StringComparison.OrdinalIgnoreCase))
                {
                    if(block!=null)throw new Exception("Duplicate DB name near line "+lineNumber+". Start each game with its heading.");
                    if(String.IsNullOrEmpty(heading))throw new Exception("Add a game heading before DB name at line "+lineNumber+".");
                    block=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);block.Add("DB name",value);continue;
                }
                Dictionary<string,string> destination=block??globals;
                if(destination.ContainsKey(key))throw new Exception("Duplicate field '"+key+"' at line "+lineNumber+" in DatabaseInfo.txt.");
                destination[key]=value;
            }
            if(block!=null)games.Add(ParseGame(heading,block));
            DefinitionConnection connection=new DefinitionConnection();
            if(globals.ContainsKey("Address"))connection.server=globals["Address"];else if(globals.ContainsKey("Server"))connection.server=globals["Server"];
            if(globals.ContainsKey("Username"))connection.username=globals["Username"];
            if(globals.ContainsKey("Password"))connection.password=globals["Password"];
            if(globals.ContainsKey("Postgres Path"))connection.pgBin=globals["Postgres Path"];
            if(globals.ContainsKey("Port")){int value;if(!Int32.TryParse(globals["Port"],out value))throw new Exception("Check Port in the old DatabaseInfo.txt.");connection.port=value;}
            return new DatabaseDefinition {schemaVersion=1,connection=connection,games=games.ToArray()};
        }
        static void ValidateDefinition(DatabaseDefinition value)
        {
            if(value==null || value.schemaVersion!=1)throw new Exception("DatabaseInfo.json must have schemaVersion: 1. This version of the app cannot read other schema versions.");
            if(value.games==null || value.games.Length==0 || value.games.Any(g=>g==null))throw new Exception("DatabaseInfo.json needs at least one complete game entry.");
            if(value.games.Select(g=>g.database??"").Distinct(StringComparer.OrdinalIgnoreCase).Count()!=value.games.Length)throw new Exception("Use unique database names in DatabaseInfo.json.");
            foreach(Game g in value.games){
                if(String.IsNullOrWhiteSpace(g.game) || !Regex.IsMatch(g.database??"","^[A-Za-z_][A-Za-z0-9_]{0,62}$") || !Regex.IsMatch(g.encoding??"","^[A-Za-z0-9_]+$"))throw new Exception("Each JSON game needs a name, a valid database name (letters, digits, underscores), and an encoding.");
                g.encoding=g.encoding.ToUpperInvariant();
                if(!String.IsNullOrEmpty(g.restoreProfile) && g.restoreProfile!="it-category-keys-v1")throw new Exception("Unknown restoreProfile for "+g.game+" in DatabaseInfo.json.");
                if(!String.IsNullOrEmpty(g.databaseFile)){
                    if(!Path.IsPathRooted(g.databaseFile))throw new Exception("Use a full databaseFile path for "+g.game+" in DatabaseInfo.json.");
                    g.databaseFile=Path.GetFullPath(g.databaseFile);
                }
                if(String.IsNullOrWhiteSpace(g.backupFolder))g.backupFolder=g.game;
                if(g.backupFolder.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || g.backupFolder=="." || g.backupFolder=="..")throw new Exception("Use a valid backupFolder for "+g.game+" in DatabaseInfo.json.");
                if(String.IsNullOrWhiteSpace(g.backup) || Path.IsPathRooted(g.backup))throw new Exception("Use a database filename or relative path for "+g.game+" in DatabaseInfo.json.");
                g.backup=g.backup.Replace('\\','/');
                if(g.backup.Split('/').Any(part=>String.IsNullOrWhiteSpace(part) || part=="." || part==".." || part.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new Exception("Check the relative database file path for "+g.game+" in DatabaseInfo.json.");
                if(!String.IsNullOrEmpty(g.sha256)){g.sha256=g.sha256.ToLowerInvariant();if(!Regex.IsMatch(g.sha256,"^[a-f0-9]{64}$"))throw new Exception("The sha256 for "+g.game+" must have 64 hexadecimal characters.");}
            }
        }
        public static string PrettyJson(object value)
        {
            string compact=Json.Serialize(value);StringBuilder result=new StringBuilder();int indent=0;bool quoted=false,escaped=false;
            foreach(char ch in compact){
                if(quoted){result.Append(ch);if(escaped)escaped=false;else if(ch=='\\')escaped=true;else if(ch=='"')quoted=false;continue;}
                if(ch=='"'){quoted=true;result.Append(ch);}
                else if(ch=='{' || ch=='['){result.Append(ch);result.AppendLine();indent++;result.Append(' ',indent*2);}
                else if(ch=='}' || ch==']'){result.AppendLine();indent--;result.Append(' ',indent*2);result.Append(ch);}
                else if(ch==','){result.Append(ch);result.AppendLine();result.Append(' ',indent*2);}
                else if(ch==':')result.Append(": ");else if(!Char.IsWhiteSpace(ch))result.Append(ch);
            }
            return result.ToString()+Environment.NewLine;
        }
        void SaveDefinition()
        {
            ValidateDefinition(definition);AtomicText(DefinitionPath,DefinitionJson(definition));backupFiles=null;
        }
        public void SaveGame(Game edited,string originalDatabase,string selectedBackup=null)
        {
            ReloadDefinition();List<Game> games=catalog.ToList();
            if(originalDatabase==null)games.Add(edited);
            else{int index=games.FindIndex(g=>g.database==originalDatabase);if(index<0)throw new Exception("This entry changed in DatabaseInfo.json. Reload and edit it again.");games[index]=edited;}
            definition.games=games.ToArray();ValidateDefinition(definition);
            if(selectedBackup!=null){edited.sha256=RegisterArchive(selectedBackup);edited.databaseFile=Path.GetFullPath(selectedBackup);}
            SaveDefinition();ReloadDefinition();
        }
        static Game ParseGame(string heading,Dictionary<string,string> block)
        {
            foreach(string required in new[]{"DB name","Encoding","db filename"})
                if(!block.ContainsKey(required) || String.IsNullOrWhiteSpace(block[required]))throw new Exception("Missing '"+required+"' for "+heading+" in DatabaseInfo.txt.");
            string location=block.ContainsKey("db backup location")?block["db backup location"].Replace('\\','/').Trim('/') : "";
            location=Regex.Replace(location,@"^pg_backups?(?:/|$)","",RegexOptions.IgnoreCase).Trim('/');
            string filename=block["db filename"];
            if(filename.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || filename=="." || filename=="..")throw new Exception("Use a database filename without a path for "+heading+".");
            if(location.Split('/').Any(part=>part=="." || part==".." || part.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new Exception("Use a relative database file location for "+heading+".");
            return new Game { game=Regex.Replace(heading,@"\s+\([^()]*\)\s*$", "").Trim(), database=block["DB name"],encoding=block["Encoding"].ToUpperInvariant(),
                backup=(location.Length>0?location+"/":"")+filename,
                backupFolder=block.ContainsKey("Backup folder")?block["Backup folder"]:Regex.Replace(heading,@"\s+\([^()]*\)\s*$", "").Trim(),
                sha256=ReadHash(block,heading),restoreProfile=block.ContainsKey("Restore profile")?block["Restore profile"]:null };
        }
        static string ReadHash(Dictionary<string,string> block,string heading)
        {
            string hash=block.ContainsKey("Backup SHA256")?block["Backup SHA256"].ToLowerInvariant():null;
            if(!String.IsNullOrEmpty(hash) && !Regex.IsMatch(hash,"^[a-f0-9]{64}$"))throw new Exception("Check Backup SHA256 for "+heading+"; it must have 64 hexadecimal characters.");
            return hash;
        }
        static string Normalize(string text) {return Regex.Replace(text.Replace("&","and").ToLowerInvariant(),"[^a-z0-9]","");}
        static bool Archive(string path)
        {
            return DatabaseArchive(path);
        }
        void ScanFiles()
        {
            hashes.Clear();FileScanWarnings.Clear();List<string> files=new List<string>();
            foreach(string name in new[]{"PG_backup","pg_backups"}){
                string folder=Path.Combine(root,name);if(Directory.Exists(folder))FindDatabaseFiles(folder,files,FileScanWarnings,CancellationToken.None,null);
            }
            backupFiles=files.Where(file=>!IsUserBackup(file)).ToArray();
        }
        string CachedHash(string path) {string value;if(!hashes.TryGetValue(path,out value)){value=Hash(path);hashes[path]=value;}return value;}
        public string FindBackup(Game game)
        {
            if(backupFiles==null)ScanFiles();
            if(!String.IsNullOrEmpty(game.databaseFile)){
                try{
                if(!File.Exists(game.databaseFile))return null;
                if(!Archive(game.databaseFile))throw new BackupChoiceException("The selected database file is no longer a PostgreSQL archive. Use Add database file to choose the original.");
                if(!String.IsNullOrEmpty(game.sha256) && CachedHash(game.databaseFile)!=game.sha256)throw new BackupChoiceException("The selected database file changed. Use Add database file to confirm the file you want.");
                return game.databaseFile;
                }catch(IOException){throw new BackupChoiceException("The selected database file could not be read. Close any program using it and try again.");}
                catch(UnauthorizedAccessException){throw new BackupChoiceException("Access to the selected database file was denied. Choose a readable file.");}
            }
            // Fingerprints identify the content even when a file has been renamed.
            if(!String.IsNullOrEmpty(game.sha256))return backupFiles.FirstOrDefault(p=>CachedHash(p)==game.sha256);
            string filename=Path.GetFileName(game.backup);
            Func<string,bool> named=p=>Regex.IsMatch(Path.GetFileName(p),"^"+Regex.Escape(filename)+@"(?:\.(?:backup|dump|pgdump))?$",RegexOptions.IgnoreCase);
            string[] candidates=backupFiles.Where(named).ToArray();
            if(candidates.Length==0)return null;
            bool sharedName=catalog.Count(g=>String.Equals(Path.GetFileName(g.backup),filename,StringComparison.OrdinalIgnoreCase))>1;
            if(sharedName){
                string[] owned=candidates.Where(p=>p.Split(Path.DirectorySeparatorChar).Any(part=>Normalize(part)==Normalize(game.backupFolder))).ToArray();
                if(owned.Length>0)candidates=owned;
                else {
                    string date=Regex.Match(game.backup,@"\d{4}-\d{2}-\d{2}").Value;
                    bool uniqueDate=date.Length>0 && catalog.Count(g=>String.Equals(Path.GetFileName(g.backup),filename,StringComparison.OrdinalIgnoreCase) && g.backup.Contains(date))==1;
                    candidates=uniqueDate?candidates.Where(p=>p.Split(Path.DirectorySeparatorChar).Contains(date)).ToArray():new string[0];
                    if(candidates.Length==0)throw new BackupChoiceException("This filename belongs to more than one game. Select this game and click Add database file once.");
                }
            }
            var distinct=candidates.GroupBy(CachedHash).ToArray();
            if(distinct.Length>1)throw new BackupChoiceException("Different database files match this game. Select the game and click Add database file once.");
            return candidates[0];
        }
        string RegisterArchive(string selected)
        {
            if(!Archive(selected))throw new Exception("Select an extracted PostgreSQL custom or tar database file. Unzip downloaded packs first.");
            return Hash(selected);
        }
        public void RememberBackup(string database,string selected)
        {
            ReloadDefinition();Game game=catalog.Single(g=>g.database==database);
            game.sha256=RegisterArchive(selected);game.databaseFile=Path.GetFullPath(selected);SaveDefinition();ReloadDefinition();
        }
        static bool GameFolderMatches(Game game,string folder)
        {
            // Release-pack suffixes describe the build, not the game's title year.
            // Keep a standalone (2018) / [2018] title year, but discard versions,
            // release dates, regions and platform tags before matching.
            string title=Regex.Replace(folder??"",@"\([^)]*\)|\[[^\]]*\]",match=>Regex.IsMatch(match.Value,@"^[\(\[]\s*(?:19|20)\d{2}\s*[\)\]]$")?match.Value:" ");
            title=Regex.Replace(title,@"(?<!\d)(?:19|20)\d{2}[-_.]\d{1,2}[-_.]\d{1,2}(?!\d)"," ");
            string name=Normalize(title),canonical=Normalize(game.game),hint=Normalize(game.backupFolder);
            string year=Regex.Match(game.game+" "+game.backupFolder,@"(?<!\d)(?:19|20)\d{2}(?!\d)").Value;
            string[] titleYears=Regex.Matches(title,@"(?<!\d)(?:19|20)\d{2}(?!\d)").Cast<Match>().Select(match=>match.Value).Distinct().ToArray();
            bool hasYear=year.Length==0 || (titleYears.Length==1 && titleYears[0]==year);
            if(!hasYear)return false;
            if(name.Contains(canonical) || name.Contains(hint))return true;
            if(canonical.Contains("goldentee"))return year.Length>0 && (name.Contains("goldentee") || (Regex.IsMatch(title,@"\bgolden\b",RegexOptions.IgnoreCase) && Regex.IsMatch(title,@"\btee\b",RegexOptions.IgnoreCase)));
            if(canonical.Contains("powerputt"))return name.Contains("powerputt");
            if(canonical.Contains("silverstrike"))return (name.Contains("silverstrike") || name.Contains("ssb")) && (!canonical.Contains("live") || name.Contains("live"));
            if(canonical.Contains("orangecountychopper"))return name.Contains("orangecountychopper") || name.Contains("occpinball");
            if(canonical.Contains("targettoss"))return name.Contains("targettoss") && (canonical.Contains("darts")?name.Contains("darts"):name.Contains("bags") && !name.Contains("darts"));
            return false;
        }
        string[] FileOwners(string file,string sourceRoot)
        {
            string directory=Path.GetDirectoryName(file);
            while(directory!=null){
                string[] owners=catalog.Where(game=>GameFolderMatches(game,Path.GetFileName(directory))).Select(game=>game.database).ToArray();
                if(owners.Length>0)return owners;
                if(String.Equals(directory,sourceRoot,StringComparison.OrdinalIgnoreCase))break;
                directory=Path.GetDirectoryName(directory);
            }
            return new string[0];
        }
        static void FindDatabaseFiles(string folder,List<string> files,List<string> skipped,CancellationToken cancel,Action<string> progress)
        {
            cancel.ThrowIfCancellationRequested();
            try{
                if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0){skipped.Add(folder+" (linked folder)");return;}
                if(progress!=null)progress("Searching for database files: "+folder);
                foreach(string file in Directory.GetFiles(folder)){
                    cancel.ThrowIfCancellationRequested();
                    try{if(!Regex.IsMatch(file,@"\.[a-f0-9]{32}\.loading$",RegexOptions.IgnoreCase) && (File.GetAttributes(file)&FileAttributes.ReparsePoint)==0 && DatabaseArchive(file))files.Add(Path.GetFullPath(file));}
                    catch(IOException){skipped.Add(file+" (could not read)");}catch(UnauthorizedAccessException){skipped.Add(file+" (access denied)");}
                }
                foreach(string child in Directory.GetDirectories(folder))FindDatabaseFiles(child,files,skipped,cancel,progress);
            }catch(IOException){skipped.Add(folder+" (could not read)");}catch(UnauthorizedAccessException){skipped.Add(folder+" (access denied)");}
        }
        static bool DatabaseArchive(string path)
        {
            using(FileStream stream=File.OpenRead(path)){
                byte[] header=new byte[517];int read=stream.Read(header,0,header.Length);
                if(read>=5 && Encoding.ASCII.GetString(header,0,5)=="PGDMP")return true;
                return read==header.Length && Encoding.ASCII.GetString(header,0,100).TrimEnd('\0')=="toc.dat" && Encoding.ASCII.GetString(header,512,5)=="PGDMP";
            }
        }
        public FileLoadResult AutoLoadDatabaseFiles(string directory,Action<string> progress=null,CancellationToken cancel=default(CancellationToken))
        {
            ReloadDefinition();string sourceRoot=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if(sourceRoot.EndsWith(":"))sourceRoot+=Path.DirectorySeparatorChar;
            if(!Directory.Exists(sourceRoot))throw new Exception("Choose an existing games folder.");
            if((File.GetAttributes(sourceRoot)&FileAttributes.ReparsePoint)!=0)throw new Exception("Choose a normal games folder instead of a linked folder.");
            byte[] definitionBefore=File.ReadAllBytes(DefinitionPath);
            FileLoadResult result=new FileLoadResult {sourceFolder=sourceRoot};
            List<string> sources=new List<string>();bool changed=false;
            try{
                FindDatabaseFiles(sourceRoot,sources,result.skippedPaths,cancel,progress);sources.RemoveAll(IsUserBackup);sources.Sort(StringComparer.OrdinalIgnoreCase);result.filesFound=sources.Count;
                Dictionary<string,string> sourceHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string,string[]> owners=new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase);
                Func<string,string> sourceHash=file=>{cancel.ThrowIfCancellationRequested();string hash;if(!sourceHashes.TryGetValue(file,out hash)){hash=Hash(file);sourceHashes[file]=hash;}return hash;};
                foreach(string file in sources)owners[file]=FileOwners(file,sourceRoot);
                ScanFiles();
                foreach(Game game in catalog){
                    cancel.ThrowIfCancellationRequested();if(progress!=null)progress("Loading database file for "+game.game+"...");
                    FileLoadEntry entry=new FileLoadEntry {game=game.game,database=game.database};result.entries.Add(entry);
                    try{
                        try{string local=FindBackup(game);if(local!=null){entry.status="Already available";entry.destinationFile=local;continue;}}
                        catch(BackupChoiceException ex){if(!ex.Message.StartsWith("This filename belongs",StringComparison.Ordinal)){entry.status="Choose file";entry.detail=ex.Message;continue;}}
                        string filename=Path.GetFileName(game.backup);
                        string[] named=sources.Where(file=>Regex.IsMatch(Path.GetFileName(file),"^"+Regex.Escape(filename)+@"(?:\.(?:backup|dump|pgdump))?$",RegexOptions.IgnoreCase)).ToArray();
                        string[] owned=sources.Where(file=>owners[file].Length==1 && owners[file][0]==game.database).ToArray();
                        string[] candidates;
                        if(!String.IsNullOrEmpty(game.sha256)){
                            candidates=sources.Where(file=>sourceHash(file)==game.sha256).ToArray();
                            if(candidates.Length==0){entry.status=named.Length>0 || owned.Length>0?"Choose file":"Not found";entry.detail=entry.status=="Choose file"?"Files were found, but their contents differ from the recorded original. Use Add database file to choose one.":null;continue;}
                        }else{
                            candidates=named.Where(file=>owned.Contains(file)).ToArray();
                            if(candidates.Length==0){
                                bool shared=catalog.Count(other=>String.Equals(Path.GetFileName(other.backup),filename,StringComparison.OrdinalIgnoreCase))>1;
                                string date=Regex.Match(game.backup,@"\d{4}-\d{2}-\d{2}").Value;
                                bool uniqueDate=date.Length>0 && catalog.Count(other=>String.Equals(Path.GetFileName(other.backup),filename,StringComparison.OrdinalIgnoreCase) && other.backup.Contains(date))==1;
                                candidates=named.Where(file=>owners[file].Length==0 && (!shared || (uniqueDate && file.Substring(sourceRoot.Length).Split(Path.DirectorySeparatorChar).Contains(date)))).ToArray();
                            }
                            if(candidates.Length==0)candidates=owned;
                            if(candidates.Length==0){entry.status=named.Length>0?"Choose file":"Not found";entry.detail=entry.status=="Choose file"?"This filename matches multiple games or a different game folder. Use Add database file.":null;continue;}
                        }
                        var versions=candidates.GroupBy(sourceHash).ToArray();
                        if(versions.Length!=1){entry.status="Choose file";entry.detail="Different database files match this game. Use Add database file to choose the version you want.";continue;}
                        string selected=versions[0].First(),fingerprint=versions[0].Key;
                        entry.sourceFile=selected;entry.destinationFile=selected;
                        game.sha256=fingerprint;game.databaseFile=selected;changed=true;
                        entry.status="Loaded";
                    }catch(IOException ex){entry.status="Choose file";entry.detail=ex.Message;}catch(UnauthorizedAccessException ex){entry.status="Choose file";entry.detail=ex.Message;}
                }
            }catch(OperationCanceledException){result.canceled=true;foreach(FileLoadEntry entry in result.entries)if(entry.status==null)entry.status="Canceled";}
            result.filesFound=sources.Count;
            if(changed){
                if(!File.ReadAllBytes(DefinitionPath).SequenceEqual(definitionBefore))result.error="DatabaseInfo.json changed during the search. Your edits and original database files were preserved. Reload before searching again.";
                else SaveDefinition();
            }
            string reportRoot=Path.Combine(root,"reports","database-files-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
            PrivateDirectory(reportRoot);LastReportPath=Path.Combine(reportRoot,"file-load-report.json");result.reportPath=LastReportPath;AtomicText(LastReportPath,PrettyJson(result));
            return result;
        }
        public Entry[] Inventory()
        {
            ReloadDefinition();
            ScanFiles();
            return catalog.Select(g => { Entry entry=new Entry {game=g.game,requestedDatabase=g.database,database=g.database,encoding=g.encoding};
                try{entry.backup=FindBackup(g);entry.status=entry.backup==null?"Waiting for backup":"Backup found";}
                catch(BackupChoiceException ex){entry.status="Choose backup";entry.detail=ex.Message;}return entry;}).ToArray();
        }
        public Connection LoadConnection()
        {
            DefinitionConnection stored=SettingsConnection(ReadUserSettings());
            Connection c = new Connection();
            c.server=stored.server;c.port=stored.port;c.username=stored.username;
            if(!String.IsNullOrWhiteSpace(stored.pgBin))c.pgBin=stored.pgBin;
            c.pgBin=DetectTools(c.pgBin);
            return c;
        }
        public static bool ToolsPresent(string folder) {return File.Exists(Path.Combine(folder,"psql.exe")) && File.Exists(Path.Combine(folder,"pg_restore.exe"));}
        public static string DetectTools(string preferred)
        {
            if(ToolsPresent(preferred))return preferred;
            foreach(string parent in new[]{Environment.GetEnvironmentVariable("ProgramFiles(x86)"),Environment.GetEnvironmentVariable("ProgramW6432"),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)})
                if(!String.IsNullOrEmpty(parent)){string folder=Path.Combine(parent,"PostgreSQL","8.3","bin");if(ToolsPresent(folder))return folder;}
            return preferred;
        }
        public void SaveConnection(Connection c)
        {
            var settings=ReadUserSettings();DefinitionConnection stored=SettingsConnection(settings);
            stored.server=c.server;stored.port=c.port;stored.username=c.username;stored.pgBin=c.pgBin;
            ValidateStoredConnection(stored);settings["connection"]=stored;SaveUserSettings(settings);
        }
        public void SavePassword(string password)
        {
            var settings=ReadUserSettings();DefinitionConnection stored=SettingsConnection(settings);
            if(stored.password==password && settings.ContainsKey("connection"))return;
            stored.password=password;settings["connection"]=stored;SaveUserSettings(settings);
        }
        public string InfoField(string field)
        {
            DefinitionConnection stored=SettingsConnection(ReadUserSettings());
            if(field.Equals("Password",StringComparison.OrdinalIgnoreCase))return stored.password;
            if(field.Equals("Username",StringComparison.OrdinalIgnoreCase))return stored.username;
            return null;
        }
        static void ValidateConnection(Connection c)
        {
            if (c.server != "127.0.0.1" && c.server != "localhost" && c.server != "::1") throw new Exception("Choose a local PostgreSQL address: 127.0.0.1, localhost, or ::1.");
            if (c.port < 1 || c.port > 65535 || String.IsNullOrWhiteSpace(c.username)) throw new Exception("Check the PostgreSQL port and username.");
            foreach (string tool in new[] { "psql.exe", "pg_restore.exe" })
                if (!File.Exists(Path.Combine(c.pgBin, tool))) throw new Exception("PostgreSQL 8.3 tools were not found. Select your installed PostgreSQL 8.3 bin folder.");
        }
        static string QI(string value) { return "\"" + value.Replace("\"", "\"\"") + "\""; }
        static string QL(string value) { return "'" + value.Replace("'", "''") + "'"; }
        static string Arg(string value)
        {
            // Standard Windows command line quoting, including trailing backslashes.
            StringBuilder b = new StringBuilder("\""); int slashes=0;
            foreach (char ch in value) {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') { b.Append('\\', slashes*2+1); b.Append(ch); }
                else { b.Append('\\', slashes); b.Append(ch); }
                slashes=0;
            }
            b.Append('\\', slashes*2); b.Append('"'); return b.ToString();
        }
        static ToolResult RunTool(Connection c, string password, string tool, IEnumerable<string> args, int timeout)
        {
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(c.pgBin, tool), String.Join(" ", args.Select(Arg)));
            start.UseShellExecute=false; start.CreateNoWindow=true; start.RedirectStandardOutput=true; start.RedirectStandardError=true;
            start.WorkingDirectory=c.pgBin;
            foreach (string name in new[] { "PGSERVICE", "PGOPTIONS", "PGHOST", "PGPORT", "PGDATABASE", "PGUSER", "PGPASSFILE", "PSQLRC", "PAGER", "PSQL_PAGER" }) start.EnvironmentVariables.Remove(name);
            start.EnvironmentVariables["PGPASSWORD"]=password;
            start.EnvironmentVariables["PGCONNECT_TIMEOUT"]="10";
            start.EnvironmentVariables["PGCLIENTENCODING"]="UTF8";
            using (Process process = Process.Start(start))
            {
                Task<string> output=process.StandardOutput.ReadToEndAsync();
                Task<string> errors=process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeout)) { process.Kill(); process.WaitForExit(); throw new Exception("PostgreSQL took too long. Check the saved report before retrying."); }
                Task.WaitAll(output,errors);
                return new ToolResult { code=process.ExitCode, output=output.Result, error=errors.Result };
            }
        }
        static string[] Common(Connection c) { return new[] { "-h",c.server,"-p",c.port.ToString(CultureInfo.InvariantCulture),"-U",c.username }; }
        static int[] TableCounts(Connection c,string password,string database)
        {
            string[] counts=Sql(c,password,database,"SELECT COALESCE(sum(CASE WHEN n.nspname='public' THEN 1 ELSE 0 END),0)||'|'||count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind='r' AND substring(n.nspname from 1 for 3)<>'pg_' AND n.nspname<>'information_schema';").Single().Split('|');
            return counts.Select(Int32.Parse).ToArray();
        }
        static string[] Sql(Connection c, string password, string database, string sql)
        {
            ToolResult r=RunTool(c,password,"psql.exe",new[] {"-X","-q","-A","-t","-P","pager=off"}.Concat(Common(c)).Concat(new[] {"-d",database,"-v","ON_ERROR_STOP=1","-c",sql}),30000);
            if (r.code != 0 || Regex.IsMatch(r.error, @"ERROR:|FATAL:|is not recognized as",RegexOptions.IgnoreCase))
            {
                if(r.error.Contains("password authentication failed"))throw new Exception("PostgreSQL rejected the password. Enter the DATABASE superuser password from installation, rather than the Windows service-account password.");
                if(Regex.IsMatch(r.error,"could not connect|connection refused",RegexOptions.IgnoreCase))throw new Exception("Cannot reach PostgreSQL. Finish installation and reboot, or start the PostgreSQL 8.3 service in Windows Services. Then check the address and port and try again.");
                string detail=r.error.Trim();if(detail.Length>2000)detail=detail.Substring(0,2000);
                throw new Exception("Could not complete the database command. "+detail);
            }
            return r.output.Split(new[] {'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(s=>s.Trim()).Where(s=>s.Length>0).ToArray();
        }
        public static string[] InstalledDatabases(Connection connection,string password)
        {
            ValidateConnection(connection);
            if(String.IsNullOrEmpty(password))throw new Exception("Enter your database password, then click Reload to check installed databases.");
            string version=Sql(connection,password,"postgres","SHOW server_version;").Single();
            if(!version.StartsWith("8.3.",StringComparison.Ordinal) && version!="8.3")throw new Exception("Select your PostgreSQL 8.3 server to check installed game databases.");
            return Sql(connection,password,"postgres","SELECT datname FROM pg_database ORDER BY datname;");
        }
        public static SetupVerification VerifySetup(Connection connection,string password)
        {
            ValidateConnection(connection);
            if(String.IsNullOrEmpty(password))throw new Exception("Enter the DATABASE password from Initialize database cluster, then check the connection again.");
            string[] settings=Sql(connection,password,"postgres","SELECT current_setting('server_version')||'|'||current_setting('server_encoding')||'|'||current_setting('lc_collate')||'|'||current_setting('lc_ctype');").Single().Split('|');
            if(settings.Length!=4)throw new Exception("Could not read the PostgreSQL setup settings.");
            if(!settings[0].StartsWith("8.3.",StringComparison.Ordinal) && settings[0]!="8.3")throw new Exception("Connected to PostgreSQL "+settings[0]+". Select the PostgreSQL 8.3 server for these games; no databases were changed.");
            return new SetupVerification {serverVersion=settings[0],encoding=settings[1],collation=settings[2],characterType=settings[3],guideDefaultsMatch=settings[1]=="UTF8" && settings[2]=="C" && settings[3]=="C"};
        }
        public Entry[] CheckInstalledGames(Connection connection,string password,Entry[] entries)
        {
            string[] installed=InstalledDatabases(connection,password);
            foreach(Entry entry in entries){
                string actual=installed.FirstOrDefault(name=>String.Equals(name,entry.requestedDatabase,StringComparison.OrdinalIgnoreCase));
                entry.databaseInstalled=actual!=null;if(actual==null)continue;entry.database=actual;
                Game game=catalog.Single(item=>item.database==entry.requestedDatabase);var problems=new List<string>();var warnings=new List<string>();string oid=null;
                try{
                    string[] metadata=Sql(connection,password,"postgres","SELECT oid||'|'||pg_encoding_to_char(encoding) FROM pg_database WHERE datname="+QL(actual)+";").Single().Split('|');oid=metadata[0];
                    if(metadata[1]!=game.encoding)warnings.Add("Installed encoding is "+metadata[1]+"; new installs from the supplied definition use "+game.encoding+". The existing database was preserved.");
                    int[] tables=TableCounts(connection,password,actual);entry.publicTables=tables[0];entry.gameTables=tables[1];
                    if(entry.gameTables==0)problems.Add("The database contains no game tables.");
                    // Working manual installs can omit these foreign keys. Verify
                    // constraints during our restores, rather than treating their
                    // absence as evidence that an existing game cannot work.
                }catch(Exception ex){warnings.Add("The installed database could not be verified. "+ex.Message);}
                Entry prior=oid==null?null:LatestRestore(connection,entry.requestedDatabase,actual,oid);
                if(prior!=null && prior.status!="Imported" && prior.status!="Clean installed")problems.Add("The latest restore into this database did not complete successfully. "+prior.detail);
                entry.databaseNeedsRepair=problems.Count>0;entry.databaseHealthDetail=String.Join("\r\n",problems);
                entry.databaseCheckWarning=String.Join("\r\n",warnings);
                entry.installVerified=prior!=null && (prior.status=="Imported" || prior.status=="Clean installed") && problems.Count==0 && warnings.Count==0;
            }
            return entries;
        }
        static string ServerKey(string server) {return new[]{"127.0.0.1","localhost","::1"}.Contains(server)?"local":server;}
        List<InstallRecord> ReadInstallHistory()
        {
            try{return Json.Deserialize<List<InstallRecord>>(File.ReadAllText(Path.Combine(root,"InstallHistory.json")))??new List<InstallRecord>();}
            catch(IOException){return new List<InstallRecord>();}catch(UnauthorizedAccessException){return new List<InstallRecord>();}catch(ArgumentException){return new List<InstallRecord>();}catch(InvalidOperationException){return new List<InstallRecord>();}
        }
        void RecordInstallation(Connection connection,Entry entry)
        {
            var history=ReadInstallHistory();
            history.RemoveAll(record=>record!=null && ServerKey(record.server)==ServerKey(connection.server) && record.port==connection.port && String.Equals(record.database,entry.database,StringComparison.OrdinalIgnoreCase));
            history.Add(new InstallRecord {server=connection.server,port=connection.port,database=entry.database,databaseOid=entry.restoreDatabaseOid,status=entry.status,detail=entry.detail,reportPath=LastReportPath});
            AtomicText(Path.Combine(root,"InstallHistory.json"),PrettyJson(history));
        }
        Entry LatestRestore(Connection connection,string requested,string actual,string oid)
        {
            InstallRecord saved=ReadInstallHistory().LastOrDefault(record=>record!=null && ServerKey(record.server)==ServerKey(connection.server) && record.port==connection.port && String.Equals(record.database,actual,StringComparison.OrdinalIgnoreCase) && record.databaseOid==oid);
            Func<Entry> savedEntry=()=>{if(saved==null)return null;if(String.IsNullOrEmpty(LastReportPath) && File.Exists(saved.reportPath))LastReportPath=saved.reportPath;return new Entry {status=saved.status,detail=saved.detail};};
            string reports=Path.Combine(root,"reports");if(!Directory.Exists(reports))return savedEntry();
            var files=new List<string>();
            foreach(string directory in Directory.GetDirectories(reports)){
                if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)continue;
                string path=Path.Combine(directory,"setup-report.json");if(File.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0)files.Add(path);
            }
            foreach(string file in files.OrderByDescending(File.GetLastWriteTimeUtc)){
                try{
                    Report report=Json.Deserialize<Report>(File.ReadAllText(file));if(report==null || report.plan || ServerKey(report.server)!=ServerKey(connection.server) || report.port!=connection.port || report.entries==null)continue;
                    Entry entry=report.entries.LastOrDefault(item=>item!=null && item.restoreDatabaseOid==oid && String.Equals(item.requestedDatabase,requested,StringComparison.OrdinalIgnoreCase) && (item.status=="Imported" || item.status=="Imported; review errors" || item.status=="Clean installed" || item.status=="Needs review") && (item.status=="Clean installed" || String.Equals(item.restoreDatabase,actual,StringComparison.OrdinalIgnoreCase)));
                    if(entry!=null){if(String.IsNullOrEmpty(LastReportPath))LastReportPath=file;return entry;}
                }catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(InvalidOperationException){}
            }
            return savedEntry();
        }
        static string Hash(string path)
        {
            using (var sha=SHA256.Create()) using (var f=File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant();
        }
        static void PrivateDirectory(string path)
        {
            Directory.CreateDirectory(path);
            DirectorySecurity access=new DirectorySecurity();
            access.SetAccessRuleProtection(true,false);
            access.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(access);
        }
        void SaveReport(Report report)
        {
            AtomicText(LastReportPath,Json.Serialize(report));
        }
        static void AtomicText(string path,string content)
        {
            string temp=path+"."+Guid.NewGuid().ToString("N")+".writing";
            File.WriteAllText(temp,content,new UTF8Encoding(false));
            try{
                for(int attempt=0;;attempt++){
                    try{if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);return;}
                    catch(IOException){if(attempt>=11)throw;Thread.Sleep(100);}
                }
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
        public Report Build(Connection connection,string password,bool plan,Action<Entry> progress,IEnumerable<string> selectedDatabases=null,IEnumerable<string> confirmedOverwrites=null,ConfirmedBackupRestore confirmedRestore=null)
        {
            ReloadDefinition();
            ScanFiles();
            Game[] selected=catalog;
            if(selectedDatabases!=null){string[] names=selectedDatabases.ToArray();if(names.Length==0 || names.Any(n=>!catalog.Any(g=>g.database==n)))throw new Exception("Choose at least one database defined in DatabaseInfo.json.");selected=catalog.Where(g=>names.Contains(g.database)).ToArray();}
            string[] overwrite=confirmedOverwrites==null?new string[0]:confirmedOverwrites.ToArray();
            if(overwrite.Any(name=>!selected.Any(game=>game.database==name)))throw new Exception("Clean installs must be selected and explicitly confirmed.");
            ValidateConnection(connection);
            if (String.IsNullOrEmpty(password)) throw new Exception("Enter the PostgreSQL password or set connection.password in UserSettings.json.");
            string mutexName="Local\\ITDatabaseTool_"+HashText(ServerKey(connection.server)+":"+connection.port.ToString());
            using (Mutex gate=new Mutex(false,mutexName))
            {
                bool owns=false;
                try { try { owns=gate.WaitOne(0); } catch (AbandonedMutexException) { owns=true; }
                    if (!owns) throw new Exception("Another database setup is running. Wait for it to finish.");
                    if(confirmedRestore!=null){
                        ValidateConfirmedRestore(connection,confirmedRestore);
                        if(selected.Length!=1 || selected[0].database!=confirmedRestore.Database || !String.Equals(selected[0].databaseFile,confirmedRestore.Path,StringComparison.OrdinalIgnoreCase) || selected[0].sha256!=confirmedRestore.SHA256)throw new Exception("The confirmed restore source changed. No databases were changed.");
                    }
                    return BuildLocked(connection,password,plan,progress,selected,overwrite);
                } finally { if (owns) gate.ReleaseMutex(); }
            }
        }
        static string HashText(string text)
        {
            using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-","").Substring(0,24);
        }
        public Report BackupDatabases(Connection connection,string password,IEnumerable<string> selectedDatabases,string existingAction,Action<Entry> progress)
        {
            ReloadDefinition();
            string[] names=selectedDatabases==null?new string[0]:selectedDatabases.Distinct().ToArray();
            if(names.Length==0 || names.Any(name=>!catalog.Any(game=>game.database==name)))throw new Exception("Check the installed games you want to back up.");
            if(names.Any(name=>new[]{"postgres","template0","template1"}.Contains(name.ToLowerInvariant())))throw new Exception("Choose game databases rather than PostgreSQL system databases.");
            if(existingAction!="ask" && existingAction!="keep" && existingAction!="overwrite")throw new Exception("Choose Keep versions or Overwrite latest for existing backups.");
            ValidateConnection(connection);
            if(String.IsNullOrEmpty(password))throw new Exception("Enter your PostgreSQL password.");
            if(!File.Exists(Path.Combine(connection.pgBin,"pg_dump.exe")))throw new Exception("pg_dump.exe is missing. Select the PostgreSQL 8.3 bin folder to back up databases.");
            using(var gate=new Mutex(false,"Local\\ITDatabaseTool_"+HashText(ServerKey(connection.server)+":"+connection.port.ToString()))){
                bool owns=false;
                try{
                    try{owns=gate.WaitOne(0);}catch(AbandonedMutexException){owns=true;}
                    if(!owns)throw new Exception("Another database operation is running. Wait for it to finish.");
                    string[] installed=InstalledDatabases(connection,password);
                    ToolResult version=RunTool(connection,password,"pg_dump.exe",new[]{"--version"},30000);
                    if(version.code!=0 || !Regex.IsMatch(version.output,@"\b8\.3(?:\.|\s|$)"))throw new Exception("Use PostgreSQL 8.3 pg_dump.exe to back up these databases.");
                    // CLI defaults to asking as well: never silently decide what to do
                    // with an existing backup. UI supplies the user's explicit choice.
                    if(existingAction=="ask" && names.Any(name=>LatestBackup(connection,name)!=null))throw new Exception("A backup already exists. Choose --backup-existing keep or --backup-existing overwrite, or use Backup in the app.");
                    EnsureNormalFolder(BackupDirectory);
                    string reportRoot=Path.Combine(root,"reports","backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
                    PrivateDirectory(reportRoot);LastReportPath=Path.Combine(reportRoot,"backup-report.json");
                    var report=new Report {server=connection.server,port=connection.port,serverVersion="8.3"};
                    SaveReport(report);
                    DateTime backupDate=DateTime.Now;
                    foreach(Game game in catalog.Where(item=>names.Contains(item.database))){
                        var entry=new Entry {game=game.game,requestedDatabase=game.database,database=game.database,status="Backing up"};
                        report.entries.Add(entry);SaveReport(report);if(progress!=null)progress(entry);
                        string partial=null;bool published=false;
                        try{
                            string[] matches=installed.Where(name=>String.Equals(name,game.database,StringComparison.OrdinalIgnoreCase)).ToArray();
                            if(matches.Length==0){entry.status="Not installed";entry.detail="No installed database to back up.";continue;}
                            if(matches.Length!=1)throw new Exception("Multiple database spellings exist. Choose a unique database before backing up.");
                            entry.database=matches[0];entry.databaseInstalled=true;
                            string[] metadata=Sql(connection,password,"postgres","SELECT oid||'|'||pg_encoding_to_char(encoding) FROM pg_database WHERE datname="+QL(entry.database)+";").Single().Split('|');
                            entry.encoding=metadata[1];
                            DatabaseBackup previous=LatestBackup(connection,game.database);
                            bool overwrite=existingAction=="overwrite" && previous!=null;
                            string relative=BackupRelativePath(game,backupDate);
                            string stem=relative.Substring(0,relative.Length-"_DBbackup".Length);
                            bool replaceDestination=overwrite && (String.Equals(previous.file,relative,StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(previous.file,"^"+Regex.Escape(stem)+@"_(?:[2-9]|[1-9][0-9]+)_DBbackup$",RegexOptions.IgnoreCase));
                            if(replaceDestination)relative=previous.file;
                            else{
                                var reserved=ReadBackupRecords(ReadUserSettings());
                                for(int versionNumber=1;File.Exists(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar))) || Directory.Exists(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar))) || reserved.Any(item=>String.Equals(item.file,relative,StringComparison.OrdinalIgnoreCase));versionNumber++)relative=BackupRelativePath(game,backupDate,versionNumber+1);
                            }
                            string destination=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));
                            EnsureBackupParents(relative);
                            string folder=Path.GetDirectoryName(destination);
                            string previousPath=overwrite?BackupRecordPath(previous):null;
                            Action checkPrevious=()=>{
                                if(!overwrite)return;
                                EnsureBackupParents(previous.file);
                                if(!File.Exists(previousPath) || (File.GetAttributes(previousPath)&FileAttributes.ReparsePoint)!=0 || Hash(previousPath)!=previous.sha256)throw new Exception("The latest backup file changed, disappeared, or became a link. Keep versions to save a new backup.");
                                if(catalog.Any(item=>!String.IsNullOrEmpty(item.databaseFile) && String.Equals(Path.GetFullPath(item.databaseFile),previousPath,StringComparison.OrdinalIgnoreCase)))throw new Exception("The latest backup is selected as a restore source. Keep versions to preserve that file association.");
                            };
                            Action checkDestination=()=>{
                                EnsureBackupParents(relative);
                                checkPrevious();
                                if(File.Exists(destination) && (File.GetAttributes(destination)&FileAttributes.ReparsePoint)!=0)throw new Exception("Use a normal backup file rather than a link.");
                                if(!replaceDestination && (File.Exists(destination) || Directory.Exists(destination)))throw new Exception("A file already exists at the new backup location. Retry to use another filename.");
                            };
                            checkDestination();
                            partial=Path.Combine(folder,"."+Guid.NewGuid().ToString("N")+".partial");
                            // SQL_ASCII game data can contain arbitrary legacy bytes.
                            // Override the runner's UTF8 default with the source encoding.
                            ToolResult dump=RunTool(connection,password,"pg_dump.exe",Common(connection).Concat(new[]{"-F","c","-E",metadata[1],"-f",partial,entry.database}),600000);
                            if(dump.code!=0)throw new Exception("Database backup failed. "+dump.error.Trim());
                            if(!File.Exists(partial) || new FileInfo(partial).Length==0)throw new Exception("PostgreSQL did not produce a backup file.");
                            ToolResult verify=RunTool(connection,password,"pg_restore.exe",new[]{"--file", "NUL",partial},600000);
                            if(verify.code!=0)throw new Exception("The backup archive could not be read completely. "+verify.error.Trim());
                            string[] current=Sql(connection,password,"postgres","SELECT oid FROM pg_database WHERE datname="+QL(entry.database)+";");
                            if(current.Length!=1 || current[0]!=metadata[0])throw new Exception("The database changed during backup. Retry after other database operations finish.");
                            var record=new DatabaseBackup {kind="export",game=game.game,requestedDatabase=game.database,database=entry.database,databaseOid=metadata[0],server=connection.server,port=connection.port,encoding=metadata[1],createdUtc=DateTimeOffset.UtcNow.ToString("o"),file=relative,sha256=Hash(partial),bytes=new FileInfo(partial).Length};
                            // Re-read settings after the dump to preserve external preference edits.
                            var settings=ReadUserSettings();var records=ReadBackupRecords(settings);
                            checkDestination();
                            if(replaceDestination)File.Replace(partial,destination,null);else File.Move(partial,destination);
                            partial=null;published=true;entry.backup=destination;entry.backupSHA256=record.sha256;
                            records.RemoveAll(item=>String.Equals(item.file,relative,StringComparison.OrdinalIgnoreCase));records.Add(record);
                            settings["backups"]=records;SaveUserSettings(settings);
                            entry.status="Backed up";entry.detail=(overwrite?"Replaced the latest backup. ":"Saved a new backup version. ")+"Use Add database file to select this backup for a later restore. "+destination;
                            if(overwrite && !replaceDestination){
                                try{
                                    // Keep the prior file until the new dated backup and its
                                    // history are safely published. Never delete a changed file.
                                    checkPrevious();File.Delete(previousPath);
                                    var cleanupSettings=ReadUserSettings();var cleanupRecords=ReadBackupRecords(cleanupSettings);
                                    cleanupRecords.RemoveAll(item=>String.Equals(item.file,previous.file,StringComparison.OrdinalIgnoreCase) && item.sha256==previous.sha256);
                                    cleanupSettings["backups"]=cleanupRecords;SaveUserSettings(cleanupSettings);
                                }catch(Exception cleanup){entry.detail+=" The new backup is available, but cleanup of the previous version was incomplete: "+cleanup.Message;}
                            }
                            if(!String.IsNullOrWhiteSpace(dump.error))entry.detail+=" PostgreSQL warning: "+dump.error.Trim();
                        }catch(Exception ex){
                            entry.status="Needs review";
                            entry.detail=(published?"The verified backup was saved at "+entry.backup+", but its history could not be saved. Keep this file. ":"No new backup was saved; existing backups were kept. ")+ex.Message.Replace(password,"[redacted]");
                        }finally{
                            if(partial!=null && File.Exists(partial))File.Delete(partial);
                            SaveReport(report);if(progress!=null)progress(entry);
                        }
                    }
                    report.completed=true;SaveReport(report);return report;
                }finally{if(owns)gate.ReleaseMutex();}
            }
        }
        public Report RemoveDatabases(Connection connection,string password,IEnumerable<string> selectedDatabases,bool confirmed,Action<Entry> progress,bool plan=false)
        {
            ReloadDefinition();string[] names=selectedDatabases==null?new string[0]:selectedDatabases.Distinct().ToArray();
            if(names.Length==0 || names.Any(name=>!catalog.Any(game=>game.database==name)))throw new Exception("Choose databases defined in DatabaseInfo.json.");
            if(names.Any(name=>new[]{"postgres","template0","template1"}.Contains(name.ToLowerInvariant())))throw new Exception("System databases cannot be removed.");
            if(!plan && !confirmed)throw new Exception("Database removal requires explicit confirmation.");
            ValidateConnection(connection);if(String.IsNullOrEmpty(password))throw new Exception("Enter the PostgreSQL password.");
            using(var gate=new Mutex(false,"Local\\ITDatabaseTool_"+HashText(ServerKey(connection.server)+":"+connection.port.ToString()))){
                bool owns=false;
                try{
                    try{owns=gate.WaitOne(0);}catch(AbandonedMutexException){owns=true;}
                    if(!owns)throw new Exception("Another database setup is running. Wait for it to finish.");
                    string reportRoot=Path.Combine(root,"reports",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-remove-"+Guid.NewGuid().ToString("N").Substring(0,8));PrivateDirectory(reportRoot);LastReportPath=Path.Combine(reportRoot,"setup-report.json");
                    Report report=new Report {server=connection.server,port=connection.port,plan=plan};
                    try{
                        string[] installed=InstalledDatabases(connection,password);report.serverVersion="8.3";
                        foreach(Game game in catalog.Where(item=>names.Contains(item.database))){
                            var entry=new Entry {game=game.game,requestedDatabase=game.database,database=game.database,status="Checking"};report.entries.Add(entry);SaveReport(report);
                            string[] matches=installed.Where(name=>String.Equals(name,game.database,StringComparison.OrdinalIgnoreCase)).ToArray();
                            if(matches.Length==0){entry.status="Already absent";entry.detail="Nothing to remove.";}
                            else if(matches.Length>1){entry.status="Needs review";entry.detail="Multiple database spellings exist; none were removed.";}
                            else{
                                entry.database=matches[0];entry.databaseInstalled=true;
                                if(plan){entry.status="Would remove";entry.detail="Preview only; no database changes.";}
                                else{
                                    string oid=Sql(connection,password,"postgres","SELECT oid FROM pg_database WHERE datname="+QL(entry.database)+";").Single();
                                    int clients=Int32.Parse(Sql(connection,password,"postgres","SELECT count(*) FROM pg_stat_activity WHERE datname="+QL(entry.database)+";").Single());
                                    if(clients>0){entry.status="Needs review";entry.detail="Database is in use. Close the game and pgAdmin connections, then retry. Nothing was removed.";}
                                    else{
                                        string[] current=Sql(connection,password,"postgres","SELECT oid FROM pg_database WHERE datname="+QL(entry.database)+";");
                                        if(current.Length!=1 || current[0]!=oid){entry.status="Needs review";entry.detail="Database changed during the check; nothing was removed.";}
                                        else try{Sql(connection,password,"postgres","DROP DATABASE "+QI(entry.database)+";");entry.databaseInstalled=false;entry.status="Removed";entry.detail="Removed the installed database. Original database files and any recovery copies were kept.";}
                                        catch(Exception ex){entry.status="Needs review";entry.detail="Could not remove the database. "+ex.Message;}
                                    }
                                }
                            }
                            SaveReport(report);progress(entry);
                        }
                        report.completed=true;SaveReport(report);return report;
                    }catch(Exception ex){report.error=ex.Message;SaveReport(report);throw;}
                }finally{if(owns)gate.ReleaseMutex();}
            }
        }
        static string TemporaryDatabaseName(string database,string purpose) {return database.Substring(0,Math.Min(database.Length,40))+"_"+purpose+"_"+Guid.NewGuid().ToString("N").Substring(0,8);}
        static bool HasITCategoryLayout(string listing)
        {
            // Recognize the actual archive's ten public category tables and five
            // foreign keys. Game names and years do not select the migration.
            for(int category=1;category<=5;category++){
                foreach(string suffix in new[]{"object","attr"})
                    if(!Regex.IsMatch(listing,@"(?m)^\s*\d+;\s+\d+\s+\d+\s+TABLE\s+public\s+category"+category+"_"+suffix+@"\s"))return false;
                string key="category"+category+(category<=3?"_attr_fk":"_fk");
                if(!Regex.IsMatch(listing,@"(?m)^\s*\d+;\s+\d+\s+\d+\s+(?:FK )?CONSTRAINT\s+(?:public\s+)?"+key+@"\s"))return false;
            }
            return true;
        }
        static ToolResult RestoreLegacyIT(Connection connection,string password,string target,string archive,string listing,string reportFolder,string database,bool requireMigration)
        {
            // The old IT dumps defer constraints until their integer object keys
            // have been widened to match the numeric(38,0) attribute keys.
            string[] entries=listing.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Where(line=>Regex.IsMatch(line,@"^\s*\d+;")).ToArray();
            Func<string,bool> deferred=line=>Regex.IsMatch(line,@"^\s*\d+;\s+\d+\s+\d+\s+(?:CONSTRAINT|FK CONSTRAINT|CHECK CONSTRAINT|INDEX|TRIGGER)\b");
            string baseList=Path.Combine(reportFolder,database+"-base.list"),finalList=Path.Combine(reportFolder,database+"-constraints.list");
            File.WriteAllLines(baseList,entries.Where(line=>!deferred(line)),new UTF8Encoding(false));
            File.WriteAllLines(finalList,entries.Where(deferred),new UTF8Encoding(false));
            var output=new StringBuilder();var errors=new StringBuilder();
            string[] common=Common(connection).Concat(new[]{"-d",target,"--no-owner","--no-acl","--exit-on-error","--single-transaction"}).ToArray();
            Func<string,string,ToolResult> phase=(label,list)=>{
                ToolResult result=RunTool(connection,password,"pg_restore.exe",common.Concat(new[]{"--use-list="+list,archive}),600000);
                output.AppendLine("--- "+label+" ---").AppendLine(result.output);if(result.error.Length>0)errors.AppendLine("--- "+label+" ---").AppendLine(result.error);
                return result;
            };
            try{
                ToolResult first=phase("Schema and data",baseList);
                if(first.code!=0 || Regex.IsMatch(first.error,@"ERROR:|FATAL:",RegexOptions.IgnoreCase))return new ToolResult {code=first.code==0?1:first.code,output=output.ToString(),error=errors.ToString()};
                string columns=" FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_type t ON t.oid=a.atttypid WHERE n.nspname='public' AND c.relkind='r' AND a.attname='object_id' AND NOT a.attisdropped AND ";
                string objects=Sql(connection,password,target,"SELECT count(*)"+columns+"c.relname ~ '^category[1-5]_object$' AND t.typname='int4';").Single();
                string attributes=Sql(connection,password,target,"SELECT count(*)"+columns+"c.relname ~ '^category[1-5]_attr$' AND t.typname='numeric' AND a.atttypmod=2490372;").Single();
                bool migrate=objects=="5" && attributes=="5";
                if(requireMigration && !migrate)throw new Exception("Legacy IT compatibility could not be applied: the column layout differs from the verified layout. No column changes were made.");
                if(migrate){
                    string migration="BEGIN;"+String.Concat(Enumerable.Range(1,5).Select(category=>"ALTER TABLE ONLY public.category"+category+"_object ALTER COLUMN object_id TYPE numeric(38,0) USING object_id::numeric(38,0);"))+"COMMIT;";
                    Sql(connection,password,target,migration);output.AppendLine("Verified legacy IT object keys widened to numeric(38,0); original data and sequence defaults retained.");
                }else output.AppendLine("IT category tables detected; the verified integer/numeric mismatch is absent. Original column types retained.");
                ToolResult last=phase("Constraints, indexes and triggers",finalList);
                if(last.code!=0 || Regex.IsMatch(last.error,@"ERROR:|FATAL:",RegexOptions.IgnoreCase))return new ToolResult {code=last.code==0?1:last.code,output=output.ToString(),error=errors.ToString()};
                if(migrate && Sql(connection,password,target,"SELECT count(*)"+columns+"c.relname ~ '^category[1-5]_object$' AND t.typname='numeric' AND a.atttypmod=2490372 AND a.atthasdef;").Single()!="5")throw new Exception("Legacy IT key verification failed.");
                if(Sql(connection,password,target,"SELECT count(*) FROM pg_constraint con JOIN pg_class c ON c.oid=con.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND con.contype='f' AND con.conname IN ('category1_attr_fk','category2_attr_fk','category3_attr_fk','category4_fk','category5_fk');").Single()!="5")throw new Exception("Legacy IT foreign key verification failed.");
                return new ToolResult {code=0,output=output.ToString(),error=errors.ToString()};
            }catch(Exception ex){errors.AppendLine("ERROR: "+ex.Message);return new ToolResult {code=1,output=output.ToString(),error=errors.ToString()};}
        }
        Report BuildLocked(Connection c,string password,bool plan,Action<Entry> progress,Game[] selected,string[] overwrite)
        {
            string runRoot=Path.Combine(root,"reports",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
            PrivateDirectory(runRoot);
            LastReportPath=Path.Combine(runRoot,"setup-report.json");
            Report report=new Report {server=c.server,port=c.port,plan=plan};
            try
            {
                report.serverVersion=Sql(c,password,"postgres","SHOW server_version;").Single();
                if (!report.serverVersion.StartsWith("8.3.",StringComparison.Ordinal) && report.serverVersion!="8.3") throw new Exception("This tool requires an installed PostgreSQL 8.3 server. No databases were changed.");
                string[] existing=Sql(c,password,"postgres","SELECT datname FROM pg_database ORDER BY datname;");
                foreach(Game g in selected)
                {
                    Entry entry=new Entry {game=g.game,requestedDatabase=g.database,database=g.database,encoding=g.encoding,status="Checking"};
                    report.entries.Add(entry); SaveReport(report);
                    bool activated=false;
                    try{
                    string[] matches=existing.Where(n=>String.Equals(n,g.database,StringComparison.OrdinalIgnoreCase)).ToArray();
                    bool replacing=matches.Length>0 && overwrite.Contains(g.database);
                    if(matches.Length>0)
                    {
                        entry.database=matches[0];entry.databaseInstalled=true; entry.status="Already present";
                        entry.detail=matches.Length>1 ? "Multiple case variants exist; preserved all of them." : "Preserved. Use this database spelling in your game settings.";
                        if(!replacing || matches.Length>1){SaveReport(report); progress(entry); continue;}
                        if(new[]{"postgres","template0","template1"}.Contains(entry.database.ToLowerInvariant()))throw new Exception("System databases cannot be overwritten.");
                    }
                    try{entry.backup=FindBackup(g);}catch(BackupChoiceException ex){entry.status="Choose backup";entry.detail=ex.Message;SaveReport(report);progress(entry);continue;}
                    if(entry.backup==null) { entry.status="Waiting for backup"; entry.detail="Put the database file anywhere inside PG_backup, or select this game and click Add database file."; SaveReport(report); progress(entry); continue; }
                    entry.backupSHA256=Hash(entry.backup);
                    DatabaseBackup savedBackup=ReadBackupRecords(ReadUserSettings()).LastOrDefault(item=>item.kind!="initial" && item.requestedDatabase==g.database && item.sha256==entry.backupSHA256);
                    if(savedBackup!=null)entry.encoding=savedBackup.encoding;
                    if(!String.IsNullOrEmpty(g.sha256) && entry.backupSHA256!=g.sha256){entry.status="Backup needs review";entry.detail="The database file changed since it was identified. Reload and try again.";SaveReport(report);progress(entry);continue;}
                    // Validate archive metadata before creating a database.
                    ToolResult toc=RunTool(c,password,"pg_restore.exe",new[]{"-l",entry.backup},30000);
                    string tocLog=Path.Combine(runRoot,g.database+"-archive.log");
                    File.WriteAllText(tocLog,toc.output+"\r\n"+toc.error);
                    if(toc.code!=0 || !Regex.IsMatch(toc.output,@"(?m)^\s*\d+;.*\bTABLE DATA\b"))
                    { entry.status="Backup needs review"; entry.detail="The database file could not be read, or contains no table data."; SaveReport(report); progress(entry); continue; }
                    if(plan) { entry.status=replacing?"Would clean install":"Would build"; entry.detail="Database file validated; preview only."; SaveReport(report); progress(entry); continue; }
                    // Check immediately before creation too, so a concurrently created
                    // database cannot be selected as a restore destination.
                    if(!replacing && Sql(c,password,"postgres","SELECT datname FROM pg_database WHERE lower(datname)=lower("+QL(g.database)+");").Length>0)
                    { entry.status="Already present"; entry.detail="Created elsewhere during setup; preserved."; SaveReport(report); progress(entry); continue; }
                    string originalOid=replacing?Sql(c,password,"postgres","SELECT oid FROM pg_database WHERE datname="+QL(entry.database)+";").Single():null;
                    // Publish only a complete, validated restore. Failed or
                    // interrupted new installs never occupy the game's DB name.
                    string target=TemporaryDatabaseName(entry.database,"restore");
                    entry.restoreDatabase=target;
                    Sql(c,password,"postgres","CREATE DATABASE "+QI(target)+" WITH OWNER="+QI(c.username)+" TEMPLATE=template0 ENCODING="+QL(entry.encoding)+" TABLESPACE=pg_default;");
                    entry.restoreDatabaseOid=Sql(c,password,"postgres","SELECT oid FROM pg_database WHERE datname="+QL(target)+";").Single();
                    entry.status="Created; restore pending"; SaveReport(report); progress(entry);
                    Sql(c,password,"postgres","ALTER DATABASE "+QI(target)+" SET standard_conforming_strings=on;");
                    entry.status="Restoring"; SaveReport(report); progress(entry);
                    bool explicitProfile=g.restoreProfile=="it-category-keys-v1";
                    // Our verified exports already contain the installed schema.
                    // Restore it as-is, including compatible keys or missing optional
                    // constraints in manual installs, rather than reapplying a profile
                    // intended for the original distributed game archive.
                    bool userBackup=savedBackup!=null;
                    ToolResult restored=!userBackup && (explicitProfile || HasITCategoryLayout(toc.output))?RestoreLegacyIT(c,password,target,entry.backup,toc.output,runRoot,g.database,explicitProfile):RunTool(c,password,"pg_restore.exe",Common(c).Concat(new[]{"-d",target,"--no-owner","--no-acl","--exit-on-error","--single-transaction",entry.backup}),600000);
                    entry.restoreExitCode=restored.code;
                    entry.restoreLog=Path.Combine(runRoot,g.database+"-restore.log");
                    File.WriteAllText(entry.restoreLog,restored.output+"\r\n--- ERRORS / WARNINGS ---\r\n"+restored.error);
                    Sql(c,password,"postgres","ALTER DATABASE "+QI(target)+" SET standard_conforming_strings=on;");
                    int[] tables=TableCounts(c,password,target);entry.publicTables=tables[0];entry.gameTables=tables[1];
                    string[] settings=Sql(c,password,"postgres","SELECT pg_encoding_to_char(encoding)||'|'||pg_get_userbyid(datdba)||'|'||array_to_string(datconfig,',') FROM pg_database WHERE datname="+QL(target)+";");
                    entry.settingsVerified=settings.Length==1 && settings[0]==entry.encoding+"|"+c.username+"|standard_conforming_strings=on";
                    bool preserved=Hash(entry.backup)==entry.backupSHA256;
                    int expectedTables=Regex.Matches(toc.output,@"(?m)^\s*\d+;\s+\d+\s+\d+\s+TABLE\s+(?!DATA\s|pg_|information_schema\s)\S+\s").Count;
                    entry.expectedTables=expectedTables;entry.sourceUnchanged=preserved;
                    bool errors=restored.code!=0 || Regex.IsMatch(restored.error,@"ERROR:|FATAL:",RegexOptions.IgnoreCase);
                    entry.status=errors ? "Imported; review errors" : "Imported";
                    if(!entry.settingsVerified || !preserved || entry.gameTables!=expectedTables) entry.status="Needs review";
                    var issues=new List<string>();
                    if(errors)issues.Add("Restore reported errors (exit code "+restored.code+"); open its log.");
                    if(!entry.settingsVerified)issues.Add("Restored database defaults do not match DatabaseInfo.json.");
                    if(!preserved)issues.Add("The source file changed during the restore.");
                    if(entry.gameTables!=expectedTables)issues.Add("Expected "+expectedTables+" game tables; restored "+entry.gameTables+".");
                    entry.detail=issues.Count>0?String.Join(" ",issues):entry.gameTables+" game tables; defaults and unchanged original file verified.";
                    if(!replacing){
                        if(entry.status!="Imported"){
                            entry.status="Needs review";entry.detail="Install was not activated. "+entry.detail+" The game's database name is still available. Review the temporary restore "+target+" and its report, then retry Install.";
                        }else if(Sql(c,password,"postgres","SELECT datname FROM pg_database WHERE lower(datname)=lower("+QL(entry.database)+");").Length>0){
                            entry.status="Needs review";entry.detail="Another database appeared during the restore. It was preserved; the completed temporary restore is "+target+".";
                        }else{
                            Sql(c,password,"postgres","ALTER DATABASE "+QI(target)+" RENAME TO "+QI(entry.database)+";");activated=true;entry.restoreDatabase=entry.database;entry.databaseInstalled=true;
                        }
                    }else{
                        if(entry.status!="Imported"){
                            entry.status="Needs review";entry.detail="Clean install was not activated. "+entry.detail+" Your original database is unchanged. Review the restore in "+target+" and its report.";
                        }else{
                            string[] current=Sql(c,password,"postgres","SELECT oid FROM pg_database WHERE datname="+QL(entry.database)+";");
                            int connections=Int32.Parse(Sql(c,password,"postgres","SELECT count(*) FROM pg_stat_activity WHERE datname IN ("+QL(entry.database)+","+QL(target)+");").Single());
                            if(current.Length!=1 || current[0]!=originalOid || connections>0){entry.status="Needs review";entry.detail="Clean install was not activated. The original changed or a game/pgAdmin connection is using it. Close those connections, then retry. Restored copy: "+target;}
                            else{
                                string recovery=TemporaryDatabaseName(entry.database,"before");
                                try{
                                    Sql(c,password,"postgres","BEGIN; ALTER DATABASE "+QI(entry.database)+" RENAME TO "+QI(recovery)+"; ALTER DATABASE "+QI(target)+" RENAME TO "+QI(entry.database)+"; COMMIT;");
                                    activated=true;
                                    entry.recoveryDatabase=recovery;entry.databaseInstalled=true;entry.status="Clean installed";entry.detail="Original database file restored. Previous database kept as recovery copy: "+recovery;
                                }catch(Exception ex){entry.status="Needs review";entry.detail="Could not activate the clean install. Original database preserved; restored copy: "+target+". "+ex.Message;}
                            }
                        }
                    }
                    if(entry.status=="Imported" || entry.status=="Clean installed"){
                        try{RecordInstallation(c,entry);}catch(IOException ex){entry.detail+=" Installed successfully, but local installation history could not be saved: "+ex.Message;}catch(UnauthorizedAccessException ex){entry.detail+=" Installed successfully, but local installation history could not be saved: "+ex.Message;}
                    }
                    if(activated && !replacing && entry.status=="Imported"){
                        try{RecordInitialBackup(g,entry,c,password);}catch(Exception ex){entry.initialBackupError=ex.Message.Replace(password,"[redacted]");entry.detail+=" Installed successfully, but the initial backup could not be completed: "+entry.initialBackupError;}
                    }
                    SaveReport(report); progress(entry);
                    }catch(Exception ex){
                        entry.status=activated?(entry.recoveryDatabase==null?"Imported":"Clean installed"):"Needs review";
                        entry.detail=(activated?"Database installed successfully; recording the result failed. ":entry.databaseInstalled?"The existing database was preserved. ":"No incomplete database was activated. ")+ex.Message+(activated || entry.restoreDatabase==null?"":" Temporary restore: "+entry.restoreDatabase);
                        SaveReport(report);progress(entry);
                    }
                }
                report.completed=true; SaveReport(report); return report;
            }
            catch(Exception ex) { report.error=ex.Message; if(report.entries.Count>0){Entry last=report.entries.Last();if(last.status=="Checking"||last.status=="Restoring"||last.status=="Created; restore pending"){last.status="Needs review";last.detail=ex.Message;progress(last);}} SaveReport(report); throw; }
        }
    }

    public sealed class SetupVerification
    {
        public string serverVersion,encoding,collation,characterType;
        public bool guideDefaultsMatch;
    }
    public sealed class InstallerStatus
    {
        public string folder;
        public string[] missingFiles;
        public bool filesPresent;
        public bool toolsPresent;
        public bool secondaryLogonRunning;
        public bool secondaryLogonAutomatic;
        public string prerequisiteDetail;
        public string startFile;
        public string workingDirectory;
        public string elevation;
        public string windowStyle;
    }
    public static class InstallerSupport
    {
        public static readonly string[] RequiredFiles={"SETUP.bat","postgresql-8.3.msi","postgresql-8.3-int.msi","vcredist_x86.exe"};
        public static string Folder(string root){return Path.Combine(Path.GetFullPath(root),"installs","PostgreSQL v8.3");}
        public static InstallerStatus Check(string root,string tools)
        {
            string folder=Folder(root);
            string parent=Path.GetDirectoryName(folder);
            bool linked=(Directory.Exists(folder) && (File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0) || (Directory.Exists(parent) && (File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0);
            string[] missing=RequiredFiles.Where(name=>linked || !File.Exists(Path.Combine(folder,name)) || (File.GetAttributes(Path.Combine(folder,name))&FileAttributes.ReparsePoint)!=0 || new FileInfo(Path.Combine(folder,name)).Length==0).ToArray();
            InstallerStatus status=new InstallerStatus {folder=folder,missingFiles=missing,filesPresent=missing.Length==0,toolsPresent=Engine.ToolsPresent(tools)};
            try{
                using(var service=new ServiceController("seclogon"))status.secondaryLogonRunning=service.Status==ServiceControllerStatus.Running;
                object start=Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\seclogon","Start",null);
                status.secondaryLogonAutomatic=start is int && (int)start==2;
                status.prerequisiteDetail="Secondary Logon: "+(status.secondaryLogonRunning?"running":"stopped")+"; startup type "+(status.secondaryLogonAutomatic?"Automatic":"needs Automatic");
            }catch{status.prerequisiteDetail="Could not check Secondary Logon. Open Services and set it to Automatic, then start it.";}
            if(status.filesPresent){ProcessStartInfo launch=StartInfo(root);status.startFile=launch.FileName;status.workingDirectory=launch.WorkingDirectory;status.elevation=launch.Verb;status.windowStyle=launch.WindowStyle.ToString();}
            return status;
        }
        public static ProcessStartInfo StartInfo(string root)
        {
            string folder=Folder(root);
            return new ProcessStartInfo(Path.Combine(folder,"SETUP.bat")){WorkingDirectory=folder,UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
        }
        public static ProcessStartInfo PrepareWindowsStartInfo()
        {
            string script="$ErrorActionPreference='Stop'; try { Set-Service -Name seclogon -StartupType Automatic; Start-Service -Name seclogon; (Get-Service -Name seclogon).WaitForStatus('Running',[TimeSpan]::FromSeconds(30)); exit 0 } catch { exit 1 }";
            string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
            return new ProcessStartInfo(powershell,"-NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(script))){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
        }
        public static bool NeedsWindowsPreparation(InstallerStatus status){return status!=null && (!status.secondaryLogonRunning || !status.secondaryLogonAutomatic);}
    }
    public sealed class InstallHelpForm : Form
    {
        public const string DownloadURL="https://github.com/Eggmansworld/tp-it-guides/releases/tag/universal-guide";
        public const string GuideURL="https://github.com/Eggmansworld/tp-it-guides";
        public const string Credit="Info courtesy of Eggmansworld";
        readonly string root;
        readonly Connection connection;
        readonly ListBox navigation=new ListBox();
        readonly Label heading=new Label(), statusLabel=new Label(), passwordLabel=new Label();
        readonly TextBox instructions=new TextBox(), databasePassword=new TextBox();
        readonly CheckBox showPassword=new CheckBox();
        readonly Button install=new Button(), recheck=new Button(), services=new Button();
        readonly Button back=new Button(), next=new Button(), done=new Button();
        readonly Button download=new Button(), folder=new Button();
        InstallerStatus state;
        bool installing, preparing;
        string message;
        Color messageColor=SystemColors.ControlText;
        public bool RebootNeeded {get;private set;}
        public bool InstallationStarted {get;private set;}
        public string DatabasePassword {get{return databasePassword.Text;}}
        public InstallHelpForm(string root,string tools,Connection current,string password)
        {
            this.root=root;
            connection=new Connection {server=current.server,port=current.port,username=current.username,pgBin=Engine.DetectTools(tools)};
            Text="First-time PostgreSQL setup";ClientSize=new Size(1000,800);MinimumSize=Size;StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(247,248,250);
            var title=new Label {Text="Set up PostgreSQL 8.3",Font=new Font("Segoe UI",20,FontStyle.Bold)};title.SetBounds(24,18,950,48);Controls.Add(title);
            var intro=new Label {Text="Follow these four pages to prepare Windows, install PostgreSQL and save your database password."};intro.SetBounds(26,76,948,30);Controls.Add(intro);
            navigation.Items.AddRange(new object[]{"1. Introduction","2. Services","3. Install","4. Finish & restart"});
            navigation.SetBounds(24,126,190,520);navigation.IntegralHeight=false;navigation.ItemHeight=38;navigation.DrawMode=DrawMode.OwnerDrawFixed;
            navigation.DrawItem+=(s,e)=>{if(e.Index<0)return;e.DrawBackground();TextRenderer.DrawText(e.Graphics,navigation.Items[e.Index].ToString(),Font,e.Bounds,e.ForeColor,TextFormatFlags.VerticalCenter|TextFormatFlags.Left);e.DrawFocusRectangle();};
            navigation.SelectedIndexChanged+=(s,e)=>{message=null;ShowPage();};Controls.Add(navigation);
            heading.Font=new Font("Segoe UI",16,FontStyle.Bold);heading.SetBounds(238,124,736,44);Controls.Add(heading);
            instructions.Multiline=true;instructions.ReadOnly=true;instructions.TabStop=false;instructions.ScrollBars=ScrollBars.Vertical;instructions.BorderStyle=BorderStyle.None;instructions.BackColor=BackColor;instructions.Font=new Font("Segoe UI",11);instructions.SetBounds(238,174,736,446);Controls.Add(instructions);
            passwordLabel.Text="Database password (from Initialize database cluster)";passwordLabel.SetBounds(238,393,736,28);Controls.Add(passwordLabel);
            databasePassword.SetBounds(238,432,440,30);databasePassword.UseSystemPasswordChar=true;databasePassword.Text=password??"";databasePassword.TextChanged+=(s,e)=>{message=null;UpdateButtons();};Controls.Add(databasePassword);
            showPassword.Text="Show password";showPassword.SetBounds(694,432,160,30);showPassword.CheckedChanged+=(s,e)=>databasePassword.UseSystemPasswordChar=!showPassword.Checked;Controls.Add(showPassword);
            AddButton(services,"Open Services",238,632,200,40,()=>{try{Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"services.msc")){UseShellExecute=true,Verb="runas"});}catch(System.ComponentModel.Win32Exception ex){SetMessage(ex.NativeErrorCode==1223?"Administrator prompt canceled. Click Open Services to try again.":ex.Message,Color.Firebrick);}catch(Exception ex){SetMessage(ex.Message,Color.Firebrick);}});
            AddButton(install,"Run setup",238,632,200,40,null);install.Click+=async(s,e)=>await StartInstaller();
            AddButton(download,"Download installer",452,632,200,40,()=>OpenLink(DownloadURL));
            AddButton(folder,"Installer folder",666,632,200,40,()=>{try{Process.Start(new ProcessStartInfo(InstallerSupport.Folder(root)){UseShellExecute=true});}catch(Exception ex){SetMessage(ex.Message,Color.Firebrick);}});
            statusLabel.SetBounds(238,680,736,60);Controls.Add(statusLabel);
            AddButton(recheck,"Recheck setup files",24,660,190,40,()=>{message=null;RefreshStatus(true);});
            AddButton(back,"Back",742,748,110,40,()=>navigation.SelectedIndex=Math.Max(0,navigation.SelectedIndex-1));
            AddButton(next,"Next",864,748,110,40,()=>navigation.SelectedIndex=Math.Min(3,navigation.SelectedIndex+1));
            AddButton(done,"Finish",864,748,110,40,()=>{if(String.IsNullOrWhiteSpace(databasePassword.Text))return;RebootNeeded=InstallationStarted;DialogResult=DialogResult.OK;Close();});
            var credit=new LinkLabel {Text=Credit+" - GitHub: Eggmansworld/tp-it-guides",AutoSize=false};credit.SetBounds(24,760,700,28);credit.LinkClicked+=(s,e)=>OpenLink(GuideURL);Controls.Add(credit);
            Shown+=(s,e)=>{RefreshStatus();navigation.SelectedIndex=0;};
        }
        void AddButton(Button button,string text,int x,int y,int width,int height,Action action)
        {
            button.Text=text;button.SetBounds(x,y,width,height);button.UseVisualStyleBackColor=true;Controls.Add(button);if(action!=null)button.Click+=(s,e)=>action();
        }
        void ShowPage()
        {
            int page=navigation.SelectedIndex;if(page<0)return;
            string[] titles={"Introduction","Prepare Windows Services","Install Visual C++ 2005 & PostgreSQL","Finish & restart"};
            string[] bodies={
                "Follow this guide to set up PostgreSQL 8.3 for the first time. The included setup also installs Visual C++ 2005 and pgAdmin III.\r\n\r\nFor a fresh installation, uninstall any existing PostgreSQL installation first. Then delete only its PostgreSQL folder under:\r\nC:\\Program Files (x86)\\PostgreSQL\\\r\n\r\nDeleting that folder removes any databases stored there. Keep a copy of any database data you need before removing an existing installation.\r\n\r\nClick Next to prepare Windows Services.",
                "You must be logged in to Windows as an administrator for this step.\r\n\r\nRead these steps, then click Open Services below.\r\n\r\n1. Find Secondary Logon. Right-click it and choose Properties.\r\n2. Set Startup type to Automatic.\r\n3. Under Service status, click Start. If it is already running, leave it running.\r\n4. Click OK and close the Services window.\r\n\r\nReturn here and click Next.",
                "Read these steps, then click Run setup. SETUP.bat installs Visual C++ 2005, then PostgreSQL.\r\n\r\nKeep all settings at their defaults until Service configuration.\r\n\r\nSERVICE CONFIGURATION\r\n1. Enter an Account password and confirm it. Click Next.\r\n2. When asked to create the Windows account, choose Yes.\r\n3. If warned about a weak password, choose No.\r\n\r\nINITIALIZE DATABASE CLUSTER\r\n1. Set Locale to C.\r\n2. Set Encoding (Server) and (Client) to UTF8.\r\n3. Enter and confirm the database password. Click Next.\r\nRemember the DATABASE password for this tool and your game launchers. It is separate from the Windows account password above.\r\n\r\nFINAL SCREENS & INSTALL\r\n1. Keep the remaining defaults and click Next until Install Complete!\r\n2. Uncheck Launch Stack Builder at exit and click Finish.",
                "Enter the DATABASE password you chose on Initialize database cluster below, then click Finish to save it and connect on the main page.\r\n\r\nSave your other work and restart your computer using Start > Power > Restart. Finish saves your password, closes this guide and checks the connection on the main page; it does not restart Windows automatically.\r\n\r\nAfter restarting, reopen IT Database Builder. Click Connect to check PostgreSQL, then add your database files and install the games you want."
            };
            heading.Text=titles[page];instructions.Text=bodies[page];instructions.Select(0,0);
            instructions.Height=page==3?196:446;
            passwordLabel.Visible=databasePassword.Visible=showPassword.Visible=page==3;
            services.Visible=page==1;install.Visible=page==2;done.Visible=page==3;next.Visible=page!=3;
            UpdateButtons();
        }
        void SetMessage(string text,Color color){message=text;messageColor=color;RenderStatus();}
        void RenderStatus()
        {
            statusLabel.ForeColor=message==null?SystemColors.ControlText:messageColor;
            statusLabel.Text=message??(navigation.SelectedIndex!=2?"":state==null?"Checking setup files...":!state.filesPresent?"Installer files are missing: "+String.Join(", ",state.missingFiles)+". Download and extract them into the Installer folder, then Recheck setup files.":state.toolsPresent?"PostgreSQL tools are already present. For a fresh installation, follow the uninstall instructions on Introduction first.":"The bundled SETUP.bat is ready. Read the instructions above, then click Run setup.");
        }
        void UpdateButtons()
        {
            bool working=installing || preparing;
            install.Enabled=!working && !InstallationStarted && state!=null && state.filesPresent && !state.toolsPresent;
            services.Enabled=recheck.Enabled=!working;
            download.Visible=folder.Visible=navigation.SelectedIndex==2 && state!=null && !state.filesPresent;
            back.Enabled=navigation.SelectedIndex>0;
            next.Enabled=navigation.SelectedIndex>=0 && navigation.SelectedIndex<3;
            done.Enabled=!working && !String.IsNullOrWhiteSpace(databasePassword.Text);
            RenderStatus();
        }
        void RefreshStatus(bool showResult=false)
        {
            try{
                connection.pgBin=Engine.DetectTools(connection.pgBin);state=InstallerSupport.Check(root,connection.pgBin);
                if(showResult){
                    message="Recheck complete. Installer files: "+(state.filesPresent?"all found.":"missing "+String.Join(", ",state.missingFiles)+".")+"\r\nPostgreSQL tools: "+(state.toolsPresent?"found":"not found")+" (database connection not tested).\r\n"+state.prerequisiteDetail;
                    messageColor=state.filesPresent?SystemColors.ControlText:Color.Firebrick;
                }
            }
            catch(Exception ex){state=null;message="Could not check setup: "+ex.Message;messageColor=Color.Firebrick;}
            UpdateButtons();
        }
        async Task PrepareWindows()
        {
            if(preparing || installing)return;preparing=true;SetMessage("Preparing Secondary Logon. Approve the Windows administrator prompt.",SystemColors.ControlText);UpdateButtons();
            try{
                using(var process=Process.Start(InstallerSupport.PrepareWindowsStartInfo())){
                    if(process==null)throw new Exception("Windows did not return the preparation result. Click Recheck setup files.");
                    await Task.Run(()=>process.WaitForExit());if(IsDisposed)return;
                    if(process.ExitCode!=0)throw new Exception("Windows preparation failed. On Services, set Secondary Logon to Automatic and start it.");
                }
                message=null;
            }catch(System.ComponentModel.Win32Exception ex){if(!IsDisposed)SetMessage(ex.NativeErrorCode==1223?"Administrator prompt canceled. Click Run setup to try again.":ex.Message,Color.Firebrick);}
            catch(Exception ex){if(!IsDisposed)SetMessage(ex.Message,Color.Firebrick);}
            finally{preparing=false;if(!IsDisposed)RefreshStatus();}
        }
        async Task StartInstaller()
        {
            if(installing || preparing)return;RefreshStatus();if(!install.Enabled)return;
            if(InstallerSupport.NeedsWindowsPreparation(state)){
                await PrepareWindows();if(IsDisposed)return;
                if(state==null || InstallerSupport.NeedsWindowsPreparation(state)){
                    if(message==null)SetMessage("Windows preparation did not finish. Follow the Services page, then try Run setup again.",Color.Firebrick);
                    return;
                }
                if(!install.Enabled)return;
            }
            installing=true;SetMessage("Opening SETUP.bat. Follow the installation instructions above.",SystemColors.ControlText);UpdateButtons();
            try{
                using(var process=Process.Start(InstallerSupport.StartInfo(root))){
                    InstallationStarted=true;
                    if(process==null){SetMessage("Complete the installer, then use Finish & restart to save your database password.",SystemColors.ControlText);return;}
                    await Task.Run(()=>process.WaitForExit());if(IsDisposed)return;
                    if(process.ExitCode!=0 && process.ExitCode!=3010 && process.ExitCode!=1641){SetMessage("Setup exited with code "+process.ExitCode+". If canceled, close this guide and reopen it to try again. Review the installation steps if a password was rejected.",Color.Firebrick);return;}
                }
                RebootNeeded=true;SetMessage("Setup has closed. Once installation is complete, click Next to save your password and restart Windows.",Color.FromArgb(152,79,0));
            }catch(System.ComponentModel.Win32Exception ex){if(!IsDisposed)SetMessage(ex.NativeErrorCode==1223?"Administrator prompt canceled. Click Run setup to try again.":"Could not start setup: "+ex.Message,Color.Firebrick);}
            catch(Exception ex){if(!IsDisposed)SetMessage("Installer stopped: "+ex.Message,Color.Firebrick);}
            finally{installing=false;if(!IsDisposed)RefreshStatus();}
        }
        public static void OpenLink(string url){try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch(Exception ex){MessageBox.Show("Open this address in your browser:\r\n"+url+"\r\n\r\n"+ex.Message);}}
    }

    public sealed class GameEditorForm : Form
    {
        readonly TextBox name=new TextBox(), database=new TextBox(), backup=new TextBox(), folder=new TextBox(), fingerprint=new TextBox();
        readonly ComboBox encoding=new ComboBox();
        readonly Label error=new Label();
        string selectedBackup;
        public GameEditorForm(Engine engine,Game original)
        {
            Text=original==null?"Add game to DatabaseInfo.json":"Edit game in DatabaseInfo.json";ClientSize=new Size(720,575);MinimumSize=Size;StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);
            Label intro=new Label {Text="Define a game once; install its database from the main window later."};intro.SetBounds(22,18,670,30);Controls.Add(intro);
            Field("Game name",name,57);Field("Database name",database,112);
            Label encodingLabel=new Label {Text="Encoding"};encodingLabel.SetBounds(22,169,190,24);Controls.Add(encodingLabel);
            encoding.SetBounds(218,165,230,28);encoding.Items.AddRange(new object[]{"SQL_ASCII","UTF8"});encoding.Text="SQL_ASCII";Controls.Add(encoding);
            Label encodingHelp=new Label {Text="Match the original file."};encodingHelp.SetBounds(460,169,235,25);Controls.Add(encodingHelp);
            Field("Database filename / path",backup,219);backup.Width=315;
            Button pick=new Button {Text="Choose file..."};pick.SetBounds(546,215,152,30);Controls.Add(pick);
            pick.Click+=(s,e)=>{using(var dialog=new OpenFileDialog {Title="Choose the extracted database file for this game",Filter="All files (*.*)|*.*",InitialDirectory=engine.root})if(dialog.ShowDialog(this)==DialogResult.OK){selectedBackup=dialog.FileName;backup.Text=Path.GetFileName(dialog.FileName);fingerprint.Clear();error.Text="The original file location will be remembered when you save.";}};
            Field("Game folder hint (optional)",folder,276);
            Label hint=new Label {Text="Leave blank to use the game name. Folders are only hints for ambiguous filenames."};hint.SetBounds(22,318,676,43);Controls.Add(hint);
            Field("Remembered fingerprint",fingerprint,367);fingerprint.ReadOnly=true;fingerprint.Width=315;
            Button forget=new Button {Text="Forget fingerprint"};forget.SetBounds(546,363,152,30);Controls.Add(forget);forget.Click+=(s,e)=>{fingerprint.Clear();selectedBackup=null;error.Text="The app will match by filename until you choose a database file again.";};
            error.SetBounds(22,419,676,70);error.ForeColor=Color.FromArgb(152,79,0);Controls.Add(error);
            Button cancel=new Button {Text="Cancel",DialogResult=DialogResult.Cancel};cancel.SetBounds(380,510,150,38);Controls.Add(cancel);CancelButton=cancel;
            Button save=new Button {Text="Save game"};save.SetBounds(548,510,150,38);Controls.Add(save);AcceptButton=save;
            if(original!=null){name.Text=original.game;database.Text=original.database;encoding.Text=original.encoding;backup.Text=original.backup;folder.Text=original.backupFolder;fingerprint.Text=original.sha256;}
            save.Click+=(s,e)=>{try{engine.SaveGame(new Game {game=name.Text.Trim(),database=database.Text.Trim(),encoding=encoding.Text.Trim(),backup=backup.Text.Trim(),backupFolder=folder.Text.Trim(),sha256=fingerprint.Text.Trim(),restoreProfile=original==null?null:original.restoreProfile,databaseFile=original==null?null:original.databaseFile},original==null?null:original.database,selectedBackup);DialogResult=DialogResult.OK;Close();}catch(Exception ex){error.Text=ex.Message;}};
        }
        void Field(string label,TextBox control,int y){Label title=new Label {Text=label};title.SetBounds(22,y,190,36);Controls.Add(title);control.SetBounds(218,y-4,480,28);Controls.Add(control);}
    }
    public sealed class WelcomeForm : Form
    {
        public const string RepositoryURL="https://github.com/natemac/IT-Database-Tool";
        readonly CheckBox hide=new CheckBox();
        public bool DontShowAgain {get{return hide.Checked;}}
        public WelcomeForm(bool hidden)
        {
            string version=typeof(WelcomeForm).Assembly.GetName().Version.ToString(3);
            Text="Welcome to IT Database Builder v"+version;ClientSize=new Size(640,552);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",11);BackColor=Color.FromArgb(247,248,250);
            var title=new Label {Text="Your game databases, in a few clicks",Font=new Font("Segoe UI",18,FontStyle.Bold)};title.SetBounds(24,20,595,42);Controls.Add(title);
            var versionLabel=new Label {Text="IT Database Builder  ·  Version "+version,ForeColor=UiTheme.Muted,AccessibleName="App version"};versionLabel.SetBounds(26,66,590,26);Controls.Add(versionLabel);
            var basics=new Label {Text="1. Use PostgreSQL / pgAdmin setup if PostgreSQL 8.3 is missing.\r\n2. Add database files, or auto-load them from your games folder.\r\n3. Check games, or Ctrl/Shift-click rows and right-click for actions.\r\n\r\nInstall adds missing databases and skips installed ones.\r\nClean install restores installed databases from the selected files.\r\nRemove deletes installed databases, including their saved progress.\r\nBack up saves current data and progress in dated pg_backup folders.\r\n\r\nGreen = installed. Black = ready. Red = missing file or needs repair.\r\nClean install and Remove ask before changing installed databases."};basics.SetBounds(26,106,590,250);Controls.Add(basics);
            var repository=new LinkLabel {Text="App on GitHub: natemac/IT-Database-Tool",AccessibleName="App GitHub repository"};repository.SetBounds(26,370,590,28);repository.Links.Add(0,repository.Text.Length,RepositoryURL);repository.LinkClicked+=(s,e)=>InstallHelpForm.OpenLink(RepositoryURL);Controls.Add(repository);
            var credit=new Label {Text=InstallHelpForm.Credit+", creator of the IT setup guide."};credit.SetBounds(26,409,590,28);Controls.Add(credit);
            var github=new LinkLabel {Text="Guide on GitHub: Eggmansworld/tp-it-guides"};github.SetBounds(26,441,590,28);github.LinkClicked+=(s,e)=>InstallHelpForm.OpenLink(InstallHelpForm.GuideURL);Controls.Add(github);
            hide.Text="Don't show again";hide.Checked=hidden;hide.SetBounds(26,491,300,30);Controls.Add(hide);
            var start=new Button {Text="Get started",DialogResult=DialogResult.OK};start.SetBounds(452,487,164,40);Controls.Add(start);AcceptButton=start;UiTheme.Apply(this);
        }
    }
    public sealed class BackupChoiceForm : Form
    {
        public string ExistingAction {get;private set;}
        public BackupChoiceForm(IEnumerable<DatabaseBackup> backups)
        {
            Text="A backup already exists";ClientSize=new Size(660,340);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;
            var description=new Label {Text="Choose how to save these databases. Keep versions saves new dated files. Overwrite latest replaces only the latest backup for each game listed below, after the new backup passes verification."};description.SetBounds(22,18,616,70);Controls.Add(description);
            var files=new TextBox {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Text=String.Join("\r\n\r\n",backups.Select(item=>item.game+"\r\n"+item.file))};files.SetBounds(22,100,616,158);Controls.Add(files);
            var keep=new Button {Text="Keep versions"};keep.SetBounds(162,282,150,36);keep.Click+=(s,e)=>{ExistingAction="keep";DialogResult=DialogResult.OK;};Controls.Add(keep);AcceptButton=keep;
            var overwrite=new Button {Text="Overwrite latest"};overwrite.SetBounds(322,282,156,36);overwrite.Click+=(s,e)=>{ExistingAction="overwrite";DialogResult=DialogResult.OK;};Controls.Add(overwrite);
            var cancel=new Button {Text="Cancel",DialogResult=DialogResult.Cancel};cancel.SetBounds(488,282,150,36);Controls.Add(cancel);CancelButton=cancel;UiTheme.Apply(this);
        }
    }
    public sealed class CheckboxListView : ListView
    {
        bool suppressMouseCheck;
        protected override void WndProc(ref Message message)
        {
            // Native list views can also toggle on a row double-click.
            // Only suppress the native double-click toggle. Normal clicks retain
            // native row selection, including Ctrl/Shift selection ranges.
            if(message.Msg==0x203){
                int position=message.LParam.ToInt32();
                ListViewHitTestInfo hit=HitTest(new Point((short)(position&0xffff),(short)((position>>16)&0xffff)));
                suppressMouseCheck=(hit.Location&ListViewHitTestLocations.StateImage)==0;
                try{base.WndProc(ref message);}finally{suppressMouseCheck=false;}
            }else base.WndProc(ref message);
        }
        protected override void OnItemCheck(ItemCheckEventArgs e)
        {
            if(suppressMouseCheck)e.NewValue=e.CurrentValue;
            base.OnItemCheck(e);
        }
    }
    public sealed class SetupForm : Form
    {
        readonly Engine engine;
        readonly ListView list=new CheckboxListView();
        readonly Button build=new ModernButton(), refresh=new ModernButton(), reportButton=new ModernButton(), connect=new ModernButton();
        readonly Label intro=new Label(), summary=new Label();
        readonly TextBox server=new TextBox(), user=new TextBox(), password=new TextBox(), pgBin=new TextBox();
        readonly NumericUpDown port=new NumericUpDown();
        readonly ProgressBar progress=new ProgressBar();
        readonly Button choose=new Button(), editInfo=new Button(), installHelp=new Button();
        readonly CheckBox selectAll=new CheckBox {AutoCheck=false};
        readonly Button clearAll=new Button();
        readonly Button addGame=new Button(), editGame=new Button();
        readonly Button autoLoad=new Button();
        readonly Button clean=new Button(), remove=new Button(), welcome=new Button();
        readonly Button backup=new ModernButton {Primary=true}, openBackups=new Button();
        readonly Button filesMenuButton=new ModernButton(), libraryMenuButton=new ModernButton(), helpMenuButton=new ModernButton(), selectedMenuButton=new ModernButton(), rowMenuButton=new ModernButton();
        readonly RoundedCard connectionCard=new RoundedCard();
        readonly Label subtitle=new Label(), inventorySummary=new Label(), selectionSummary=new Label();
        readonly ContextMenuStrip filesMenu=new ContextMenuStrip(), libraryMenu=new ContextMenuStrip(), helpMenu=new ContextMenuStrip(), selectedMenu=new ContextMenuStrip(), rowMenu=new ContextMenuStrip();
        ToolStripMenuItem selectedBackup,selectedClean,selectedRemove,rowBackup,rowInstall,rowClean,rowRemove,rowChoose,rowEdit,rowProperties,rowOpenBackup,rowRestore;
        string[] rowMenuDatabases=new string[0];
        CancellationTokenSource fileSearch;
        string lastGamesFolder;
        bool busy,scanning,syncingChecks,databasesChecked;
        bool connectionEdited, passwordEdited, loadingConnection, restartRequired;
        public SetupForm(Engine core)
        {
            engine=core;Text="IT Database Builder v1.2.0";ClientSize=new Size(1280,820);MinimumSize=new Size(1056,799);
            StartPosition=FormStartPosition.CenterScreen;Font=new Font("Segoe UI",10);BackColor=Color.FromArgb(245,246,248);
            intro.Text="Database Library";intro.Font=new Font("Segoe UI",23,FontStyle.Bold);Controls.Add(intro);
            subtitle.Text="Your games, saved progress and database backups. All in one place.";subtitle.ForeColor=Color.FromArgb(105,111,121);Controls.Add(subtitle);
            helpMenuButton.Text="Help  ▾";Controls.Add(helpMenuButton);
            Controls.Add(connectionCard);
            AddField(connectionCard,"Address",server,18,16,170);
            var portLabel=new Label {Text="Port"};portLabel.SetBounds(202,16,84,20);connectionCard.Controls.Add(portLabel);
            port.Minimum=1;port.Maximum=65535;port.SetBounds(202,38,84,28);connectionCard.Controls.Add(port);
            AddField(connectionCard,"Username",user,302,16,160);
            AddField(connectionCard,"Password",password,478,16,200);password.UseSystemPasswordChar=true;
            connect.Text="Connect";connect.SetBounds(696,35,116,34);connectionCard.Controls.Add(connect);
            connect.Click+=async(s,e)=>{if(PersistPassword())await Scan();};
            AddField(connectionCard,"PostgreSQL 8.3 tools",pgBin,18,76,660);
            var browse=new ModernButton {Text="Browse…"};browse.SetBounds(696,95,116,30);connectionCard.Controls.Add(browse);
            browse.Click+=(s,e)=>{using(var dialog=new FolderBrowserDialog()){dialog.Description="Select your PostgreSQL 8.3 bin folder";if(dialog.ShowDialog(this)==DialogResult.OK)pgBin.Text=dialog.SelectedPath;}};
            Connection saved=engine.LoadConnection();server.Text=saved.server;port.Value=saved.port;user.Text=saved.username;pgBin.Text=saved.pgBin;password.Text=engine.InfoField("Password")??"";
            var section=new Label {Text="Databases",Font=new Font("Segoe UI",17,FontStyle.Bold),Name="databaseHeading"};section.SetBounds(24,252,400,34);Controls.Add(section);
            inventorySummary.Text="Connect to check your database library.";inventorySummary.ForeColor=subtitle.ForeColor;Controls.Add(inventorySummary);
            backup.Text="Backup databases";Controls.Add(backup);backup.Click+=async(s,e)=>await StartBackup(true);
            selectAll.Text="Select all";Controls.Add(selectAll);selectAll.Click+=(s,e)=>{bool check=selectAll.CheckState!=CheckState.Checked;syncingChecks=true;try{foreach(ListViewItem row in list.Items)row.Checked=check;}finally{syncingChecks=false;UpdateSelectAll();}};
            filesMenuButton.Text="Add files  ▾";Controls.Add(filesMenuButton);
            libraryMenuButton.Text="Library  ▾";Controls.Add(libraryMenuButton);
            refresh.Text="Refresh";Controls.Add(refresh);refresh.Click+=async(s,e)=>await Scan();
            rowMenuButton.Text="Database actions  ▾";Controls.Add(rowMenuButton);
            list.View=View.Details;list.FullRowSelect=true;list.HideSelection=false;list.MultiSelect=true;list.CheckBoxes=true;
            list.Columns.Add("Game",220);list.Columns.Add("Database",115);list.Columns.Add("Status",195);list.Columns.Add("Latest backup / source file",360);list.Columns.Add("Backed up",135);
            list.Columns[4].DisplayIndex=3;list.Columns[3].DisplayIndex=4;Controls.Add(list);UiTheme.StyleTable(list);
            list.MouseDown+=RowMouseDown;
            list.ItemChecked+=(s,e)=>{if(!syncingChecks)UpdateSelectAll();};list.SelectedIndexChanged+=(s,e)=>UpdateActionButtons();
            list.ContextMenuStrip=rowMenu;
            summary.ForeColor=Color.FromArgb(85,92,104);Controls.Add(summary);Controls.Add(progress);
            build.Text="Install selected";((ModernButton)build).Primary=true;Controls.Add(build);build.Click+=async(s,e)=>await StartBuild(connectionCard);
            selectedMenuButton.Text="Selected actions  ▾";Controls.Add(selectedMenuButton);
            selectionSummary.ForeColor=subtitle.ForeColor;Controls.Add(selectionSummary);
            reportButton.Text="Latest report";Controls.Add(reportButton);reportButton.Click+=(s,e)=>OpenLatestReport();
            // Command objects keep the existing action guards in one place. They
            // are exposed through menus instead of another row of buttons.
            choose.Click+=(s,e)=>ChooseBackup();autoLoad.Click+=(s,e)=>AutoLoadFiles();addGame.Click+=(s,e)=>EditGame(false);editGame.Click+=(s,e)=>EditGame(true);
            clearAll.Click+=(s,e)=>{foreach(ListViewItem row in list.Items)row.Checked=false;};
            clean.Click+=async(s,e)=>await StartBuild(connectionCard,true);remove.Click+=async(s,e)=>await StartRemove();welcome.Click+=(s,e)=>ShowWelcome();installHelp.Click+=async(s,e)=>await ShowInstallHelp();
            editInfo.Click+=(s,e)=>{try{Process.Start(new ProcessStartInfo("notepad.exe","\""+engine.DefinitionPath+"\""){UseShellExecute=true});}catch(Exception ex){summary.Text=ex.Message;}};
            openBackups.Click+=(s,e)=>OpenBackupFolder(null);
            BuildMenus();
            FormClosing+=(s,e)=>{if(fileSearch!=null){fileSearch.Cancel();e.Cancel=true;summary.Text="Canceling the file search. Close the app after it finishes.";}else if(busy || scanning){e.Cancel=true;summary.Text="A database operation is running. Wait for it to finish.";}else if(!PersistPassword())e.Cancel=true;};
            FormClosed+=(s,e)=>{foreach(var menu in new[]{filesMenu,libraryMenu,helpMenu,selectedMenu,rowMenu})menu.Dispose();foreach(var command in new[]{choose,autoLoad,addGame,editGame,clearAll,clean,remove,welcome,installHelp,editInfo,openBackups})command.Dispose();};
            server.TextChanged+=(s,e)=>ConnectionChanged();user.TextChanged+=(s,e)=>ConnectionChanged();pgBin.TextChanged+=(s,e)=>ConnectionChanged();port.ValueChanged+=(s,e)=>ConnectionChanged();
            password.TextChanged+=(s,e)=>{if(!loadingConnection)passwordEdited=true;MarkConnectionUnverified();};
            password.Leave+=(s,e)=>{if(!loadingConnection && !busy && !scanning)PersistPassword();};
            Shown+=async(s,e)=>{if(!engine.HideWelcome())ShowWelcome();await Scan();if(!IsDisposed && !Engine.ToolsPresent(pgBin.Text))await ShowInstallHelp();};
            Resize+=(s,e)=>LayoutLibrary();LayoutLibrary();UiTheme.Apply(this);UpdateActionButtons();
        }
        void LayoutLibrary()
        {
            int width=ClientSize.Width,height=ClientSize.Height;
            intro.SetBounds(24,22,width-180,42);subtitle.SetBounds(26,68,width-180,24);helpMenuButton.SetBounds(width-124,28,100,36);
            connectionCard.SetBounds(24,104,width-48,140);
            inventorySummary.SetBounds(26,288,width-300,24);backup.SetBounds(width-246,258,222,42);
            selectAll.SetBounds(26,324,96,34);filesMenuButton.SetBounds(134,322,126,36);libraryMenuButton.SetBounds(272,322,110,36);refresh.SetBounds(394,322,100,36);rowMenuButton.SetBounds(width-208,322,184,36);
            list.SetBounds(24,372,width-48,Math.Max(180,height-514));
            list.Columns[3].Width=Math.Max(260,list.ClientSize.Width-220-115-195-135-24);
            summary.SetBounds(26,height-130,width-52,48);progress.SetBounds(24,height-75,width-48,4);
            build.SetBounds(24,height-60,152,38);selectedMenuButton.SetBounds(188,height-60,174,38);selectionSummary.SetBounds(378,height-53,Math.Max(180,width-570),28);reportButton.SetBounds(width-174,height-60,150,38);
        }
        Connection CurrentConnection()
        {return new Connection {server=server.Text.Trim(),port=(int)port.Value,username=user.Text.Trim(),pgBin=Engine.DetectTools(pgBin.Text.Trim())};}
        static ToolStripMenuItem MenuItem(ContextMenuStrip menu,string text,EventHandler action)
        {var item=new ToolStripMenuItem(text);item.Click+=action;menu.Items.Add(item);return item;}
        void BuildMenus()
        {
            MenuItem(filesMenu,"Add database file…",(s,e)=>choose.PerformClick());MenuItem(filesMenu,"Auto-Find database files…",(s,e)=>autoLoad.PerformClick());
            MenuItem(libraryMenu,"Add game…",(s,e)=>addGame.PerformClick());
            libraryMenu.Items.Add(new ToolStripSeparator());MenuItem(libraryMenu,"Open backups folder",(s,e)=>openBackups.PerformClick());MenuItem(libraryMenu,"Open Database List",(s,e)=>editInfo.PerformClick());MenuItem(libraryMenu,"Clear checkboxes",(s,e)=>clearAll.PerformClick());
            MenuItem(helpMenu,"Welcome / basics",(s,e)=>welcome.PerformClick());MenuItem(helpMenu,"PostgreSQL / pgAdmin setup",(s,e)=>installHelp.PerformClick());
            selectedBackup=MenuItem(selectedMenu,"Back up selected databases",async(s,e)=>await StartBackup(false));selectedClean=MenuItem(selectedMenu,"Clean install selected…",async(s,e)=>await StartBuild(connectionCard,true));
            selectedMenu.Items.Add(new ToolStripSeparator());selectedRemove=MenuItem(selectedMenu,"Remove selected databases…",async(s,e)=>await StartRemove());selectedRemove.ForeColor=Color.Firebrick;
            rowBackup=MenuItem(rowMenu,"Back Up Database",async(s,e)=>await StartBackup(false,rowMenuDatabases.ToArray()));
            rowRestore=MenuItem(rowMenu,"Restore backup…",(s,e)=>ShowBackupPicker());
            rowOpenBackup=MenuItem(rowMenu,"Open latest backup folder",(s,e)=>OpenBackupFolder(SelectedDatabase()));
            rowMenu.Items.Add(new ToolStripSeparator());
            rowInstall=MenuItem(rowMenu,"Install Database",async(s,e)=>await StartBuild(connectionCard,false,rowMenuDatabases.ToArray()));
            rowClean=MenuItem(rowMenu,"Clean Install Database from source…",async(s,e)=>await StartBuild(connectionCard,true,rowMenuDatabases.ToArray()));
            rowChoose=MenuItem(rowMenu,"Choose database file…",(s,e)=>ChooseBackup());rowEdit=MenuItem(rowMenu,"Edit game…",(s,e)=>EditGame(true));
            rowMenu.Items.Add(new ToolStripSeparator());rowProperties=MenuItem(rowMenu,"Properties…",(s,e)=>ShowProperties());
            rowMenu.Items.Add(new ToolStripSeparator());rowRemove=MenuItem(rowMenu,"Remove Database…",async(s,e)=>await StartRemove(rowMenuDatabases.ToArray()));rowRemove.ForeColor=Color.Firebrick;
            filesMenuButton.Click+=(s,e)=>{if(fileSearch!=null){fileSearch.Cancel();filesMenuButton.Enabled=false;summary.Text="Canceling the file search…";}else filesMenu.Show(filesMenuButton,new Point(0,filesMenuButton.Height));};libraryMenuButton.Click+=(s,e)=>libraryMenu.Show(libraryMenuButton,new Point(0,libraryMenuButton.Height));
            helpMenuButton.Click+=(s,e)=>helpMenu.Show(helpMenuButton,new Point(0,helpMenuButton.Height));selectedMenuButton.Click+=(s,e)=>selectedMenu.Show(selectedMenuButton,new Point(0,selectedMenuButton.Height));rowMenuButton.Click+=(s,e)=>rowMenu.Show(rowMenuButton,new Point(0,rowMenuButton.Height));
            foreach(var menu in new[]{filesMenu,libraryMenu,helpMenu,selectedMenu,rowMenu}){UiTheme.ConfigureMenu(menu);menu.Opening+=(s,e)=>{if(menu==rowMenu)rowMenuDatabases=SelectedDatabases();UpdateActionButtons();if(busy || scanning || (menu==rowMenu && rowMenuDatabases.Length==0))e.Cancel=true;};}
            filesMenu.Opening+=(s,e)=>{filesMenu.Items[0].Enabled=choose.Enabled;};
        }
        string SelectedDatabase(){return list.SelectedItems.Count==1?list.SelectedItems[0].Name:null;}
        string[] SelectedDatabases(){return list.SelectedItems.Cast<ListViewItem>().Select(row=>row.Name).ToArray();}
        ListViewItem[] ActionRows(string[] targetDatabases=null,bool allDatabases=false)
        {
            var names=targetDatabases==null?null:new HashSet<string>(targetDatabases,StringComparer.Ordinal);
            return list.Items.Cast<ListViewItem>().Where(row=>names!=null?names.Contains(row.Name):allDatabases || row.Checked).ToArray();
        }
        Entry[] BackupTargets(bool allDatabases,string[] targetDatabases=null)
        {return ActionRows(targetDatabases,allDatabases).Select(row=>row.Tag as Entry).Where(entry=>entry!=null && entry.databaseInstalled).ToArray();}
        static string DatabaseActionText(string action,int eligible,bool multiple,bool confirmation=false)
        {return action+(multiple?" ("+eligible+") Database"+(eligible==1?"":"s"):" Database")+(confirmation?"…":"");}
        void OpenLatestReport()
        {try{if(File.Exists(engine.LastReportPath))Process.Start(new ProcessStartInfo(Path.GetDirectoryName(engine.LastReportPath)){UseShellExecute=true});else summary.Text="The latest report is no longer available.";}catch(Exception ex){summary.Text="Could not open the report: "+ex.Message;}}
        void OpenBackupFolder(string database)
        {
            try{
                string folder=engine.UserBackupDirectory;
                if(database!=null){var latest=engine.LatestBackup(CurrentConnection(),database);if(latest==null){summary.Text="There is no available backup for this game on this connection.";return;}folder=Path.GetDirectoryName(engine.BackupRecordPath(latest));}
                if(Directory.Exists(folder))Process.Start(new ProcessStartInfo(folder){UseShellExecute=true});else summary.Text="Use Backup databases to save your first backup.";
            }catch(Exception ex){summary.Text="Could not open backups: "+ex.Message;}
        }
        async void ShowProperties()
        {
            if(busy || scanning || SelectedDatabase()==null)return;
            string database=SelectedDatabase();
            try{using(var dialog=new DatabasePropertiesForm(engine,CurrentConnection(),list.SelectedItems[0].Tag as Entry,databasesChecked,!restartRequired && Engine.ToolsPresent(pgBin.Text))){dialog.ShowDialog(this);if(dialog.RestoreSelection!=null)await RestoreBackup(database,dialog.RestoreSelection);else if(dialog.HistoryChanged)await Scan();}}catch(Exception ex){summary.Text="Could not open properties: "+ex.Message;}
        }
        async void ShowBackupPicker()
        {
            if(busy || scanning || restartRequired || !databasesChecked || SelectedDatabase()==null)return;
            string database=SelectedDatabase();
            try{using(var dialog=new BackupPickerForm(engine,CurrentConnection(),database,list.SelectedItems[0].Text)){dialog.ShowDialog(this);if(dialog.RestoreSelection!=null)await RestoreBackup(database,dialog.RestoreSelection);}}catch(Exception ex){summary.Text="Could not open backups: "+ex.Message;}
        }
        async Task RestoreBackup(string database,DatabaseBackup record)
        {
            if(busy || scanning || restartRequired || !await Scan() || !databasesChecked)return;
            try{
                var row=list.Items[database];if(row==null)throw new Exception("This game is no longer in the library.");
                bool installed=((Entry)row.Tag).databaseInstalled;
                var restore=engine.PrepareRecordedRestore(CurrentConnection(),database,record);
                if(installed && !ConfirmOverwrite(new[]{new Entry {database=((Entry)row.Tag).database,backup=restore.Path}}))return;
                engine.SelectRecordedBackup(CurrentConnection(),database,record);
                await StartBuild(connectionCard,installed,new[]{database},restore);
            }catch(Exception ex){summary.Text="Restore stopped: "+ex.Message;MessageBox.Show(this,summary.Text,"Restore backup",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        }
        void UpdateBackupStatus(ListViewItem row)
        {
            while(row.SubItems.Count<5)row.SubItems.Add("");
            row.SubItems[4].Text=engine.BackupStatus(CurrentConnection(),row.Name);
            if(row.Tag is Entry)row.SubItems[3].Text=BackupText((Entry)row.Tag);
            var latest=engine.BackupHistory(CurrentConnection(),row.Name).FirstOrDefault();
            if(latest!=null){string path=engine.BackupRecordPath(latest);row.ToolTipText+="\r\nLast backed up: "+DateTimeOffset.Parse(latest.createdUtc,CultureInfo.InvariantCulture).ToLocalTime().ToString("f")+"\r\n"+path+(File.Exists(path)?"":" (file missing)")+"\r\nA backup is a snapshot; changes made later are not included.";}
        }
        void RowMouseDown(object sender,MouseEventArgs e)
        {
            if(busy || scanning || e.Button!=MouseButtons.Right)return;
            var hit=list.HitTest(e.Location);
            if(hit.Item==null){list.SelectedItems.Clear();rowMenuDatabases=new string[0];rowMenu.Close();return;}
            if(!hit.Item.Selected){list.SelectedItems.Clear();hit.Item.Selected=true;}
            hit.Item.Focused=true;
            // Native left clicks retain Shift/Ctrl range selection. Checkboxes
            // remain independent from the highlighted row actions.
        }
        void ConnectionChanged(){if(!loadingConnection){connectionEdited=true;MarkConnectionUnverified();}}
        bool PersistPassword()
        {
            try{
                if(connectionEdited){engine.SaveConnection(new Connection {server=server.Text.Trim(),port=(int)port.Value,username=user.Text.Trim(),pgBin=String.IsNullOrWhiteSpace(pgBin.Text)?null:pgBin.Text.Trim()});connectionEdited=false;}
                if(passwordEdited){engine.SavePassword(password.Text);passwordEdited=false;}
                return true;
            }
            catch(Exception ex){summary.Text="Could not save the connection settings: "+ex.Message;return false;}
        }
        void ShowWelcome()
        {
            using(var dialog=new WelcomeForm(engine.HideWelcome())){
                dialog.ShowDialog(this);try{engine.SaveWelcomePreference(dialog.DontShowAgain);}catch(Exception ex){summary.Text="Could not save the welcome preference: "+ex.Message;}
            }
        }
        Control[] WorkControls() {return new Control[]{build,clean,remove,backup,openBackups,refresh,connect,choose,autoLoad,addGame,editGame,editInfo,installHelp,welcome,selectAll,clearAll,list,connectionCard,filesMenuButton,libraryMenuButton,helpMenuButton,selectedMenuButton,rowMenuButton};}
        void MarkConnectionUnverified()
        {
            if(loadingConnection || busy || scanning)return;
            databasesChecked=false;
            UpdateActionButtons();
            foreach(ListViewItem row in list.Items)try{UpdateBackupStatus(row);}catch{row.SubItems[4].Text="Unavailable";}
            summary.Text="Connection settings changed. Selections are preserved; displayed results are from the last check.\r\nClick Reload to verify the settings and enable the applicable actions.";
        }
        void UpdateSelectAll()
        {
            selectAll.Checked=list.Items.Count>0 && list.CheckedItems.Count==list.Items.Count;
            UpdateActionButtons();
        }
        void UpdateActionButtons()
        {
            bool ready=!busy && !scanning && !restartRequired && databasesChecked && Engine.ToolsPresent(pgBin.Text);
            Entry[] selected=list.CheckedItems.Cast<ListViewItem>().Select(row=>row.Tag as Entry).Where(entry=>entry!=null).ToArray();
            build.Enabled=ready && selected.Any(entry=>!entry.databaseInstalled && entry.backup!=null);
            clean.Enabled=ready && selected.Any(entry=>entry.databaseInstalled && entry.backup!=null);
            remove.Enabled=ready && selected.Any(entry=>entry.databaseInstalled);
            bool canBackup=ready && File.Exists(Path.Combine(pgBin.Text,"pg_dump.exe"));
            backup.Enabled=canBackup && list.Items.Cast<ListViewItem>().Any(row=>row.Tag is Entry && ((Entry)row.Tag).databaseInstalled);
            openBackups.Enabled=!busy && !scanning;
            choose.Enabled=!busy && !scanning && list.SelectedItems.Count==1;
            selectionSummary.Text=selected.Length+" checked  ·  "+list.SelectedItems.Count+" highlighted";
            if(selectedBackup!=null){
                selectedBackup.Enabled=canBackup && selected.Any(entry=>entry.databaseInstalled);selectedClean.Enabled=clean.Enabled;selectedRemove.Enabled=remove.Enabled;
                selectedMenuButton.Enabled=!busy && !scanning && selected.Length>0;
                Entry[] highlighted=list.SelectedItems.Cast<ListViewItem>().Select(row=>row.Tag as Entry).Where(entry=>entry!=null).ToArray();
                Entry rowEntry=list.SelectedItems.Count==1?highlighted.FirstOrDefault():null;
                int installed=highlighted.Count(entry=>entry.databaseInstalled),installable=highlighted.Count(entry=>!entry.databaseInstalled && entry.backup!=null),cleanable=highlighted.Count(entry=>entry.databaseInstalled && entry.backup!=null);
                bool multiple=list.SelectedItems.Count>1;
                int count=highlighted.Length;
                rowBackup.Text=DatabaseActionText("Back Up",count,multiple);rowInstall.Text=DatabaseActionText("Install",count,multiple);
                rowClean.Text=DatabaseActionText("Clean Install",count,multiple)+" from source…";rowRemove.Text=DatabaseActionText("Remove",count,multiple,true);
                rowBackup.Enabled=canBackup && count>0 && installed==count;rowInstall.Enabled=ready && count>0 && installable==count;
                rowClean.Enabled=ready && count>0 && cleanable==count;rowRemove.Enabled=ready && count>0 && installed==count;
                rowProperties.Enabled=rowChoose.Enabled=rowEdit.Enabled=!busy && !scanning && rowEntry!=null;
                rowMenuButton.Enabled=!busy && !scanning && highlighted.Length>0;
                try{rowOpenBackup.Enabled=rowProperties.Enabled && engine.LatestBackup(CurrentConnection(),rowEntry.requestedDatabase)!=null;}catch{rowOpenBackup.Enabled=false;}
                rowRestore.Enabled=ready && rowOpenBackup.Enabled;
            }
            refresh.Enabled=!busy && !scanning;
            connect.Enabled=!busy && !scanning;
            reportButton.Enabled=!busy && !scanning && File.Exists(engine.LastReportPath);
        }
        bool ConfirmOverwrite(IEnumerable<Entry> entries)
        {
            Entry[] games=entries.ToArray();
            if(MessageBox.Show(this,"These databases are already installed:\r\n\r\n"+String.Join("\r\n\r\n",games.Select(entry=>entry.database+"\r\n"+entry.backup))+"\r\n\r\nRestore them from these selected database files?\r\n\r\nThe current databases will be kept as recovery copies. A failed restore will leave the current databases unchanged. Close games and pgAdmin connections using these databases first.","Overwrite installed databases?",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return false;
            return true;
        }
        async Task ShowInstallHelp()
        {
            bool reboot,started,finished;
            var current=new Connection {server=server.Text.Trim(),port=(int)port.Value,username=user.Text.Trim(),pgBin=pgBin.Text.Trim()};
            using(var dialog=new InstallHelpForm(engine.root,pgBin.Text,current,password.Text)){
                dialog.ShowDialog(this);reboot=dialog.RebootNeeded;started=dialog.InstallationStarted;finished=dialog.DialogResult==DialogResult.OK;
                if(dialog.DialogResult==DialogResult.OK || !String.IsNullOrEmpty(dialog.DatabasePassword)){
                    password.Text=dialog.DatabasePassword;if(!PersistPassword())return;
                }
            }
            pgBin.Text=Engine.DetectTools(pgBin.Text);
            if(finished){
                if(reboot || started)restartRequired=true;
                await Scan();
                if(restartRequired)summary.Text+="\r\nRestart Windows, then reopen this app before installing game databases.";
                return;
            }
            if(reboot || started){restartRequired=true;UpdateActionButtons();summary.Text=reboot?"Installation finished. Reboot Windows, then reopen this app to build your databases.":"Finish the installer and reboot Windows, then reopen this app to build your databases.";}
            else if(Engine.ToolsPresent(pgBin.Text)){await Scan();}
            else summary.Text="Use PostgreSQL / pgAdmin setup to follow the installation steps here in the app.";
        }
        async void ChooseBackup()
        {
            if(busy || scanning)return;
            if(list.SelectedItems.Count!=1){summary.Text="Select one game, then right-click and choose its database file.";return;}
            ListViewItem row=list.SelectedItems[0];
            try{
                Game game=engine.catalog.Single(g=>g.database==row.Name);
                Entry entry=row.Tag as Entry;
                string actual=entry==null?null:entry.backup;
                bool found=!String.IsNullOrEmpty(actual) && File.Exists(actual);
                using(var dialog=new OpenFileDialog {Title="Add database file for "+row.Text+(found?"":" - Looking for: "+Engine.ExpectedBackupPath(game)),Filter="All files (*.*)|*.*",InitialDirectory=found?Path.GetDirectoryName(actual):Directory.Exists(engine.BackupDirectory)?engine.BackupDirectory:engine.root,FileName=Path.GetFileName(found?actual:game.backup)})
                    if(dialog.ShowDialog(this)==DialogResult.OK){engine.RememberBackup(row.Name,dialog.FileName);row.Checked=true;await Scan();}
            }catch(Exception ex){summary.Text=ex.Message;MessageBox.Show(this,ex.Message,"Add database file",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        }
        async void AutoLoadFiles()
        {
            if(fileSearch!=null){fileSearch.Cancel();autoLoad.Enabled=false;summary.Text="Canceling the file search...";return;}
            if(busy || scanning)return;
            string folder;
            using(var dialog=new FolderBrowserDialog {Description="Choose your games folder. All subfolders will be searched for extracted database files.",ShowNewFolderButton=false,SelectedPath=lastGamesFolder??engine.root}){
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;folder=dialog.SelectedPath;lastGamesFolder=folder;
            }
            Control[] locked=WorkControls().Where(control=>control!=autoLoad && control!=filesMenuButton).ToArray();
            bool[] enabled=locked.Select(control=>control.Enabled).ToArray();
            foreach(Control control in locked)control.Enabled=false;
            busy=true;UpdateActionButtons();fileSearch=new CancellationTokenSource();autoLoad.Text="Cancel file search";filesMenuButton.Text="Cancel search";filesMenuButton.Enabled=true;progress.Style=ProgressBarStyle.Marquee;
            FileLoadResult result=null;string error=null;
            Stopwatch updates=Stopwatch.StartNew();
            Action<string> update=message=>{if(updates.ElapsedMilliseconds<250)return;updates.Restart();if(!IsDisposed)BeginInvoke(new Action(()=>{if(fileSearch!=null)summary.Text=message;}));};
            try{result=await Task.Run(()=>engine.AutoLoadDatabaseFiles(folder,update,fileSearch.Token));}
            catch(Exception ex){error=ex.Message;}
            finally{
                busy=false;fileSearch.Dispose();fileSearch=null;
                if(!IsDisposed){autoLoad.Text="Auto-Find database files";autoLoad.Enabled=true;filesMenuButton.Text="Add files  ▾";filesMenuButton.Enabled=true;progress.Style=ProgressBarStyle.Blocks;for(int i=0;i<locked.Length;i++)locked[i].Enabled=enabled[i];UpdateActionButtons();}
            }
            if(IsDisposed)return;
            await Scan();string checkedState=summary.Text;
            if(result!=null){
                summary.Text=(result.canceled?"Search canceled. ":"File search finished. ")+result.entries.Count(entry=>entry.status=="Loaded")+" linked, "+result.entries.Count(entry=>entry.status=="Already available")+" already available, "+result.entries.Count(entry=>entry.status=="Choose file")+" need a file choice, "+result.entries.Count(entry=>entry.status=="Not found")+" not found.\r\n"+(result.error??"Use Add database file for unclear matches; Open latest report shows details.");
                if(!databasesChecked)summary.Text+="\r\n"+checkedState.Split(new[]{"\r\n"},StringSplitOptions.None)[0];
            }else summary.Text="File search stopped: "+error;
        }
        async void EditGame(bool editExisting)
        {
            if(busy || scanning)return;
            try{
                string selected=editExisting && list.SelectedItems.Count==1?list.SelectedItems[0].Name:null;
                if(editExisting && selected==null){summary.Text="Select the game row first, then click Edit game.";return;}
                engine.ReloadDefinition();Game original=editExisting?engine.catalog.Single(g=>g.database==selected):null;
                using(var dialog=new GameEditorForm(engine,original))if(dialog.ShowDialog(this)==DialogResult.OK){await Scan();}
            }catch(Exception ex){summary.Text=ex.Message;}
        }
        static void AddField(Control parent,string name,TextBox box,int x,int y,int width)
        { Label label=new Label {Text=name};label.SetBounds(x,y,width,20);parent.Controls.Add(label);box.SetBounds(x,y+22,width,27);parent.Controls.Add(box); }
        async Task<bool> Scan(bool checkInstalled=true)
        {
            if(scanning || busy)return false;
            scanning=true;databasesChecked=false;syncingChecks=true;
            Control[] locked=WorkControls();
            bool[] enabled=locked.Select(control=>control.Enabled).ToArray();
            foreach(Control control in locked)control.Enabled=false;
            UpdateActionButtons();
            try {
                loadingConnection=true;
                try{
                    if(!connectionEdited){Connection current=engine.LoadConnection();server.Text=current.server;port.Value=current.port;user.Text=current.username;pgBin.Text=current.pgBin;}
                    if(!passwordEdited)password.Text=engine.InfoField("Password")??"";
                }finally{loadingConnection=false;}
                var checks=list.Items.Cast<ListViewItem>().ToDictionary(row=>row.Name,row=>row.Checked);
                var highlighted=new HashSet<string>(SelectedDatabases(),StringComparer.Ordinal);
                string focusedDatabase=list.FocusedItem==null?null:list.FocusedItem.Name;
                list.Items.Clear(); Entry[] entries=engine.Inventory();
                foreach(Entry e in entries){ListViewItem row=new ListViewItem(e.game);row.Name=e.requestedDatabase;row.Tag=e;row.StateImageIndex=0;row.Checked=checks.ContainsKey(row.Name) && checks[row.Name];row.SubItems.Add(e.database);row.SubItems.Add(DisplayStatus(e.status));row.SubItems.Add(BackupText(e));row.ToolTipText=e.detail??e.backup??BackupText(e);UpdateBackupStatus(row);list.Items.Add(row);row.Selected=highlighted.Contains(row.Name);if(row.Name==focusedDatabase){row.Focused=true;row.EnsureVisible();}}
                list.ShowItemToolTips=true;
                int found=entries.Count(e=>e.backup!=null);
                if(!checkInstalled)return true;
                foreach(ListViewItem row in list.Items)row.SubItems[2].Text="Checking database";
                summary.Text="Checking PostgreSQL for installed game databases...";progress.Style=ProgressBarStyle.Marquee;
                try{
                    Connection connection=new Connection {server=server.Text.Trim(),port=(int)port.Value,username=user.Text.Trim(),pgBin=Engine.DetectTools(pgBin.Text.Trim())};
                    string secret=password.Text;
                    if(!PersistPassword())return false;
                    entries=await Task.Run(()=>engine.CheckInstalledGames(connection,secret,entries));
                    secret=null;if(IsDisposed)return false;
                    int present=0;
                    foreach(Entry entry in entries){
                        ListViewItem row=list.Items[entry.requestedDatabase];
                        string actual=entry.databaseInstalled?entry.database:null;
                        if(actual!=null)present++;
                        ShowDatabaseState(row,entry,actual);
                        row.Checked=checks.ContainsKey(row.Name)?checks[row.Name]:(actual==null && entry.backup!=null);
                    }
                    databasesChecked=true;
                    int repair=entries.Count(entry=>entry.databaseNeedsRepair);
                    inventorySummary.Text=present+" installed  ·  "+found+" source files  ·  "+entries.Length+" games";
                    summary.Text=repair>0?repair+" databases need repair. Right-click a game to review its properties.":"Backup databases saves every installed game. Use Shift/Ctrl to highlight games and right-click for batch actions, or check games for Selected actions.";
                    if(engine.FileScanWarnings.Count>0)summary.Text+="\r\n"+engine.FileScanWarnings.Count+" files or folders could not be read; other files were checked.";
                }catch(Exception ex){
                    if(IsDisposed)return false;
                    string status=DatabaseCheckStatus(ex.Message,password.Text,Engine.ToolsPresent(pgBin.Text));
                    foreach(ListViewItem row in list.Items){row.SubItems[2].Text=status;row.ForeColor=Color.Red;row.ToolTipText="Could not check whether this database is installed. "+ex.Message;}
                    inventorySummary.Text="Connection needs attention  ·  "+entries.Length+" games in your library";
                    summary.Text="Installed databases could not be checked. "+ex.Message+"\r\nCorrect the connection or password, then click Reload. "+found+" database files found.";
                }
                return true;
            } catch(Exception ex) {if(!IsDisposed)summary.Text=ex.Message;return false;}
            finally{scanning=false;syncingChecks=false;if(!IsDisposed){progress.Style=ProgressBarStyle.Blocks;for(int i=0;i<locked.Length;i++)locked[i].Enabled=enabled[i];UpdateSelectAll();}}
        }
        static string DatabaseCheckStatus(string error,string secret,bool toolsPresent)
        {
            if(!toolsPresent)return "PostgreSQL tools missing";
            if(String.IsNullOrEmpty(secret))return "Missing database password";
            if(Regex.IsMatch(error,@"rejected the password|password authentication failed",RegexOptions.IgnoreCase))return "Wrong database password";
            if(Regex.IsMatch(error,@"cannot reach PostgreSQL|could not connect|connection refused",RegexOptions.IgnoreCase))return "Cannot connect to PostgreSQL";
            if(Regex.IsMatch(error,@"Select your PostgreSQL 8\.3 server",RegexOptions.IgnoreCase))return "Wrong PostgreSQL version";
            return "Database check failed";
        }
        string BackupText(Entry entry)
        {
            var latest=engine.BackupHistory(CurrentConnection(),entry.requestedDatabase).FirstOrDefault();
            if(latest!=null)return "\\pg_backup\\"+latest.file.Substring(latest.file.IndexOf('/')+1).Replace('/', '\\');
            if(!String.IsNullOrEmpty(entry.backup)){
                string prefix=engine.BackupDirectory+Path.DirectorySeparatorChar;
                return entry.backup.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)?"\\pg_backup\\"+entry.backup.Substring(prefix.Length):entry.backup;
            }
            Game game=engine.catalog.Single(g=>g.database==entry.requestedDatabase);
            if(!String.IsNullOrEmpty(game.databaseFile))return game.databaseFile;
            return "\\pg_backup"+Engine.ExpectedBackupPath(game);
        }
        void ShowDatabaseState(ListViewItem row,Entry entry,string installedName)
        {
            entry.databaseInstalled=installedName!=null;entry.database=installedName??entry.database;row.Tag=entry;
            row.SubItems[1].Text=installedName??entry.database;
            if(installedName!=null){
                row.SubItems[2].Text=entry.databaseNeedsRepair?"Needs repair":entry.backup!=null?"Installed or clean install":"Installed";
                row.SubItems[3].Text=BackupText(entry);row.ForeColor=entry.databaseNeedsRepair?Color.Red:Color.DarkGreen;
                row.ToolTipText=entry.databaseNeedsRepair?entry.databaseHealthDetail+"\r\nUse Clean install with the original database file, or Remove to start over.":"Database is present. "+(entry.installVerified?"This tool's restore was verified. ":entry.gameTables>0?"Game tables were found. ":"")+entry.databaseCheckWarning+"\r\n"+(entry.backup!=null?"Use Clean install to restore from: "+entry.backup:"Add the original database file to enable a clean install.");
            }else{
                row.SubItems[2].Text=entry.backup!=null?"Ready to install":"Looking for file";
                row.SubItems[3].Text=BackupText(entry);row.ForeColor=entry.backup!=null?Color.Black:Color.Red;
                row.ToolTipText=entry.detail??(entry.backup!=null?"Ready to install from: "+entry.backup:BackupText(entry));
            }
            if(!String.IsNullOrEmpty(entry.initialBackupError))row.ToolTipText+="\r\nInitial backup needs attention: "+entry.initialBackupError;
            UpdateBackupStatus(row);
        }
        static string DisplayStatus(string status)
        {
            return status=="Waiting for backup"?"Looking for file":status=="Backup found"?"Database file found":status=="Choose backup"?"Choose database file":status=="Backup needs review"?"Database file needs review":status;
        }
        void UpdateEntry(Entry entry)
        {
            if(IsDisposed)return;
            if(InvokeRequired){BeginInvoke(new Action<Entry>(UpdateEntry),entry);return;}
            ListViewItem row=list.Items[entry.requestedDatabase];
            if(row==null){row=new ListViewItem(entry.game);row.Name=entry.requestedDatabase;row.SubItems.Add(entry.database);row.SubItems.Add(entry.status);row.SubItems.Add("");row.SubItems.Add("");list.Items.Add(row);}
            row.SubItems[1].Text=entry.database;row.SubItems[2].Text=DisplayStatus(entry.status);row.ToolTipText=entry.detail;
            row.SubItems[3].Text=BackupText(entry);
            row.ForeColor=entry.status.Contains("review") || entry.status=="Needs review" ? Color.FromArgb(152,79,0) : (entry.status=="Imported"?Color.DarkGreen:Color.Black);
            if(entry.status=="Already present" || entry.status=="Imported" || entry.status=="Clean installed"){
                Entry previous=row.Tag as Entry;
                Entry state=new Entry {game=entry.game,requestedDatabase=entry.requestedDatabase,database=entry.database,backup=entry.backup??(previous==null?null:previous.backup),databaseNeedsRepair=entry.status=="Already present" && previous!=null && previous.databaseNeedsRepair,databaseHealthDetail=previous==null?null:previous.databaseHealthDetail,initialBackupError=entry.initialBackupError};
                ShowDatabaseState(row,state,entry.database);
            }else if(entry.status=="Waiting for backup" || entry.status=="Choose backup")ShowDatabaseState(row,entry,null);
            syncingChecks=true;try{row.Checked=false;}finally{syncingChecks=false;UpdateSelectAll();}
            row.EnsureVisible();summary.Text=entry.game+": "+DisplayStatus(entry.status);
        }
        static string BuildCompletionText(Report report)
        {
            int imported=report.entries.Count(e=>e.status=="Imported"||e.status=="Clean installed"),review=report.entries.Count(e=>e.status.Contains("review")||e.status=="Needs review"),kept=report.entries.Count(e=>e.status=="Already present"),waiting=report.entries.Count(e=>e.status=="Waiting for backup"),choices=report.entries.Count(e=>e.status=="Choose backup"),backupWarnings=report.entries.Count(e=>!String.IsNullOrEmpty(e.initialBackupError));
            return "Finished: "+imported+" imported, "+review+" need review, "+kept+" already present, "+waiting+" waiting, "+choices+" need a database file choice"+(backupWarnings>0?", "+backupWarnings+(backupWarnings==1?" initial backup needs attention":" initial backups need attention"):"")+".\r\n"+(backupWarnings>0?"Games with backup warnings installed successfully. Open latest report to review the initial backup problems. ":"")+(review>0?"Open latest report to review restore errors before assuming the games are ready.":backupWarnings==0?"Add more database files and run again whenever you want.":"");
        }
        async Task StartBuild(Control connectionPanel,bool cleanInstall=false,string[] targetDatabases=null,ConfirmedBackupRestore confirmedRestore=null)
        {
            if(busy || scanning || restartRequired)return;
            targetDatabases=targetDatabases==null?null:targetDatabases.ToArray();
            if(!await Scan() || !databasesChecked)return;
            if(confirmedRestore!=null){try{engine.ValidateConfirmedRestore(CurrentConnection(),confirmedRestore);}catch(Exception ex){summary.Text=ex.Message;return;}}
            ListViewItem[] scope=ActionRows(targetDatabases);
            if(targetDatabases!=null && (scope.Length!=targetDatabases.Length || scope.Any(row=>!(row.Tag is Entry) || ((Entry)row.Tag).backup==null || ((Entry)row.Tag).databaseInstalled!=cleanInstall))){summary.Text="This action must apply to every highlighted database. Refresh and select a compatible group.";return;}
            if(targetDatabases!=null && !cleanInstall)scope=scope.Where(row=>row.Tag is Entry && !((Entry)row.Tag).databaseInstalled && ((Entry)row.Tag).backup!=null).ToArray();
            string[] selected=scope.Select(row=>row.Name).ToArray();
            if(selected.Length==0){summary.Text=targetDatabases==null?"Check the games you want first.":"None of the highlighted games has a database file ready to install.";return;}
            Entry[] cleanEntries=cleanInstall?scope.Select(row=>row.Tag as Entry).Where(entry=>entry!=null && entry.databaseInstalled && entry.backup!=null).ToArray():new Entry[0];
            if(cleanInstall){
                if(cleanEntries.Length==0){summary.Text="Clean install needs an installed database and its original database file. Add the file first.";return;}
                selected=cleanEntries.Select(entry=>entry.requestedDatabase).ToArray();if(confirmedRestore==null && !ConfirmOverwrite(cleanEntries))return;
            }
            Connection c;
            string secret;
            try{c=new Connection {server=server.Text.Trim(),port=(int)port.Value,username=user.Text.Trim(),pgBin=Engine.DetectTools(pgBin.Text.Trim())};
                secret=password.Text;}
            catch(Exception ex){summary.Text=ex.Message;return;}
            if(!Engine.ToolsPresent(c.pgBin)){await ShowInstallHelp();return;}
            if(String.IsNullOrEmpty(secret)){summary.Text="Enter your PostgreSQL password, then choose an action.";password.Focus();return;}
            Control[] locked=WorkControls();bool[] enabled=locked.Select(control=>control.Enabled).ToArray();
            try {
                busy=true;foreach(Control control in locked)control.Enabled=false;UpdateActionButtons();progress.Style=ProgressBarStyle.Marquee;
                engine.SaveConnection(c);
                Report report=await Task.Run(()=>engine.Build(c,secret,false,UpdateEntry,selected,cleanEntries.Select(entry=>entry.requestedDatabase),confirmedRestore));
                summary.Text=BuildCompletionText(report);
            } catch(Exception ex){summary.Text=ex.Message;MessageBox.Show(this,ex.Message,"Database setup stopped",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
            finally{secret=null;busy=false;for(int i=0;i<locked.Length;i++)locked[i].Enabled=enabled[i];progress.Style=ProgressBarStyle.Blocks;UpdateActionButtons();}
            string completed=summary.Text;await Scan();summary.Text=completed;
        }
        async Task StartBackup(bool allDatabases=true,string[] targetDatabases=null)
        {
            if(busy || scanning || restartRequired)return;
            targetDatabases=targetDatabases==null?null:targetDatabases.ToArray();
            if(!await Scan() || !databasesChecked)return;
            Entry[] selected=BackupTargets(allDatabases,targetDatabases);
            if(targetDatabases!=null && selected.Length!=targetDatabases.Length){summary.Text="Back Up requires every highlighted database to be installed. Refresh and select a compatible group.";return;}
            if(selected.Length==0){summary.Text=targetDatabases!=null?"None of the highlighted games has an installed database to back up.":allDatabases?"There are no installed game databases to back up.":"Check installed games to back up their current databases.";return;}
            var connection=new Connection {server=server.Text.Trim(),port=(int)port.Value,username=user.Text.Trim(),pgBin=Engine.DetectTools(pgBin.Text.Trim())};
            if(!Engine.ToolsPresent(connection.pgBin) || !File.Exists(Path.Combine(connection.pgBin,"pg_dump.exe"))){summary.Text="Select the PostgreSQL 8.3 tools, including pg_dump, before backing up databases.";return;}
            string action="ask",secret=password.Text;
            try{
                var existing=selected.Select(entry=>engine.LatestBackup(connection,entry.requestedDatabase)).Where(item=>item!=null).ToArray();
                if(existing.Length>0)using(var dialog=new BackupChoiceForm(existing)){if(dialog.ShowDialog(this)!=DialogResult.OK)return;action=dialog.ExistingAction;}
            }catch(Exception ex){summary.Text=ex.Message;return;}
            Control[] locked=WorkControls();bool[] enabled=locked.Select(control=>control.Enabled).ToArray();
            try{
                busy=true;foreach(Control control in locked)control.Enabled=false;UpdateActionButtons();progress.Style=ProgressBarStyle.Marquee;
                summary.Text="Backing up "+selected.Length+" installed game databases…";engine.SaveConnection(connection);
                Report report=await Task.Run(()=>engine.BackupDatabases(connection,secret,selected.Select(entry=>entry.requestedDatabase),action,entry=>{if(!IsDisposed)BeginInvoke(new Action(()=>{summary.Text=entry.game+": "+entry.status;}));}));
                summary.Text="Finished: "+report.entries.Count(entry=>entry.status=="Backed up")+" backed up; "+report.entries.Count(entry=>entry.status=="Needs review")+" need review.\r\nOpen backups to view the files. Use Add database file to select one for a later restore.";
            }catch(Exception ex){summary.Text="Backup stopped: "+ex.Message;}
            finally{secret=null;busy=false;for(int i=0;i<locked.Length;i++)locked[i].Enabled=enabled[i];progress.Style=ProgressBarStyle.Blocks;UpdateActionButtons();}
            string completed=summary.Text;await Scan();summary.Text=completed;
        }
        async Task StartRemove(string[] targetDatabases=null)
        {
            if(busy || scanning || restartRequired)return;
            targetDatabases=targetDatabases==null?null:targetDatabases.ToArray();
            if(!await Scan() || !databasesChecked)return;
            Entry[] selected=ActionRows(targetDatabases).Select(row=>row.Tag as Entry).Where(entry=>entry!=null && entry.databaseInstalled).ToArray();
            if(targetDatabases!=null && selected.Length!=targetDatabases.Length){summary.Text="Remove requires every highlighted database to be installed. Refresh and select a compatible group.";return;}
            if(selected.Length==0){summary.Text=targetDatabases==null?"Check an installed database to remove it.":"None of the highlighted games has an installed database to remove.";return;}
            if(MessageBox.Show(this,"Permanently remove these installed databases?\r\n\r\n"+String.Join("\r\n",selected.Select(entry=>entry.database))+"\r\n\r\nThis deletes their saved progress and scores. Your original database files and any recovery copies will be kept. You can reinstall from the original files afterward.","Remove selected databases?",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
            var connection=new Connection {server=server.Text.Trim(),port=(int)port.Value,username=user.Text.Trim(),pgBin=Engine.DetectTools(pgBin.Text.Trim())};
            string secret=password.Text;
            Control[] locked=WorkControls();bool[] enabled=locked.Select(control=>control.Enabled).ToArray();
            try{
                busy=true;foreach(Control control in locked)control.Enabled=false;UpdateActionButtons();progress.Style=ProgressBarStyle.Marquee;summary.Text="Removing selected databases...";
                Report report=await Task.Run(()=>engine.RemoveDatabases(connection,secret,selected.Select(entry=>entry.requestedDatabase),true,UpdateEntry));
                summary.Text="Finished: "+report.entries.Count(entry=>entry.status=="Removed")+" removed; "+report.entries.Count(entry=>entry.status=="Needs review")+" need review. Original database files were kept.";
            }catch(Exception ex){summary.Text="Removal stopped: "+ex.Message;}
            finally{secret=null;busy=false;for(int i=0;i<locked.Length;i++)locked[i].Enabled=enabled[i];progress.Style=ProgressBarStyle.Blocks;UpdateActionButtons();}
            string completed=summary.Text;await Scan();summary.Text=completed;
        }
    }
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                string root=AppDomain.CurrentDomain.BaseDirectory;
                for(int i=0;i<args.Length;i++)if(args[i]=="--root" && i+1<args.Length)root=args[++i];
                Engine engine=new Engine(root);
                if(args.Contains("--migrate-settings")){Console.WriteLine("Connection settings are stored in UserSettings.json; DatabaseInfo.json contains the database list.");return 0;}
                int loadFiles=Array.IndexOf(args,"--auto-load-files");
                if(loadFiles>=0){if(loadFiles+1>=args.Length)throw new Exception("Use --auto-load-files GAMES_FOLDER.");Console.WriteLine(Engine.Json.Serialize(engine.AutoLoadDatabaseFiles(args[loadFiles+1])));return 0;}
                if(args.Contains("--prepare-backup-folders")){engine.PrepareBackupFolders();Console.WriteLine("Game database file folders are ready inside PG_backup.");return 0;}
                if(args.Contains("--installer-check")){Connection check=engine.LoadConnection();int bin=Array.IndexOf(args,"--pg-bin");if(bin>=0){if(bin+1>=args.Length)throw new Exception("Specify --pg-bin FOLDER.");check.pgBin=args[bin+1];}Console.WriteLine(Engine.Json.Serialize(InstallerSupport.Check(root,check.pgBin)));return 0;}
                int remember=Array.IndexOf(args,"--remember-backup");
                if(remember>=0){if(remember+2>=args.Length)throw new Exception("Use --remember-backup DATABASE BACKUP_FILE.");engine.RememberBackup(args[remember+1],args[remember+2]);Console.WriteLine("Database file remembered in DatabaseInfo.json.");return 0;}
                int saveGame=Array.IndexOf(args,"--save-game");
                if(saveGame>=0){if(saveGame+1>=args.Length)throw new Exception("Use --save-game GAME_JSON_FILE [--replace DATABASE].");Game game;try{game=Engine.Json.Deserialize<Game>(File.ReadAllText(args[saveGame+1]));}catch{throw new Exception("Cannot read the game JSON file.");}int replace=Array.IndexOf(args,"--replace");if(replace>=0 && replace+1>=args.Length)throw new Exception("Specify the database to replace.");engine.SaveGame(game,replace>=0?args[replace+1]:null);Console.WriteLine("Game saved in DatabaseInfo.json.");return 0;}
                if(args.Contains("--inventory")){Console.WriteLine(Engine.Json.Serialize(engine.Inventory()));return 0;}
                if(args.Contains("--check-installed") || args.Contains("--check-games") || args.Contains("--verify-setup")){
                    Connection check=engine.LoadConnection();
                    for(int i=0;i<args.Length;i++){
                        if(args[i]=="--port" && i+1<args.Length)check.port=Int32.Parse(args[++i]);
                        else if(args[i]=="--pg-bin" && i+1<args.Length)check.pgBin=args[++i];
                        else if(args[i]=="--username" && i+1<args.Length)check.username=args[++i];
                    }
                    string secret=Environment.GetEnvironmentVariable("PGPASSWORD");if(String.IsNullOrEmpty(secret))secret=engine.InfoField("Password");
                    if(args.Contains("--verify-setup"))Console.WriteLine(Engine.Json.Serialize(Engine.VerifySetup(check,secret)));
                    else if(args.Contains("--check-games"))Console.WriteLine(Engine.Json.Serialize(engine.CheckInstalledGames(check,secret,engine.Inventory())));
                    else Console.WriteLine(Engine.Json.Serialize(Engine.InstalledDatabases(check,secret)));return 0;
                }
                if(args.Contains("--plan") || args.Contains("--build") || args.Contains("--remove") || args.Contains("--backup"))
                {
                    Connection connection=engine.LoadConnection();
                    for(int i=0;i<args.Length;i++){
                        if(args[i]=="--port" && i+1<args.Length)connection.port=Int32.Parse(args[++i]);
                        else if(args[i]=="--pg-bin" && i+1<args.Length)connection.pgBin=args[++i];
                        else if(args[i]=="--username" && i+1<args.Length)connection.username=args[++i];
                    }
                    string secret=Environment.GetEnvironmentVariable("PGPASSWORD");
                    if(String.IsNullOrEmpty(secret))secret=engine.InfoField("Password");
                    int only=Array.IndexOf(args,"--only");string[] selected=null;
                    if(only>=0){if(only+1>=args.Length)throw new Exception("Use --only DatabaseName,OtherDatabaseName.");selected=args[only+1].Split(',');}
                    int overwrite=Array.IndexOf(args,"--overwrite-confirmed");string[] clean=null;
                    if(overwrite>=0){if(overwrite+1>=args.Length || selected==null)throw new Exception("Use --overwrite-confirmed DatabaseName with --only after confirming a clean install.");clean=args[overwrite+1].Split(',');}
                    if(args.Contains("--backup") && (args.Contains("--remove") || args.Contains("--build") || args.Contains("--plan") || clean!=null))throw new Exception("Run --backup separately from install, remove and preview operations.");
                    int backupExisting=Array.IndexOf(args,"--backup-existing");
                    if(backupExisting>=0 && backupExisting+1>=args.Length)throw new Exception("Use --backup-existing keep or --backup-existing overwrite.");
                    Report report=args.Contains("--backup")?engine.BackupDatabases(connection,secret,selected,backupExisting<0?"ask":args[backupExisting+1],e=>{}):args.Contains("--remove")?engine.RemoveDatabases(connection,secret,selected,args.Contains("--remove-confirmed"),e=>{},args.Contains("--plan")):engine.Build(connection,secret,args.Contains("--plan"),e=>{},selected,clean);
                    Console.WriteLine(Engine.Json.Serialize(report));return 0;
                }
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new SetupForm(engine));return 0;
            }
            catch(Exception ex){if(args.Length>0){Console.Error.WriteLine(ex.Message);return 1;}MessageBox.Show(ex.Message,"IT Database Builder",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
        }
    }
}
