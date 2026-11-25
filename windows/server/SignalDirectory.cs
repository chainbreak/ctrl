using System.Text.Json;
using Microsoft.Data.Sqlite;

static class SignalDirectory {
    static bool B64(string? s,int bytes){if(s is null)return false;try{return Convert.FromBase64String(s).Length==bytes;}catch(FormatException){return false;}}
    static bool Id(int n)=>n is >0 and <=1073741823;
    public static void Map(WebApplication app,Store db) {
        db.Exec("""
            CREATE TABLE IF NOT EXISTS devices(uid TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,d INTEGER NOT NULL,identity TEXT NOT NULL,registration INTEGER NOT NULL,spkid INTEGER NOT NULL,spk TEXT NOT NULL,sig TEXT NOT NULL,PRIMARY KEY(uid,d));
            CREATE TABLE IF NOT EXISTS prekeys(uid TEXT NOT NULL,d INTEGER NOT NULL,n INTEGER NOT NULL,pub TEXT NOT NULL,used INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(uid,d,n),FOREIGN KEY(uid,d) REFERENCES devices(uid,d) ON DELETE CASCADE);
            """);
        app.MapPost("/api/signal/device",(HttpContext ctx,DeviceInput r)=>{
            var uid=(string)ctx.Items["user"]!;
            if(!Id(r.Device)||!Id(r.Registration)||!Id(r.SignedId)||!B64(r.Identity,33)||!B64(r.SignedKey,33)||!B64(r.Signature,64)||
               r.Keys==null||r.Keys.Length>64||r.Keys.Any(k=>!Id(k.Id)||!B64(k.Key,33)))return Results.BadRequest();
            using var c=db.Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.Parameters.AddWithValue("$u",uid);cmd.Parameters.AddWithValue("$d",r.Device);cmd.CommandText="SELECT identity FROM devices WHERE uid=$u AND d=$d";
            var old=cmd.ExecuteScalar() as string;if(old!=null && old!=r.Identity)return Results.Conflict();
            cmd.CommandText="SELECT COUNT(*) FROM devices WHERE uid=$u";if(old==null && Convert.ToInt64(cmd.ExecuteScalar())>=8)return Results.Conflict(new{error="Лимит устройств на аккаунт — восемь."});
            cmd.CommandText="INSERT INTO devices VALUES($u,$d,$i,$r,$s,$p,$g) ON CONFLICT(uid,d) DO UPDATE SET registration=excluded.registration,spkid=excluded.spkid,spk=excluded.spk,sig=excluded.sig";
            foreach(var (key,val) in new (string,object)[]{("i",r.Identity),("r",r.Registration),("s",r.SignedId),("p",r.SignedKey),("g",r.Signature)})cmd.Parameters.AddWithValue("$"+key,val);
            cmd.ExecuteNonQuery();cmd.CommandText="INSERT OR IGNORE INTO prekeys(uid,d,n,pub) VALUES($u,$d,$n,$b)";
            cmd.Parameters.Add("$n",SqliteType.Integer);cmd.Parameters.Add("$b",SqliteType.Text);
            foreach(var k in r.Keys){cmd.Parameters["$n"].Value=k.Id;cmd.Parameters["$b"].Value=k.Key;cmd.ExecuteNonQuery();}
            cmd.CommandText="SELECT COUNT(*) FROM prekeys WHERE uid=$u AND d=$d AND used=0";var available=Convert.ToInt32(cmd.ExecuteScalar());tx.Commit();return Results.Ok(new{available});
        });
        app.MapGet("/api/signal/chats/{chat}/devices",(HttpContext ctx,string chat)=>{
            var u=(string)ctx.Items["user"]!;
            if(!Participant(db,chat,u))return Results.NotFound();
            return Results.Ok(db.All("SELECT uid,d AS device,identity,registration,spkid AS signedId,spk AS signedKey,sig AS signature FROM devices WHERE uid IN("+MemberList+") ORDER BY uid,d",("c",chat)));
        });
        app.MapPost("/api/signal/chats/{chat}/key",(HttpContext ctx,string chat,KeyRequest r)=>{
            var u=(string)ctx.Items["user"]!;
            if(!Participant(db,chat,u)||!Participant(db,chat,r.Uid))return Results.NotFound();
            var key=db.One("UPDATE prekeys SET used=1 WHERE rowid=(SELECT rowid FROM prekeys WHERE uid=$u AND d=$d AND used=0 ORDER BY n LIMIT 1) RETURNING n AS id,pub AS key",("u",r.Uid),("d",r.Device));
            return Results.Ok(new{preKey=key});
        }).RequireRateLimiting("key-requests");
    }
    const string MemberList="SELECT a AS member FROM conversations WHERE id=$c UNION SELECT b FROM conversations WHERE id=$c UNION SELECT m.uid FROM memberships m JOIN rooms r ON m.room=r.id WHERE r.id=$c AND r.kind='group'";
    static bool Participant(Store db,string chat,string uid)=>db.One("SELECT 1 FROM ("+MemberList+") WHERE member=$u",("u",uid),("c",chat))!=null;
    public static bool ValidEnvelope(Store db,string chat,string uid,string? body) {
        if(body==null||!body.StartsWith("SIG1:",StringComparison.Ordinal)||body.Length>400000)return false;
        try{
            using var doc=JsonDocument.Parse(body[5..]);var value=doc.RootElement;
            int sender=value.GetProperty("from").GetInt32();
            if(!Guid.TryParse(value.GetProperty("event").GetString(),out _))return false;
            var devices=db.All("SELECT uid,d FROM devices WHERE uid IN("+MemberList+")",("c",chat));
            var expected=devices.Select(x=>x["uid"]+"|"+x["d"]).ToHashSet();
            if(!expected.Remove(uid+"|"+sender))return false;
            if(db.One("SELECT 1 FROM ("+MemberList+") m WHERE NOT EXISTS(SELECT 1 FROM devices d WHERE d.uid=m.member)",("c",chat))!=null)return false;
            var boxes=value.GetProperty("boxes");
            if(boxes.ValueKind!=JsonValueKind.Array||boxes.GetArrayLength()!=expected.Count)return false;
            foreach(var box in boxes.EnumerateArray()){
                if(!expected.Remove(box.GetProperty("uid").GetString()+"|"+box.GetProperty("device").GetInt32()))return false;
                if(box.GetProperty("type").GetInt32() is not(1 or 3))return false;
                var payload=box.GetProperty("body").GetString();
                if(payload==null||payload.Length>40000||Convert.FromBase64String(payload).Length<16)return false;
            }
            return expected.Count==0;
        }catch(Exception e)when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException){return false;}
    }
}
record PublicKeyInput(int Id,string Key);
record DeviceInput(int Device,int Registration,string Identity,int SignedId,string SignedKey,string Signature,PublicKeyInput[] Keys);
record KeyRequest(string Uid,int Device);

