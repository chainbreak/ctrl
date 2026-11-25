using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

static class Community {
    static string U(HttpContext ctx)=>(string)ctx.Items["user"]!;
    static long Now()=>DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static IResult Error(string message,int status=400)=>Results.Json(new{error=message},statusCode:status);
    static readonly string[] Terms=(Environment.GetEnvironmentVariable("CTRL_REVIEW_TERMS")??"").Split('|',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
    public static bool NeedsReview(string text) {
        var value=text.Normalize(NormalizationForm.FormKC);
        return value.EnumerateRunes().Any(r=>r.Value is >=0x202a and <=0x202e or >=0x2066 and <=0x2069)
            || Terms.Any(term=>value.Contains(term,StringComparison.OrdinalIgnoreCase));
    }
    public static bool Banned(Store db,string id)=>db.One("SELECT 1 FROM account_options WHERE uid=$u AND banned=1",("u",id))!=null;
    public static void Init(Store db)=>db.Exec("""
        CREATE TABLE IF NOT EXISTS account_options(uid TEXT PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,fee INTEGER NOT NULL DEFAULT 0,frame TEXT NOT NULL DEFAULT '',banned INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS frames(uid TEXT REFERENCES users(id) ON DELETE CASCADE,frame TEXT,PRIMARY KEY(uid,frame));
        CREATE TABLE IF NOT EXISTS rooms(id TEXT PRIMARY KEY,owner TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,title TEXT NOT NULL,kind TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS memberships(room TEXT REFERENCES rooms(id) ON DELETE CASCADE,uid TEXT REFERENCES users(id) ON DELETE CASCADE,PRIMARY KEY(room,uid));
        CREATE TABLE IF NOT EXISTS muted_rooms(uid TEXT REFERENCES users(id) ON DELETE CASCADE,room TEXT REFERENCES rooms(id) ON DELETE CASCADE,PRIMARY KEY(uid,room));
        CREATE TABLE IF NOT EXISTS publications(id INTEGER PRIMARY KEY AUTOINCREMENT,uid TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,room TEXT REFERENCES rooms(id) ON DELETE CASCADE,kind TEXT NOT NULL,body TEXT NOT NULL,created INTEGER NOT NULL,visible INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS support(id TEXT PRIMARY KEY,claim TEXT NOT NULL,email TEXT NOT NULL,body TEXT NOT NULL,answer TEXT NOT NULL DEFAULT '',created INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS staff_actions(id TEXT PRIMARY KEY,actor TEXT NOT NULL,kind TEXT NOT NULL,target TEXT NOT NULL,amount INTEGER NOT NULL,created INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS notices(id INTEGER PRIMARY KEY AUTOINCREMENT,uid TEXT REFERENCES users(id) ON DELETE CASCADE,body TEXT NOT NULL,created INTEGER NOT NULL);
        """);
    public static IResult SaveMessage(Store db,string chat,string user,string client,string body,long? acceptedFee,string? fileId) {
        using var c=db.Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.Parameters.AddWithValue("$c",chat);cmd.Parameters.AddWithValue("$u",user);cmd.Parameters.AddWithValue("$n",client);
        cmd.CommandText="SELECT seq FROM messages WHERE chat_id=$c AND sender=$u AND client_id=$n";
        if(cmd.ExecuteScalar()==null){
            cmd.CommandText="SELECT CASE WHEN a=$u THEN b ELSE a END FROM conversations WHERE id=$c";
            var peer=(string)cmd.ExecuteScalar()!;cmd.Parameters.AddWithValue("$p",peer);
            cmd.CommandText="SELECT fee FROM account_options WHERE uid=$p";long fee=Convert.ToInt64(cmd.ExecuteScalar()??0);
            if(fee!=Math.Max(0,acceptedFee??0))return Error("Цена сообщения изменилась. Откройте чат ещё раз.",409);
            if(fee>0){
                cmd.Parameters.AddWithValue("$f",fee);cmd.CommandText="UPDATE extras SET clicks=clicks-$f WHERE user_id=$u AND clicks>=$f";
                if(cmd.ExecuteNonQuery()!=1)return Error("Недостаточно Кликов для сообщения.",402);
                cmd.CommandText="INSERT INTO extras(user_id,clicks) VALUES($p,$f) ON CONFLICT(user_id) DO UPDATE SET clicks=clicks+$f";cmd.ExecuteNonQuery();
            }
            cmd.CommandText="INSERT INTO messages(chat_id,sender,client_id,body,created) VALUES($c,$u,$n,$b,$t)";
            cmd.Parameters.AddWithValue("$b",body);cmd.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
            if(fileId!=null){
                cmd.Parameters.AddWithValue("$file",fileId);
                cmd.CommandText="INSERT OR IGNORE INTO posted_files SELECT id,(SELECT seq FROM messages WHERE chat_id=$c AND sender=$u AND client_id=$n) FROM uploads WHERE id=$file AND owner=$u AND chat=$c AND ready=1";
                if(cmd.ExecuteNonQuery()!=1)return Error("Вложение не готово к отправке.",409);
            }
        }
        tx.Commit();db.Inbox.NotifyChat(db,chat);return Results.Ok(db.One("SELECT seq,client_id clientId,sender,body,created FROM messages WHERE chat_id=$c AND sender=$u AND client_id=$n",("c",chat),("u",user),("n",client)));
    }
    public static void Map(WebApplication app,Store db){
        if(!db.All("PRAGMA table_info(publications)").Any(x=>(string)x["name"]! == "nonce"))db.Exec("ALTER TABLE publications ADD COLUMN nonce TEXT");
        db.Exec("CREATE UNIQUE INDEX IF NOT EXISTS group_message_nonce ON publications(room,uid,nonce) WHERE nonce IS NOT NULL");
        app.MapPost("/api/support",(SupportRequest r)=>{
            if(string.IsNullOrWhiteSpace(r.Body)||r.Body.Length>3000||r.Email?.Length>200||!System.Net.Mail.MailAddress.TryCreate(r.Email,out var email)||email.Address!=r.Email)return Error("Укажите почту и опишите вопрос.");
            var id=Guid.NewGuid().ToString("N");var claim=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            db.Exec("INSERT INTO support(id,claim,email,body,created) VALUES($i,$k,$e,$b,$t)",("i",id),("k",Crypto.Hash(claim)),("e",email.Address),("b",r.Body.Trim()),("t",Now()));
            return Results.Ok(new{id,claim});
        }).RequireRateLimiting("auth");
        app.MapPost("/api/support/status",(TicketLookup r)=>Results.Ok(db.One("SELECT body,answer,created FROM support WHERE id=$i AND claim=$k",("i",r.Id),("k",Crypto.Hash(r.Claim??"")))??new()));
        app.MapGet("/api/community",(HttpContext ctx)=>{
            var uid=U(ctx);var rooms=db.All("SELECT r.*,EXISTS(SELECT 1 FROM noer WHERE room=r.id AND state='approved') verified,EXISTS(SELECT 1 FROM memberships WHERE room=r.id AND uid=$u) joined FROM rooms r WHERE (id IN(SELECT room FROM memberships WHERE uid=$u) OR kind='channel') AND NOT EXISTS(SELECT 1 FROM muted_rooms WHERE uid=$u AND room=r.id) ORDER BY joined DESC,title LIMIT 100",("u",uid));
            var stories=db.All("SELECT p.id,p.body,u.username FROM publications p JOIN users u ON u.id=p.uid WHERE p.kind='story' AND p.visible=1 AND p.created>$t AND NOT EXISTS(SELECT 1 FROM blocks b WHERE (b.owner=$u AND b.peer=p.uid) OR (b.peer=$u AND b.owner=p.uid)) ORDER BY id DESC LIMIT 60",("u",uid),("t",Now()-86400));
            return Results.Ok(new{rooms,stories,notices=db.All("SELECT body FROM notices WHERE uid=$u ORDER BY id DESC LIMIT 15",("u",uid))});
        });
        app.MapPost("/api/community/rooms",(HttpContext ctx,RoomInput r)=>{
            var uid=U(ctx);var title=r.Title?.Trim();if(title?.Length is null or <1 or >70 || r.Kind is not("channel" or "group")||NeedsReview(title))return Error("Проверьте название.");
            using var c=db.Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.Parameters.AddWithValue("$u",uid);
            cmd.CommandText="SELECT count(*) >= CASE WHEN COALESCE((SELECT premium FROM extras WHERE user_id=$u),0)>0 THEN 20 ELSE 10 END FROM rooms WHERE owner=$u AND kind='channel'";
            if(r.Kind=="channel"&&Convert.ToInt64(cmd.ExecuteScalar())!=0)return Error("Достигнут лимит каналов.",409);
            var id=Guid.NewGuid().ToString("N");cmd.Parameters.AddWithValue("$i",id);cmd.Parameters.AddWithValue("$t",title);cmd.Parameters.AddWithValue("$k",r.Kind);
            cmd.CommandText="INSERT INTO rooms VALUES($i,$u,$t,$k);INSERT INTO memberships VALUES($i,$u)";cmd.ExecuteNonQuery();tx.Commit();return Results.Ok(new{id});
        });
        app.MapPost("/api/community/{id}/join",(HttpContext ctx,string id)=>{
            if(db.One("SELECT 1 FROM rooms WHERE id=$i",("i",id))==null)return Results.NotFound();
            db.Exec("BEGIN IMMEDIATE;DELETE FROM muted_rooms WHERE room=$i AND uid=$u;INSERT OR IGNORE INTO memberships VALUES($i,$u);COMMIT;",("i",id),("u",U(ctx)));return Results.Ok(new{ok=true});
        });
        app.MapPost("/api/community/{id}/mute",(HttpContext ctx,string id)=>{
            if(db.One("SELECT 1 FROM rooms WHERE id=$i",("i",id))==null)return Results.NotFound();
            db.Exec("INSERT OR IGNORE INTO muted_rooms VALUES($u,$i)",("u",U(ctx)),("i",id));return Results.Ok(new{ok=true});
        });
        app.MapPost("/api/community/{id}/report",(HttpContext ctx,string id,RoomReport r)=>{
            var uid=U(ctx);var room=db.One("SELECT title,kind FROM rooms WHERE id=$i AND (kind='channel' OR EXISTS(SELECT 1 FROM memberships WHERE room=$i AND uid=$u))",("i",id),("u",uid));if(room==null)return Results.NotFound();
            var allowed=db.All("SELECT id FROM publications WHERE room=$i ORDER BY id DESC LIMIT 10",("i",id)).Select(x=>Convert.ToInt64(x["id"])).ToHashSet();
            var evidence=(r.Messages??[]).Where(x=>allowed.Contains(x.Id)&&x.Text?.Length<=4000).Take(5).ToArray();
            db.Exec("INSERT INTO reports VALUES($id,$u,$p,$r,$e,$t)",("id",Guid.NewGuid().ToString("N")),("u",uid),("p","room:"+id),("r","Жалоба на "+room["title"]),("e",JsonSerializer.Serialize(evidence)),("t",DateTime.UtcNow.ToString("O")));
            return Results.Ok(new{ok=true});
        }).RequireRateLimiting("auth");
        app.MapGet("/api/community/{id}/posts",(HttpContext ctx,string id)=>{
            if(db.One("SELECT 1 FROM memberships WHERE room=$i AND uid=$u",("i",id),("u",U(ctx)))==null)return Results.NotFound();
            return Results.Ok(db.All("SELECT p.id,p.body,p.uid AS sender,u.username,p.created FROM publications p JOIN users u ON u.id=p.uid WHERE p.room=$i AND p.visible=1 ORDER BY p.id DESC LIMIT 100",("i",id)));
        });
        app.MapPost("/api/community/group-message",(HttpContext ctx,GroupMessage r)=>{
            var uid=U(ctx);if(db.One("SELECT 1 FROM memberships m JOIN rooms r ON room=r.id WHERE room=$i AND uid=$u AND kind='group'",("i",r.Room),("u",uid))==null)return Results.NotFound();
            if(!SignalDirectory.ValidEnvelope(db,r.Room,uid,r.Body))return Error("Участникам нужно открыть Ctrl, чтобы получить ключи устройств.",409);
            if(!Guid.TryParse(r.ClientId,out var nonce))return Results.BadRequest();
            db.Exec("INSERT OR IGNORE INTO publications(uid,room,kind,body,created,visible,nonce) VALUES($u,$r,'groupmsg',$b,unixepoch(),1,$n)",("u",uid),("r",r.Room),("b",r.Body),("n",nonce.ToString("N")));return Results.Ok(new{ok=true});
        });
        app.MapPost("/api/community/publish",async(HttpContext ctx,Publication r)=>{
            var uid=U(ctx);var text=r.Text?.Trim();if(text?.Length is null or <1 or >2000 || r.Kind is not("story" or "post"))return Error("Введите текст до 2000 символов.");
            if(r.Kind=="post"&&db.One("SELECT 1 FROM rooms JOIN memberships ON room=id WHERE id=$i AND uid=$u AND owner=$u AND kind='channel'",("i",r.Room),("u",uid))==null)return Results.StatusCode(403);
            bool pending=await Moderation.Hold(text,ctx.RequestAborted);
            db.Exec("DELETE FROM publications WHERE kind='story' AND created<=$old;INSERT INTO publications(uid,room,kind,body,created,visible) VALUES($u,$r,$k,$b,$t,$v)",("old",Now()-86400),("u",uid),("r",r.Kind=="story"?null:r.Room),("k",r.Kind),("b",text),("t",Now()),("v",!pending));
            return Results.Ok(new{pending});
        });
        app.MapGet("/api/me/options",(HttpContext ctx)=>Results.Ok(new{settings=db.One("SELECT fee,frame FROM account_options WHERE uid=$u",("u",U(ctx))),frames=db.All("SELECT frame FROM frames WHERE uid=$u",("u",U(ctx)))}));
        app.MapPost("/api/me/options",(HttpContext ctx,UserOptions r)=>{
            if(r.Fee is <0 or >10000||r.Frame is not("" or "silver" or "graphite"))return Error("Проверьте параметры профиля.");
            var uid=U(ctx);using var c=db.Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.Parameters.AddWithValue("$u",uid);cmd.Parameters.AddWithValue("$f",r.Frame);cmd.Parameters.AddWithValue("$price",r.Fee);
            cmd.CommandText="SELECT 1 FROM frames WHERE uid=$u AND frame=$f";
            if(r.Frame!=""&&cmd.ExecuteScalar()==null){
                cmd.CommandText="UPDATE extras SET clicks=clicks-25 WHERE user_id=$u AND clicks>=25";if(cmd.ExecuteNonQuery()!=1)return Error("Для рамки нужно 25 Кликов.",402);
                cmd.CommandText="INSERT INTO frames VALUES($u,$f)";cmd.ExecuteNonQuery();
            }
            cmd.CommandText="INSERT INTO account_options(uid,fee,frame) VALUES($u,$price,$f) ON CONFLICT(uid) DO UPDATE SET fee=$price,frame=$f";cmd.ExecuteNonQuery();tx.Commit();return Results.Ok(new{ok=true});
        });
    }
}
record SupportRequest(string Email,string Body);
record TicketLookup(string Id,string Claim);
record RoomInput(string Title,string Kind);
record Publication(string Kind,string? Room,string Text);
record UserOptions(long Fee,string Frame);


record GroupMessage(string Room,string Body,string ClientId);

record RoomReport(Evidence[]? Messages);
