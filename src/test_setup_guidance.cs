using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;
using System.Windows.Forms;
using ITDatabaseSetup;

class SetupGuidanceTest
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static object Get(object instance,string name){return instance.GetType().GetField(name,Private).GetValue(instance);}
    static void Set(object instance,string name,object value){instance.GetType().GetField(name,Private).SetValue(instance,value);}
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    static void Update(object form){form.GetType().GetMethod("UpdateButtons",Private).Invoke(form,null);}
    static Dictionary<string,object> Document(string path){return Engine.Json.DeserializeObject(File.ReadAllText(path)) as Dictionary<string,object>;}
    static Dictionary<string,object> SettingsConnection(string root){return Document(Path.Combine(root,"UserSettings.json"))["connection"] as Dictionary<string,object>;}
    static void WriteJson(string path,object value){File.WriteAllText(path,Engine.PrettyJson(value),new UTF8Encoding(false));}
    static void RenderPreview(Form form,string path)
    {
        // Set only the internal Visible flag for DrawToBitmap. Never Show the
        // form or fire Shown (which would connect to a database in SetupForm).
        typeof(Control).GetMethod("SetState",Private).Invoke(form,new object[]{2,true});
        form.CreateControl();
        using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,form.Width,form.Height));bitmap.Save(path);}
    }
    static string SettingsRoot(string parent,string name,object connection=null)
    {
        string folder=Path.Combine(parent,"settings-"+name);Directory.CreateDirectory(folder);
        var catalogue=new Dictionary<string,object> {{"schemaVersion",1},{"games",new[]{new Game {
            game="Fixture arcade",database="FixtureDB",encoding="UTF8",backup="original.backup",backupFolder="Fixture arcade",
            sha256=new string('a',64),restoreProfile="it-category-keys-v1",databaseFile=Path.Combine(folder,"original.backup")}}}};
        if(connection!=null)catalogue.Add("connection",connection);
        WriteJson(Path.Combine(folder,"DatabaseInfo.json"),catalogue);return folder;
    }
    static object FixtureConnection(string password)
    {
        return new {server="localhost",port=15584,username="fixture_user",password=password,pgBin=@"C:\fixture tools\bin"};
    }
    static void RequireFixtureConnection(string root,string password,string message)
    {
        var fields=SettingsConnection(root);
        Require(fields.Count==5 && (string)fields["server"]=="localhost" && Convert.ToInt32(fields["port"])==15584 && (string)fields["username"]=="fixture_user" && (string)fields["password"]==password && (string)fields["pgBin"]==@"C:\fixture tools\bin",message);
    }
    static void ScanFilesOnly(SetupForm form)
    {
        // The false branch returns before the asynchronous installed-database
        // check; never show the form or invoke its Shown event.
        var scan=(System.Threading.Tasks.Task<bool>)form.GetType().GetMethod("Scan",Private).Invoke(form,new object[]{false});
        Require(scan.IsCompleted && scan.GetAwaiter().GetResult(),"In-memory file-only refresh failed");
        Require(!(bool)Get(form,"databasesChecked"),"File-only refresh must not check installed databases");
    }
    static void FormRefreshTests(string parent)
    {
        string folder=SettingsRoot(parent,"form-refresh",FixtureConnection("initial-fixture-password"));
        string catalogue=Path.Combine(folder,"DatabaseInfo.json"),settings=Path.Combine(folder,"UserSettings.json");
        var engine=new Engine(folder);
        var refreshGames=engine.catalog.Concat(new[]{new Game {game="Second fixture arcade",database="SecondFixtureDB",encoding="SQL_ASCII",backup="second-original.backup"}}).ToArray();
        WriteJson(catalogue,new {schemaVersion=1,games=refreshGames});
        engine.ReloadDefinition();
        byte[] catalogueBefore=File.ReadAllBytes(catalogue);
        using(var form=new SetupForm(engine)){
            var password=(TextBox)Get(form,"password");
            var list=(ListView)Get(form,"list");
            Require(password.Text=="initial-fixture-password" && !(bool)Get(form,"passwordEdited"),"Initial password must not count as a user edit");
            var external=new Engine(folder);
            external.SavePassword("externally-updated-fixture-password");
            external.SaveConnection(new Connection {server="::1",port=15586,username="external_fixture_user",pgBin=@"C:\fixture uninstalled tools\bin"});
            byte[] updatedSettings=File.ReadAllBytes(settings);
            ScanFilesOnly(form);
            Require(password.Text=="externally-updated-fixture-password" && !(bool)Get(form,"passwordEdited"),"Reload did not refresh an unedited password from user settings");
            Require(((TextBox)Get(form,"server")).Text=="::1" && ((NumericUpDown)Get(form,"port")).Value==15586 && ((TextBox)Get(form,"user")).Text=="external_fixture_user" && !(bool)Get(form,"connectionEdited"),"Reload did not refresh unedited connection fields");
            Require(File.ReadAllBytes(settings).SequenceEqual(updatedSettings),"Unedited file-only refresh rewrote settings");
            Require((bool)form.GetType().GetMethod("PersistPassword",Private).Invoke(form,null) && File.ReadAllBytes(settings).SequenceEqual(updatedSettings),"Persisting an unedited form overwrote external settings");

            IntPtr listHandle=list.Handle;
            list.Items[0].Checked=true;
            list.Items[0].Selected=true;list.Items[1].Selected=true;list.Items[1].Focused=true;
            string highlightedDatabase=list.Items[0].Name;
            string otherHighlightedDatabase=list.Items[1].Name;
            password.Text="";
            Require((bool)Get(form,"passwordEdited"),"Explicit password clearing must count as a user edit");
            external.SavePassword("later-external-fixture-password");
            ScanFilesOnly(form);
            Require(password.Text=="" && (bool)Get(form,"passwordEdited") && list.Items[0].Checked,"File-only refresh lost an explicit password clearing or game selection");
            Require(list.SelectedItems.Count==2 && list.Items[highlightedDatabase].Selected && list.Items[otherHighlightedDatabase].Selected && list.FocusedItem.Name==otherHighlightedDatabase && list.Items[highlightedDatabase].Checked && !list.Items[otherHighlightedDatabase].Checked,"File-only refresh must preserve the highlighted batch, focused row and independent checkboxes");
            Require((bool)form.GetType().GetMethod("PersistPassword",Private).Invoke(form,null),"Cleared password could not be persisted after refresh");
            Require(new Engine(folder).InfoField("Password")=="" && !(bool)Get(form,"passwordEdited"),"Cleared password did not persist or edit tracking was not reset");
            Require(File.ReadAllBytes(catalogue).SequenceEqual(catalogueBefore),"Form settings updates changed catalogue bytes");

            // EditGame resolves a selection by database name after this explicit
            // reload; changing the catalogue on disk must not reuse stale Game data.
            string selectedDatabase=list.Items[0].Name;
            var replacement=Document(catalogue);
            ((Dictionary<string,object>)((object[])replacement["games"])[0])["game"]="Externally updated fixture title";
            WriteJson(catalogue,replacement);
            Require(engine.catalog.Single(game=>game.database==selectedDatabase).game=="Fixture arcade","Fixture must begin with stale in-memory game data");
            byte[] beforeReloadSettings=File.ReadAllBytes(settings);
            engine.ReloadDefinition();
            Require(engine.catalog.Single(game=>game.database==selectedDatabase).game=="Externally updated fixture title","Explicit catalogue reload did not refresh selected game data");
            ScanFilesOnly(form);
            Require(list.Items[selectedDatabase].Text=="Externally updated fixture title" && list.Items[selectedDatabase].Checked,"Catalogue refresh lost the updated title or existing checkbox selection");
            Require(list.SelectedItems.Count==2 && list.Items[selectedDatabase].Selected && list.Items[otherHighlightedDatabase].Selected && list.FocusedItem.Name==otherHighlightedDatabase,"Catalogue refresh lost highlighted rows or the focused database");
            Require(File.ReadAllBytes(settings).SequenceEqual(beforeReloadSettings),"Refreshing selected game data changed user settings");
        }
    }
    static void SettingsTests(string parent)
    {
        // Every file below is test-owned. No server, installer or game archive
        // is opened; only the application's configuration APIs are exercised.
        const string secret="test-only \"quoted\" \\ password Ω";
        string fresh=SettingsRoot(parent,"fresh");
        byte[] freshCatalogue=File.ReadAllBytes(Path.Combine(fresh,"DatabaseInfo.json"));
        var freshEngine=new Engine(fresh);
        var defaults=SettingsConnection(fresh);
        Require((string)defaults["server"]=="127.0.0.1" && Convert.ToInt32(defaults["port"])==5432 && (string)defaults["username"]=="postgres" && (string)defaults["password"]=="" && defaults["pgBin"]==null,"Fresh settings must have blank local connection defaults");
        Require(!freshEngine.HideWelcome(),"Fresh settings must show Welcome");
        Require(File.ReadAllBytes(Path.Combine(fresh,"DatabaseInfo.json")).SequenceEqual(freshCatalogue),"Creating fresh settings changed the catalogue");

        string legacy=SettingsRoot(parent,"legacy-json",FixtureConnection(secret));
        string legacyCatalogue=Path.Combine(legacy,"DatabaseInfo.json"),legacySettings=Path.Combine(legacy,"UserSettings.json");
        string gamesBefore=Engine.Json.Serialize(Document(legacyCatalogue)["games"]);
        WriteJson(legacySettings,new {hideWelcome=true,futurePreference=new {theme="test-theme",backupFolder=@"C:\fixture backups"}});
        var legacyEngine=new Engine(legacy);
        Require(legacyEngine.InfoField("Password")==secret && legacyEngine.InfoField("Username")=="fixture_user" && legacyEngine.HideWelcome(),"Legacy JSON migration lost connection or Welcome preference");
        Require(!Document(legacyCatalogue).ContainsKey("connection"),"Legacy JSON migration retained credentials in catalogue");
        Require(Engine.Json.Serialize(Document(legacyCatalogue)["games"])==gamesBefore,"Legacy JSON migration changed game definitions");
        RequireFixtureConnection(legacy,secret,"Legacy JSON migration changed a connection field");
        Require(Document(legacySettings).ContainsKey("futurePreference"),"Legacy JSON migration lost an unknown user preference");
        byte[] migratedCatalogue=File.ReadAllBytes(legacyCatalogue);
        legacyEngine.SavePassword("replacement-fixture-password");
        legacyEngine.SaveWelcomePreference(false);
        Require(new Engine(legacy).InfoField("Password")=="replacement-fixture-password" && !new Engine(legacy).HideWelcome(),"Password and Welcome updates did not persist independently");
        Require(Document(legacySettings).ContainsKey("futurePreference"),"Preference update discarded a future setting");
        legacyEngine.SaveConnection(new Connection {server="::1",port=15585,username="updated_fixture_user",pgBin=@"C:\uninstalled fixture tools\bin"});
        var changed=SettingsConnection(legacy);
        Require((string)changed["server"]=="::1" && Convert.ToInt32(changed["port"])==15585 && (string)changed["username"]=="updated_fixture_user" && (string)changed["pgBin"]==@"C:\uninstalled fixture tools\bin" && (string)changed["password"]=="replacement-fixture-password","Saving connection did not preserve password or persist all editable fields");
        Require(Document(legacySettings).ContainsKey("futurePreference") && !legacyEngine.HideWelcome(),"Saving connection changed unrelated preferences");
        Require(File.ReadAllBytes(legacyCatalogue).SequenceEqual(migratedCatalogue),"Changing user settings changed catalogue bytes");
        byte[] settingsBeforeReplacement=File.ReadAllBytes(legacySettings);
        var replacement=Document(legacyCatalogue);
        ((Dictionary<string,object>)((object[])replacement["games"])[0])["game"]="Updated catalogue title";
        WriteJson(legacyCatalogue,replacement);
        Require(new Engine(legacy).catalog[0].game=="Updated catalogue title","Updated catalogue was not loaded");
        Require(File.ReadAllBytes(legacySettings).SequenceEqual(settingsBeforeReplacement),"Catalogue replacement changed user settings");

        // An explicitly cleared password wins, including when a later catalogue
        // update happens to contain the old legacy connection block again.
        string precedence=SettingsRoot(parent,"precedence",FixtureConnection(secret));
        string precedenceSettings=Path.Combine(precedence,"UserSettings.json");
        WriteJson(precedenceSettings,new {hideWelcome=true,connection=new {server="127.0.0.1",port=15586,username="preferred_user",password="",pgBin=(string)null},futureSetting="keep"});
        byte[] precedenceBefore=File.ReadAllBytes(precedenceSettings);
        var preferred=new Engine(precedence);
        Require(preferred.InfoField("Password")=="" && preferred.InfoField("Username")=="preferred_user" && preferred.HideWelcome(),"Existing settings did not override legacy connection wholesale");
        Require(File.ReadAllBytes(precedenceSettings).SequenceEqual(precedenceBefore),"Loading authoritative settings rewrote them");
        Require(!Document(Path.Combine(precedence,"DatabaseInfo.json")).ContainsKey("connection"),"Authoritative settings left a legacy connection in the catalogue");

        string txt=Path.Combine(parent,"settings-legacy-txt");Directory.CreateDirectory(txt);
        string txtPath=Path.Combine(txt,"DatabaseInfo.txt");
        string txtText="Address: localhost\nPort: 15584\nUsername: fixture_user\nPassword: "+secret+"\nPostgres Path: C:\\fixture tools\\bin\n\nFixture arcade\nDB name: FixtureDB\nEncoding: UTF8\ndb backup location: pg_backup\ndb filename: original.backup\n";
        File.WriteAllText(txtPath,txtText,new UTF8Encoding(false));
        WriteJson(Path.Combine(txt,"UserSettings.json"),new {hideWelcome=true});
        var txtEngine=new Engine(txt);
        Require(txtEngine.InfoField("Password")==secret && txtEngine.HideWelcome(),"Legacy TXT migration lost escaped password or existing preference");
        RequireFixtureConnection(txt,secret,"Legacy TXT migration changed connection fields");
        Require(!Document(Path.Combine(txt,"DatabaseInfo.json")).ContainsKey("connection") && txtEngine.catalog.Length==1,"Legacy TXT migration did not create a separate catalogue");
        Require(File.ReadAllText(txtPath)==txtText,"Legacy TXT source was modified");
        File.WriteAllText(txtPath,"This old TXT must no longer be read.");
        Require(new Engine(txt).InfoField("Password")==secret,"Legacy TXT overrode authoritative JSON");

        string[] malformed={"{\"connection\":", "null", "[]", "{\"connection\":null}", "{\"connection\":\"invalid\"}", "{\"connection\":{\"server\":\"remote.invalid\"}}", "{\"connection\":{\"port\":0}}", "{\"connection\":{\"username\":\"\"}}"};
        for(int i=0;i<malformed.Length;i++){
            string broken=SettingsRoot(parent,"malformed-"+i,FixtureConnection(secret));
            string settings=Path.Combine(broken,"UserSettings.json"),catalogue=Path.Combine(broken,"DatabaseInfo.json");
            File.WriteAllText(settings,malformed[i],new UTF8Encoding(false));
            byte[] settingsBefore=File.ReadAllBytes(settings),catalogueBefore=File.ReadAllBytes(catalogue);
            bool rejected=false;try{new Engine(broken);}catch(Exception error){rejected=true;Require(error.Message.Contains("UserSettings.json") && !error.Message.Contains(secret),"Settings error must identify the file without exposing its contents");}
            Require(rejected,"Malformed settings were silently accepted");
            Require(File.ReadAllBytes(settings).SequenceEqual(settingsBefore) && File.ReadAllBytes(catalogue).SequenceEqual(catalogueBefore),"Malformed settings were overwritten or caused catalogue migration");
        }
        string editedSettings=Path.Combine(fresh,"UserSettings.json");
        File.WriteAllText(editedSettings,"{broken settings",new UTF8Encoding(false));
        byte[] editedBytes=File.ReadAllBytes(editedSettings);
        foreach(Action action in new Action[]{()=>freshEngine.SavePassword("unused"),()=>freshEngine.SaveWelcomePreference(true),()=>freshEngine.SaveConnection(new Connection())}){
            bool rejected=false;try{action();}catch{rejected=true;}
            Require(rejected && File.ReadAllBytes(editedSettings).SequenceEqual(editedBytes),"An existing Engine overwrote malformed settings");
        }
    }
    static void BackupUiTests(string parent)
    {
        string folder=SettingsRoot(parent,"backup-ui");
        string bin=Path.Combine(folder,"bin");Directory.CreateDirectory(bin);
        foreach(string tool in new[]{"psql.exe","pg_restore.exe","pg_dump.exe"})File.WriteAllText(Path.Combine(bin,tool),"never executed fixture");
        string[] names={"Golden Tee Live 2020","Golden Tee Live 2019","Golden Tee Live 2018","Golden Tee Live 2017","Golden Tee Live 2016","Silver Strike Bowling Live","Target Toss Pro Bags","Power Putt Live 2013"};
        string[] databases={"GameDB20","GameDB19","GameDB18","GameDB17","GameDB16","SilverStrikeDB","BagsDB","PowerPuttDB"};
        Game[] games=names.Select((name,index)=>new Game {game=name,database=databases[index],encoding="SQL_ASCII",backup="original.dump",backupFolder=name}).ToArray();
        WriteJson(Path.Combine(folder,"DatabaseInfo.json"),new {schemaVersion=1,games=games});
        var records=new[]{new DatabaseBackup {kind="export",game=names[0],requestedDatabase=databases[0],database=databases[0],databaseOid="12345",server="127.0.0.1",port=15583,encoding="SQL_ASCII",createdUtc="2026-10-09T18:30:00.0000000+00:00",file="pg_backup/2026-10-09/GoldenTee2020_261009_DBbackup",sha256=new string('a',64),bytes=1048576},
            new DatabaseBackup {kind="initial",game=names[0],requestedDatabase=databases[0],database=databases[0],databaseOid="12345",server="127.0.0.1",port=15583,encoding="SQL_ASCII",createdUtc="2026-10-08T18:30:00.0000000+00:00",file="pg_backup/2026-10-08/GoldenTee2020_261008_DBbackup",sha256=new string('b',64),bytes=1048576},
            new DatabaseBackup {game=names[1],requestedDatabase=databases[1],database=databases[1],databaseOid="12346",server="localhost",port=15583,encoding="SQL_ASCII",createdUtc="2026-10-08T18:30:00Z",file="PG_backup/User Backups/GameDB19/GameDB19-20261008.dump",sha256=new string('c',64),bytes=524288}};
        WriteJson(Path.Combine(folder,"UserSettings.json"),new {hideWelcome=true,connection=new {server="127.0.0.1",port=15583,username="postgres",password="",pgBin=bin},backups=records});
        var engine=new Engine(folder);var connection=new Connection {server="127.0.0.1",port=15583,pgBin=bin};
        foreach(var record in records){string path=engine.BackupRecordPath(record);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,"owned backup UI fixture, never restored");}
        string source=Path.Combine(folder,"original.dump");File.WriteAllText(source,"owned source UI fixture, never restored");
        byte[] originalSettings=File.ReadAllBytes(engine.SettingsPath);
        using(var form=new SetupForm(engine)){
            var list=(ListView)Get(form,"list");
            Require(list.Columns.Count==5 && list.Columns[4].Text=="Backed up","Library must have a dedicated backup column");
            Require(list.MultiSelect,"Database rows must support native Ctrl/Shift multi-selection");
            Require(list.Columns[4].DisplayIndex==3 && list.Columns[3].DisplayIndex==4,"Backup date must precede the file path visually");
            IntPtr handle=list.Handle; // Build the native list without showing the form or firing Shown.
            var entry=new Entry {game=names[0],requestedDatabase=databases[0],database=databases[0],databaseInstalled=true};
            var row=new ListViewItem(entry.game) {Name=databases[0],Tag=entry,Checked=true};
            row.SubItems.Add(databases[0]);row.SubItems.Add("Installed");row.SubItems.Add("Missing original file");row.SubItems.Add("Never");list.Items.Add(row);
            Set(form,"databasesChecked",true);
            Action update=()=>form.GetType().GetMethod("UpdateActionButtons",Private).Invoke(form,null);
            Action<ListViewItem> select=selected=>{list.SelectedItems.Clear();selected.Selected=true;selected.Focused=true;update();};
            Func<string,bool> enabled=name=>((ToolStripMenuItem)Get(form,name)).Enabled;
            update();var backup=(Button)Get(form,"backup");
            Require(backup.Enabled,"Installed games must back up without an original archive");
            Require(backup.Text=="Backup databases" && enabled("selectedBackup"),"Global and checked backup actions must be distinct");
            row.Checked=false;select(row);
            Require(backup.Enabled && !enabled("selectedBackup"),"Global backup must include installed games regardless of checkboxes");
            Require(enabled("rowBackup") && enabled("rowRemove") && !enabled("rowInstall") && !enabled("rowClean") && enabled("rowProperties"),"Installed row actions must work without checking the row or finding an original file");
            entry.backup=source;update();Require(enabled("rowClean"),"Installed row with a source file must allow clean install");
            entry.databaseInstalled=false;update();
            Require(!backup.Enabled && !enabled("rowBackup") && enabled("rowInstall") && !enabled("rowClean") && !enabled("rowRemove"),"Ready row actions are wrong");
            entry.backup=null;update();Require(!enabled("rowInstall") && enabled("rowProperties"),"Missing row must still expose properties");
            entry.databaseInstalled=true;entry.backup=source;row.Checked=true;select(row);
            foreach(string state in new[]{"busy","scanning","restartRequired"}){
                Set(form,state,true);update();Require(!backup.Enabled && !enabled("selectedBackup") && !enabled("rowBackup") && !enabled("rowInstall") && !enabled("rowClean") && !enabled("rowRemove"),"Database actions must disable while "+state);Set(form,state,false);
            }
            Set(form,"databasesChecked",false);update();Require(!backup.Enabled && !enabled("selectedBackup") && !enabled("rowBackup"),"Backup must require the current connection to be checked");Set(form,"databasesChecked",true);
            File.Delete(Path.Combine(bin,"pg_dump.exe"));update();Require(!backup.Enabled && !enabled("selectedBackup") && !enabled("rowBackup"),"All backup scopes must require pg_dump");File.WriteAllText(Path.Combine(bin,"pg_dump.exe"),"never executed fixture");
            File.Delete(Path.Combine(bin,"pg_restore.exe"));update();Require(!backup.Enabled && !enabled("selectedBackup") && !enabled("rowBackup"),"Backup must require validation tools");File.WriteAllText(Path.Combine(bin,"pg_restore.exe"),"never executed fixture");
            foreach(string hidden in new[]{"clean","remove","openBackups","choose","autoLoad","addGame","editGame","editInfo","installHelp","welcome","clearAll"})Require(((Control)Get(form,hidden)).Parent==null,"A menu command was added as another visible button: "+hidden);

            // A checked ready row and an unchecked installed row must keep their
            // separate scopes when the context menu target changes.
            var ready=new Entry {game=names[3],requestedDatabase=databases[3],database=databases[3],backup=source};
            var readyRow=new ListViewItem(ready.game) {Name=ready.requestedDatabase,Tag=ready,Checked=true};readyRow.SubItems.Add(ready.database);readyRow.SubItems.Add("Ready");readyRow.SubItems.Add("original.dump");readyRow.SubItems.Add("Never");list.Items.Add(readyRow);
            row.Checked=false;select(readyRow);
            Require(backup.Enabled && !enabled("selectedBackup") && !enabled("rowBackup") && enabled("rowInstall"),"Row scope leaked into the checked or global backup scope");
            Func<bool,string[],Entry[]> targets=(all,databasesInScope)=>(Entry[])form.GetType().GetMethod("BackupTargets",Private).Invoke(form,new object[]{all,databasesInScope});
            Require(targets(true,null).Length==1 && targets(true,null)[0]==entry,"Global backup must target every installed catalog database");
            Require(targets(false,null).Length==0,"Checked backup must omit unchecked installed games");
            Require(targets(false,new[]{row.Name}).Length==1 && targets(false,new[]{row.Name})[0]==entry,"Context backup must target its installed row without requiring a checkbox");
            Require(targets(false,new[]{readyRow.Name}).Length==0,"Context backup must not target an uninstalled row");
            Require(targets(true,new string[0]).Length==0,"An empty explicit scope must never fall back to checked or global games");
            bool[] checks=list.Items.Cast<ListViewItem>().Select(item=>item.Checked).ToArray();
            var rowPoint=new System.Drawing.Point(row.Bounds.Left+70,row.Bounds.Top+row.Bounds.Height/2);
            typeof(Control).GetMethod("OnMouseDown",Private).Invoke(list,new object[]{new MouseEventArgs(MouseButtons.Right,1,rowPoint.X,rowPoint.Y,0)});
            update();Require(list.SelectedItems.Count==1 && list.SelectedItems[0]==row,"Right-click must target the clicked database");
            Require(list.Items.Cast<ListViewItem>().Select(item=>item.Checked).SequenceEqual(checks),"Right-click changed checked games");
            Require(enabled("rowBackup") && !enabled("selectedBackup"),"Right-click backup must target its row without changing the checked batch");
            var filePoint=new System.Drawing.Point(row.SubItems[3].Bounds.Left+20,row.Bounds.Top+row.Bounds.Height/2);
            typeof(Control).GetMethod("OnMouseClick",Private).Invoke(list,new object[]{new MouseEventArgs(MouseButtons.Left,1,filePoint.X,filePoint.Y,0)});
            Require(list.Items.Cast<ListViewItem>().Select(item=>item.Checked).SequenceEqual(checks),"Normal row click changed checked games");
            Require(list.SelectedItems.Count==1 && list.SelectedItems[0]==row,"Clicking the file column must leave row selection intact without opening a file picker");

            var secondEntry=new Entry {game=names[1],requestedDatabase=databases[1],database=databases[1],databaseInstalled=true,backup=source};
            var secondRow=new ListViewItem(secondEntry.game) {Name=secondEntry.requestedDatabase,Tag=secondEntry,Checked=false};secondRow.SubItems.Add(secondEntry.database);secondRow.SubItems.Add("Installed");secondRow.SubItems.Add(source);secondRow.SubItems.Add("Never");list.Items.Add(secondRow);
            select(row);secondRow.Selected=true;update();
            Require(((ToolStripMenuItem)Get(form,"rowBackup")).Text=="Back Up (2) Databases" && ((ToolStripMenuItem)Get(form,"rowRemove")).Text=="Remove (2) Databases…","Batch labels must show the eligible highlighted count");
            Require(enabled("rowBackup") && enabled("rowRemove") && enabled("rowClean") && !enabled("rowInstall"),"Installed highlighted batches must enable only applicable actions");
            Require(!enabled("rowProperties") && !enabled("rowChoose") && !enabled("rowEdit") && !enabled("rowOpenBackup") && !((Button)Get(form,"choose")).Enabled,"Single-game actions must disable for multiple highlighted games");
            Require(targets(false,new[]{row.Name,secondRow.Name}).Length==2 && targets(false,null).Length==0,"Highlighted batch must stay separate from checked games");
            bool[] batchChecks=list.Items.Cast<ListViewItem>().Select(item=>item.Checked).ToArray();
            typeof(Control).GetMethod("OnMouseDown",Private).Invoke(list,new object[]{new MouseEventArgs(MouseButtons.Right,1,rowPoint.X,rowPoint.Y,0)});
            Require(list.SelectedItems.Count==2 && list.SelectedItems.Contains(row) && list.SelectedItems.Contains(secondRow),"Right-clicking a highlighted row must preserve the full batch");
            Require(list.Items.Cast<ListViewItem>().Select(item=>item.Checked).SequenceEqual(batchChecks),"Right-clicking a highlighted batch changed checkbox selection");
            var context=(ContextMenuStrip)Get(form,"rowMenu");var opening=new System.ComponentModel.CancelEventArgs();
            typeof(ContextMenuStrip).GetMethod("OnOpening",Private).Invoke(context,new object[]{opening});
            Require(!opening.Cancel && ((string[])Get(form,"rowMenuDatabases")).SequenceEqual(new[]{row.Name,secondRow.Name}),"Context menu must capture the highlighted batch before an asynchronous refresh");
            select(readyRow);
            Require(((string[])Get(form,"rowMenuDatabases")).SequenceEqual(new[]{row.Name,secondRow.Name}),"Changing highlights after menu opening must not retarget its captured batch");
            select(row);readyRow.Selected=true;update();
            Require(((ToolStripMenuItem)Get(form,"rowBackup")).Text=="Back Up (2) Databases" && ((ToolStripMenuItem)Get(form,"rowInstall")).Text=="Install (2) Databases","Batch labels must describe the whole highlighted group");
            Require(!enabled("rowBackup") && !enabled("rowInstall") && !enabled("rowClean") && !enabled("rowRemove"),"Mixed installed/ready selections must disable partial actions");
            select(row);secondRow.Selected=true;secondEntry.backup=null;update();
            Require(enabled("rowBackup") && enabled("rowRemove") && !enabled("rowClean") && !enabled("rowInstall"),"Mixed source availability must disable clean install while retaining actions applicable to every row");
            secondEntry.backup=source;select(row);readyRow.Selected=true;entry.databaseInstalled=false;update();
            Require(enabled("rowInstall") && !enabled("rowBackup") && !enabled("rowClean") && !enabled("rowRemove"),"All-ready selections must allow installing the whole group");
            entry.databaseInstalled=true;update();
            Require(targets(false,new[]{row.Name,readyRow.Name}).Length==1,"Batch backup must exclude highlighted uninstalled games");
            typeof(Control).GetMethod("OnMouseDown",Private).Invoke(list,new object[]{new MouseEventArgs(MouseButtons.Left,1,filePoint.X,filePoint.Y,0)});
            Require(list.SelectedItems.Count==2,"Left-button event must leave native Ctrl/Shift selection handling intact");
            var secondPoint=new System.Drawing.Point(secondRow.Bounds.Left+70,secondRow.Bounds.Top+secondRow.Bounds.Height/2);
            typeof(Control).GetMethod("OnMouseDown",Private).Invoke(list,new object[]{new MouseEventArgs(MouseButtons.Right,1,secondPoint.X,secondPoint.Y,0)});
            Require(list.SelectedItems.Count==1 && list.SelectedItems[0]==secondRow,"Right-clicking an unselected row must target only that row");
            typeof(Control).GetMethod("OnMouseDown",Private).Invoke(list,new object[]{new MouseEventArgs(MouseButtons.Right,1,20,list.ClientSize.Height-4,0)});
            Require(list.SelectedItems.Count==0 && ((string[])Get(form,"rowMenuDatabases")).Length==0,"Right-clicking empty table space must not retain a previous action target");
            select(row);
            var libraryMenu=(ContextMenuStrip)Get(form,"libraryMenu");
            libraryMenu.Items.OfType<ToolStripMenuItem>().Single(item=>item.Text=="Clear checkboxes").PerformClick();
            Require(list.CheckedItems.Count==0 && list.SelectedItems.Count==1 && list.SelectedItems[0]==row,"Library Clear checkboxes must route through the hidden command and preserve the highlighted row");
            readyRow.Checked=true;update();

            foreach(System.Drawing.Size size in new[]{new System.Drawing.Size(1040,760),new System.Drawing.Size(1280,820)}){
                form.ClientSize=size;form.PerformLayout();
                Control[] buttons=new[]{"build","selectedMenuButton","backup","filesMenuButton","libraryMenuButton","refresh","rowMenuButton","helpMenuButton","reportButton"}.Select(name=>(Control)Get(form,name)).ToArray();
                foreach(Control button in buttons)Require(button.Parent==form && button.Left>=0 && button.Top>=0 && button.Right<=form.ClientSize.Width && button.Bottom<=form.ClientSize.Height,"Library action is outside the window: "+button.Text);
                for(int first=0;first<buttons.Length;first++)for(int second=first+1;second<buttons.Length;second++)Require(!buttons[first].Bounds.IntersectsWith(buttons[second].Bounds),"Library actions overlap: "+buttons[first].Text+" / "+buttons[second].Text);
                Require(list.Bottom<=((Label)Get(form,"summary")).Top && list.Top>=((Button)Get(form,"refresh")).Bottom,"Library table overlaps its controls");
                RenderPreview(form,Path.Combine(parent,"v1.2.0-layout-"+size.Width+".png"));
            }

            list.Items.Clear();
            for(int index=0;index<names.Length;index++){
                bool installed=index<3 || index==5;bool found=index==0 || index==3 || index==5 || index==6;
                var previewEntry=new Entry {game=names[index],requestedDatabase=databases[index],database=databases[index],encoding="SQL_ASCII",databaseInstalled=installed,backup=found?source:null};
                string status=installed?(found?"Installed or clean install":"Installed"):(found?"Ready":"Missing file");
                var previewRow=new ListViewItem(previewEntry.game) {Name=previewEntry.requestedDatabase,Tag=previewEntry,Checked=index==3 || index==6,ForeColor=installed?System.Drawing.Color.FromArgb(34,120,73):found?System.Drawing.Color.FromArgb(33,39,49):System.Drawing.Color.Firebrick};
                previewRow.SubItems.Add(previewEntry.database);previewRow.SubItems.Add(status);previewRow.SubItems.Add(found?"PG_backup\\"+previewEntry.game+"\\original.dump":"\\pg_backup\\"+previewEntry.game+"\\original.dump");list.Items.Add(previewRow);
                form.GetType().GetMethod("UpdateBackupStatus",Private).Invoke(form,new object[]{previewRow});
            }
            Require(list.Items[0].SubItems[4].Text!="Never" && list.Items[2].SubItems[4].Text=="Never","Backup column must display a recorded date or Never");
            form.ClientSize=new System.Drawing.Size(1280,820);select(list.Items[0]);
            ((Label)Get(form,"inventorySummary")).Text="4 installed  ·  2 ready to install  ·  2 need files  ·  2 with backups";
            ((Label)Get(form,"summary")).Text="Your database library is ready. Backups save current progress without changing your games.";
            Require(((Label)Get(form,"selectionSummary")).Text.Contains("2"),"Selection summary must reflect checked games");
            RenderPreview(form,Path.Combine(parent,"v1.2.0-main-preview.png"));
            Set(form,"loadingConnection",true);string previewBin=((TextBox)Get(form,"pgBin")).Text;decimal previewPort=((NumericUpDown)Get(form,"port")).Value;
            ((TextBox)Get(form,"pgBin")).Text=@"C:\Program Files (x86)\PostgreSQL\8.3\bin";((NumericUpDown)Get(form,"port")).Value=5432;
            var previewPaths=list.Items.Cast<ListViewItem>().Select(item=>item.SubItems[3].Text).ToArray();
            foreach(ListViewItem item in list.Items)if(item.SubItems[3].Text.Contains(parent))item.SubItems[3].Text=@"C:\Games\"+item.Text+@"\original.dump";
            RenderPreview(form,Path.Combine(parent,"v1.2.0-public-main.png"));
            for(int previewIndex=0;previewIndex<list.Items.Count;previewIndex++)list.Items[previewIndex].SubItems[3].Text=previewPaths[previewIndex];
            ((TextBox)Get(form,"pgBin")).Text=previewBin;((NumericUpDown)Get(form,"port")).Value=previewPort;Set(form,"loadingConnection",false);
            using(var properties=new DatabasePropertiesForm(engine,connection,list.Items[0].Tag as Entry)){
                var history=properties.Controls.Cast<Control>().SelectMany(control=>control.Controls.Cast<Control>()).OfType<ListView>().FirstOrDefault(control=>control.AccessibleName=="Backup history");
                if(history==null)history=properties.Controls.OfType<ListView>().FirstOrDefault(control=>control.AccessibleName=="Backup history");
                Require(history!=null && history.Items.Count==2,"Properties must expose all versions for the selected database");
                Require(history.Columns.Cast<ColumnHeader>().Any(column=>column.Text=="Type") && history.Items[0].SubItems[1].Text=="Backup" && history.Items[1].SubItems[1].Text=="Initial database","Properties must distinguish original archive copies from installed database exports");
                Require(history.ContextMenuStrip!=null && history.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(item=>item.Text=="Restore backup…") && history.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(item=>item.Text=="Delete backup…"),"Properties history requires restore and delete context actions");
                RenderPreview(properties,Path.Combine(parent,"v1.2.0-properties-preview.png"));
                foreach(var field in properties.Controls.Cast<Control>().SelectMany(control=>control.Controls.Cast<Control>()).OfType<TextBox>())field.Text=field.Text.Replace(folder,@"C:\IT Database Tool");
                RenderPreview(properties,Path.Combine(parent,"v1.2.0-public-properties.png"));
                history.Items[1].Selected=true;
                history.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(item=>item.Text=="Restore backup…").PerformClick();
                Require(properties.RestoreSelection!=null && properties.RestoreSelection.kind=="initial","Properties restore must return the right-clicked backup version");
            }
            Require(list.Items[0].SubItems[3].Text.EndsWith("GoldenTee2020_261009_DBbackup"),"The file column must show the latest recorded backup rather than the original source");
            using(var picker=new BackupPickerForm(engine,connection,databases[0],names[0])){
                var history=picker.Controls.OfType<ListView>().Single();
                Require(history.Items.Count==2 && history.Items[1].SubItems[1].Text=="Initial database","Restore picker must include the labeled initial database");
                IntPtr pickerHandle=history.Handle;history.Items[1].Selected=true;
                RenderPreview(picker,Path.Combine(parent,"v1.2.0-restore-picker-preview.png"));
                typeof(Button).GetMethod("OnClick",Private).Invoke(picker.AcceptButton,new object[]{EventArgs.Empty});
                Require(picker.RestoreSelection!=null && picker.RestoreSelection.kind=="initial","Restore picker must return the chosen version");
            }
            string backedUp=list.Items[0].SubItems[4].Text;File.Delete(engine.BackupRecordPath(records[0]));
            form.GetType().GetMethod("UpdateBackupStatus",Private).Invoke(form,new object[]{list.Items[0]});
            Require(list.Items[0].SubItems[4].Text==backedUp && list.Items[0].ToolTipText.Contains("file missing"),"Missing backup must retain the snapshot date and explain its availability");
            using(var missingProperties=new DatabasePropertiesForm(engine,connection,list.Items[0].Tag as Entry)){
                var availability=missingProperties.Controls.Cast<Control>().SelectMany(control=>control.Controls.Cast<Control>()).OfType<TextBox>().Single(control=>control.AccessibleName=="Availability");
                Require(availability.Text=="Recorded file is missing","Properties must identify missing backup files");
            }
            var backupWarning=new Entry {game=names[0],requestedDatabase=databases[0],database=databases[0],backup=source,status="Imported",initialBackupError="Fixture backup folder is unavailable"};
            form.GetType().GetMethod("UpdateEntry",Private).Invoke(form,new object[]{backupWarning});
            Require(list.Items[0].SubItems[2].Text.StartsWith("Installed") && list.Items[0].ToolTipText.Contains("Initial backup needs attention"),"Initial backup failure must remain visible without marking a successful install broken");
            var warningReport=new Report();warningReport.entries.Add(backupWarning);
            string completion=(string)typeof(SetupForm).GetMethod("BuildCompletionText",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{warningReport});
            Require(completion.Contains("1 imported") && completion.Contains("1 initial backup needs attention") && completion.Contains("installed successfully"),"Completion must identify backup warnings separately from installation errors");
            Require(File.ReadAllBytes(engine.SettingsPath).SequenceEqual(originalSettings),"Library/Properties inspection rewrote private settings");
        }
        var policyRecord=new DatabaseBackup {game="Fixture arcade",file="PG_backup/User Backups/FixtureDB/FixtureDB-dated.dump"};
        foreach(string action in new[]{"Keep versions","Overwrite latest","Cancel"})using(var dialog=new BackupChoiceForm(new[]{policyRecord})){
            Require(((Button)dialog.AcceptButton).Text=="Keep versions","Default must keep versions");
            RenderPreview(dialog,Path.Combine(parent,"v1.2.0-backup-choice-preview.png"));
            var button=dialog.Controls.OfType<Button>().Single(item=>item.Text==action);
            typeof(Button).GetMethod("OnClick",Private).Invoke(button,new object[]{EventArgs.Empty});
            if(action=="Cancel")Require(dialog.ExistingAction==null,"Cancel must not choose an overwrite policy");
            else Require(dialog.DialogResult==DialogResult.OK && dialog.ExistingAction==(action=="Keep versions"?"keep":"overwrite"),"Wrong backup policy selected");
        }
    }
    [STAThread] static int Main(string[] args)
    {
        using(var form=new InstallHelpForm(args[0],"missing-tools",new Connection {port=15584},"test-only-database-password")){
            // Construct controls in memory only. Never show a window, launch an
            // installer, prepare Windows, copy a password or connect to a server.
            var navigation=(ListBox)Get(form,"navigation");
            var instructions=(TextBox)Get(form,"instructions");
            Require(navigation.Items.Count==4,"Expected four setup pages");
            navigation.SelectedIndex=0;
            Require(instructions.Text.Contains("uninstall") && instructions.Text.Contains(@"C:\Program Files (x86)\PostgreSQL\") && instructions.Text.Contains("removes any databases"),"Missing fresh-install and folder guidance");
            var back=(Button)Get(form,"back");var next=(Button)Get(form,"next");
            Require(!back.Enabled && next.Enabled,"Introduction navigation is wrong");
            navigation.SelectedIndex=1;
            foreach(string expected in new[]{"administrator","Secondary Logon","Properties","Automatic","Start","Click OK"})Require(instructions.Text.Contains(expected),"Missing Services step: "+expected);
            Require(((Button)Get(form,"services")).Text=="Open Services" && back.Enabled && next.Enabled,"Missing Services action or navigation");
            navigation.SelectedIndex=2;
            foreach(string expected in new[]{"SETUP.bat","Visual C++ 2005","defaults","SERVICE CONFIGURATION","Account password","choose Yes","choose No","Locale to C","(Server) and (Client) to UTF8","database password","FINAL SCREENS","Uncheck Launch Stack Builder","click Finish"})Require(instructions.Text.Contains(expected),"Missing installer step: "+expected);
            var state=new InstallerStatus {filesPresent=true,missingFiles=new string[0],toolsPresent=false,secondaryLogonRunning=true,secondaryLogonAutomatic=true,prerequisiteDetail="Test prerequisite status"};
            Set(form,"state",state);Update(form);
            var install=(Button)Get(form,"install");
            Require(install.Text=="Run setup" && install.Enabled && !InstallerSupport.NeedsWindowsPreparation(state),"Fresh installation readiness is wrong");
            state.filesPresent=false;Update(form);Require(!install.Enabled,"Incomplete installer must not launch");
            state.filesPresent=true;state.secondaryLogonRunning=false;Update(form);Require(install.Enabled && InstallerSupport.NeedsWindowsPreparation(state),"Launch must prepare a stopped prerequisite");
            state.secondaryLogonRunning=true;state.secondaryLogonAutomatic=false;Update(form);Require(install.Enabled && InstallerSupport.NeedsWindowsPreparation(state),"Launch must prepare a manual prerequisite");
            state.secondaryLogonAutomatic=true;state.toolsPresent=true;Update(form);Require(!install.Enabled,"Existing installation must not be overwritten");
            foreach(string busy in new[]{"installing","preparing"}){Set(form,busy,true);Update(form);Require(!install.Enabled,"Setup action must be disabled while "+busy);Set(form,busy,false);}
            navigation.SelectedIndex=3;
            Require(instructions.Text.Contains("save it and connect") && instructions.Text.Contains("restart your computer") && instructions.Text.Contains("Click Connect") && instructions.Text.Contains("does not restart Windows automatically"),"Missing finish/restart guidance");
            var done=(Button)Get(form,"done");var password=(TextBox)Get(form,"databasePassword");
            Require(back.Enabled && !next.Enabled && done.Text=="Finish","Final page navigation is wrong");
            Require(password.UseSystemPasswordChar && password.Text=="test-only-database-password","Final page password must preserve the user's current password");
            password.Text="";Update(form);Require(!done.Enabled,"Missing password must disable Finish");
            password.Text="replacement-fixture-password";Update(form);Require(done.Enabled,"Entered password must enable Finish");
            Require(form.Controls.OfType<LinkLabel>().Single().Text.Contains("Info courtesy of Eggmansworld"),"Setup credit missing");
            typeof(Button).GetMethod("OnClick",Private).Invoke(done,new object[]{EventArgs.Empty});
            Require(form.DialogResult==DialogResult.OK && !form.RebootNeeded && form.DatabasePassword=="replacement-fixture-password","Finish must return the password for saving and connecting; revisiting setup must not require another restart");
        }
        using(var setupPreview=new InstallHelpForm(args[0],"missing-tools",new Connection(),"")){
            ((ListBox)Get(setupPreview,"navigation")).SelectedIndex=2;
            Set(setupPreview,"state",new InstallerStatus {filesPresent=true,missingFiles=new string[0],toolsPresent=false,secondaryLogonRunning=true,secondaryLogonAutomatic=true,prerequisiteDetail="Secondary Logon is ready"});Update(setupPreview);
            RenderPreview(setupPreview,Path.Combine(args[0],"v1.2.0-setup-preview.png"));
        }
        using(var welcome=new WelcomeForm(false)){
            Require(welcome.Controls.OfType<LinkLabel>().Any(link=>link.Text.Contains("Eggmansworld/tp-it-guides")),"Welcome guide GitHub link missing");
            var repository=welcome.Controls.OfType<LinkLabel>().Single(link=>link.AccessibleName=="App GitHub repository");
            Require(repository.Links.Count==1 && (string)repository.Links[0].LinkData=="https://github.com/natemac/IT-Database-Tool","Welcome must link to this app's GitHub repository");
            string version=typeof(WelcomeForm).Assembly.GetName().Version.ToString(3);
            Require(welcome.Text.Contains("v"+version) && welcome.Controls.OfType<Label>().Any(label=>label.AccessibleName=="App version" && label.Text.Contains(version)),"Welcome must show the compiled app version");
            Require(welcome.Controls.OfType<Label>().Any(label=>label.Text.Contains("Info courtesy of Eggmansworld")),"Welcome author credit missing");
            foreach(Control control in welcome.Controls)Require(control.Left>=0 && control.Top>=0 && control.Right<=welcome.ClientSize.Width && control.Bottom<=welcome.ClientSize.Height,"Welcome control is clipped: "+control.Text);
            RenderPreview(welcome,Path.Combine(args[0],"v1.2.0-welcome-preview.png"));
        }
        string passwordRoot=Path.Combine(args[0],"password-settings");
        var passwordEngine=new Engine(passwordRoot);
        string passwordDefinition=Path.Combine(passwordRoot,"DatabaseInfo.json");
        byte[] passwordExpected=File.ReadAllBytes(passwordDefinition);
        string savedPassword="test-only \"quoted\" \\ password Ω";
        using(var main=new SetupForm(passwordEngine)){
            var field=(TextBox)Get(main,"password");
            Require(field.Text=="" && field.Enabled && field.UseSystemPasswordChar,"First-run password must be blank, masked and editable");
            Require(main.GetType().GetField("fromInfo",Private)==null,"JSON password checkbox must be removed");
            field.Text=savedPassword;
            Require((bool)main.GetType().GetMethod("PersistPassword",Private).Invoke(main,null),"Password could not be saved");
        }
        Require(File.ReadAllBytes(passwordDefinition).SequenceEqual(passwordExpected),"Saving password changed database catalogue bytes");
        Require((string)SettingsConnection(passwordRoot)["password"]==savedPassword,"Password was not saved in UserSettings.json");
        using(var reopened=new SetupForm(new Engine(passwordRoot))){
            var field=(TextBox)Get(reopened,"password");
            Require(field.Text==savedPassword && field.Enabled,"Saved password must refill the editable field on reopen");
            field.Text="";
            Require((bool)reopened.GetType().GetMethod("PersistPassword",Private).Invoke(reopened,null),"Cleared password could not be saved");
        }
        Require(new Engine(passwordRoot).InfoField("Password")=="","Cleared password did not persist");
        SettingsTests(args[0]);
        FormRefreshTests(args[0]);
        BackupUiTests(args[0]);
        ProcessStartInfo preparation=InstallerSupport.PrepareWindowsStartInfo();
        Require(preparation.UseShellExecute && preparation.Verb=="runas" && preparation.WindowStyle==ProcessWindowStyle.Hidden,"Preparation must request administrator approval with a hidden helper");
        string script=Encoding.Unicode.GetString(Convert.FromBase64String(preparation.Arguments.Split(' ').Last()));
        Require(script.Contains("Set-Service -Name seclogon -StartupType Automatic") && script.Contains("Start-Service -Name seclogon"),"Preparation must target Secondary Logon");
        Console.WriteLine("Passed: setup steps, password save/reload/clearing in user settings, JSON/TXT migration, settings precedence, connection and Welcome preservation, independent catalogue updates, external settings refresh and explicit clearing, selected game reload, malformed settings protection, source credits and administrator preparation launch. No installer, service changes or database connections.");
        return 0;
    }
}
