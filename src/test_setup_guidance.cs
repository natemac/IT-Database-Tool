using System;
using System.Diagnostics;
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
        var passwordExpected=Engine.Json.Deserialize<DatabaseDefinition>(File.ReadAllText(passwordDefinition));
        string savedPassword="test-only \"quoted\" \\ password Ω";
        using(var main=new SetupForm(passwordEngine)){
            var field=(TextBox)Get(main,"password");
            Require(field.Text=="" && field.Enabled && field.UseSystemPasswordChar,"First-run password must be blank, masked and editable");
            Require(main.GetType().GetField("fromInfo",Private)==null,"JSON password checkbox must be removed");
            field.Text=savedPassword;
            Require((bool)main.GetType().GetMethod("PersistPassword",Private).Invoke(main,null),"Password could not be saved");
        }
        passwordExpected.connection.password=savedPassword;
        Require(Engine.Json.Serialize(Engine.Json.Deserialize<DatabaseDefinition>(File.ReadAllText(passwordDefinition)))==Engine.Json.Serialize(passwordExpected),"Saving password changed other settings or games");
        using(var reopened=new SetupForm(new Engine(passwordRoot))){
            var field=(TextBox)Get(reopened,"password");
            Require(field.Text==savedPassword && field.Enabled,"Saved password must refill the editable field on reopen");
            field.Text="";
            Require((bool)reopened.GetType().GetMethod("PersistPassword",Private).Invoke(reopened,null),"Cleared password could not be saved");
        }
        Require(new Engine(passwordRoot).InfoField("Password")=="","Cleared password did not persist");
        ProcessStartInfo preparation=InstallerSupport.PrepareWindowsStartInfo();
        Require(preparation.UseShellExecute && preparation.Verb=="runas" && preparation.WindowStyle==ProcessWindowStyle.Hidden,"Preparation must request administrator approval with a hidden helper");
        string script=Encoding.Unicode.GetString(Convert.FromBase64String(preparation.Arguments.Split(' ').Last()));
        Require(script.Contains("Set-Service -Name seclogon -StartupType Automatic") && script.Contains("Start-Service -Name seclogon"),"Preparation must target Secondary Logon");
        Console.WriteLine("Passed: all setup steps, action states, blank/editable/saved password reload and clearing, unchanged game definitions, source credits and administrator preparation launch. No installer, service changes or database connections.");
        return 0;
    }
}
