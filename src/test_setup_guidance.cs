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
        var engine=new Engine(folder);
        string catalogue=Path.Combine(folder,"DatabaseInfo.json"),settings=Path.Combine(folder,"UserSettings.json");
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

            list.Items[0].Checked=true;
            password.Text="";
            Require((bool)Get(form,"passwordEdited"),"Explicit password clearing must count as a user edit");
            external.SavePassword("later-external-fixture-password");
            ScanFilesOnly(form);
            Require(password.Text=="" && (bool)Get(form,"passwordEdited") && list.Items[0].Checked,"File-only refresh lost an explicit password clearing or game selection");
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
        using(var welcome=new WelcomeForm(false)){
            Require(welcome.Controls.OfType<LinkLabel>().Single().Text.Contains("Eggmansworld/tp-it-guides"),"Welcome GitHub link missing");
            Require(welcome.Controls.OfType<Label>().Any(label=>label.Text.Contains("Info courtesy of Eggmansworld")),"Welcome author credit missing");
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
        ProcessStartInfo preparation=InstallerSupport.PrepareWindowsStartInfo();
        Require(preparation.UseShellExecute && preparation.Verb=="runas" && preparation.WindowStyle==ProcessWindowStyle.Hidden,"Preparation must request administrator approval with a hidden helper");
        string script=Encoding.Unicode.GetString(Convert.FromBase64String(preparation.Arguments.Split(' ').Last()));
        Require(script.Contains("Set-Service -Name seclogon -StartupType Automatic") && script.Contains("Start-Service -Name seclogon"),"Preparation must target Secondary Logon");
        Console.WriteLine("Passed: setup steps, password save/reload/clearing in user settings, JSON/TXT migration, settings precedence, connection and Welcome preservation, independent catalogue updates, external settings refresh and explicit clearing, selected game reload, malformed settings protection, source credits and administrator preparation launch. No installer, service changes or database connections.");
        return 0;
    }
}
