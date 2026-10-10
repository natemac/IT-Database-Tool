using System;
using System.IO;
using System.Linq;
using ITDatabaseSetup;

// Invoked only by the native suite against its owned port-15583 cluster.
class RecordedRestoreTest
{
    static int Main(string[] args)
    {
        var engine=new Engine(args[0]);
        var connection=new Connection {server="127.0.0.1",port=Int32.Parse(args[2]),username="postgres",pgBin=args[1]};
        string database=args[3];
        var record=engine.BackupHistory(connection,database).Single(item=>item.file==args[4]);
        var confirmed=engine.PrepareRecordedRestore(connection,database,record);
        engine.SelectRecordedBackup(connection,database,record);
        if(args[5]=="changed"){
            var definition=Engine.Json.Deserialize<DatabaseDefinition>(File.ReadAllText(engine.DefinitionPath));
            definition.games.Single(item=>item.database==database).databaseFile=Path.Combine(args[0],"different-source.backup");
            File.WriteAllText(engine.DefinitionPath,Engine.PrettyJson(new {schemaVersion=1,games=definition.games}));
            try{engine.Build(connection,Environment.GetEnvironmentVariable("PGPASSWORD"),false,entry=>{},new[]{database},new[]{database},confirmed);}
            catch(Exception ex){if(ex.Message.Contains("restore file changed")){Console.WriteLine("changed source rejected");return 0;}throw;}
            throw new Exception("Changed source was accepted");
        }
        var report=engine.Build(connection,Environment.GetEnvironmentVariable("PGPASSWORD"),false,entry=>{},new[]{database},new[]{database},confirmed);
        Console.WriteLine(Engine.Json.Serialize(report));return 0;
    }
}
