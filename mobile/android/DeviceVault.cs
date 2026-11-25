using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ctrl.Mobile;
sealed class DeviceVault {
    readonly string connection;
    public DeviceVault(string root,string hex) {
        SQLitePCL.Batteries_V2.Init();
        connection=new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"device-vault.db"),Password=hex}.ToString();
        using var db=new SqliteConnection(connection); db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA cipher_version";
        if(cmd.ExecuteScalar() is not string)throw new InvalidOperationException("Не удалось открыть защищённое хранилище.");
        cmd.CommandText="CREATE TABLE IF NOT EXISTS entries(scope TEXT,key TEXT,value TEXT NOT NULL,PRIMARY KEY(scope,key))";cmd.ExecuteNonQuery();
    }
    public object? Run(string scope,JsonElement args){
        using var db=new SqliteConnection(connection);db.Open();using var cmd=db.CreateCommand();cmd.Parameters.AddWithValue("$s",scope);
        if(args.TryGetProperty("changes",out var changes)){
            using var tx=db.BeginTransaction();cmd.Transaction=tx;
            cmd.Parameters.Add("$k",SqliteType.Text);cmd.Parameters.Add("$v",SqliteType.Text);
            foreach(var item in changes.EnumerateObject()){
                cmd.Parameters["$k"].Value=item.Name;
                if(item.Value.ValueKind==JsonValueKind.Null){cmd.Parameters["$v"].Value="";cmd.CommandText="DELETE FROM entries WHERE scope=$s AND key=$k";}
                else{cmd.Parameters["$v"].Value=item.Value.GetRawText();cmd.CommandText="INSERT INTO entries VALUES($s,$k,$v) ON CONFLICT(scope,key) DO UPDATE SET value=excluded.value";}
                cmd.ExecuteNonQuery();
            }
            tx.Commit();return null;
        }
        cmd.CommandText="SELECT value FROM entries WHERE scope=$s AND key=$k";cmd.Parameters.AddWithValue("$k",args.GetProperty("key").GetString()!);
        if(cmd.ExecuteScalar() is not string raw)return null;
        using var value=JsonDocument.Parse(raw);return value.RootElement.Clone();
    }
}

