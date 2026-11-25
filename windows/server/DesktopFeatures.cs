using System.Text.Json;

static class DesktopFeatures {
    static readonly string[] Gifts=["first-gift","webm-gift-1","webm-gift-2"];
    public static bool Reserved(string name)=> new[]{"ctrl","ctrlsup","ceo","root","sudo","rbl","rebula","admin","botmather","official","support","administrator"}.Contains(name,StringComparer.OrdinalIgnoreCase);
    public static void Init(Store db)=>db.Exec("""
        CREATE TABLE IF NOT EXISTS extras(user_id TEXT PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,clicks INTEGER NOT NULL DEFAULT 0 CHECK(clicks>=0),premium INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS gift_orders(owner TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,operation TEXT NOT NULL,gift TEXT NOT NULL,PRIMARY KEY(owner,operation));
        CREATE TABLE IF NOT EXISTS blocks(owner TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,peer TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,PRIMARY KEY(owner,peer));
        CREATE TABLE IF NOT EXISTS reports(id TEXT PRIMARY KEY,reporter TEXT NOT NULL,peer TEXT NOT NULL,reason TEXT NOT NULL,evidence TEXT NOT NULL,created TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS feed(id INTEGER PRIMARY KEY AUTOINCREMENT,owner TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,text TEXT NOT NULL,created TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS likes(post INTEGER NOT NULL REFERENCES feed(id) ON DELETE CASCADE,owner TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,PRIMARY KEY(post,owner));
        CREATE TABLE IF NOT EXISTS reactions(message INTEGER NOT NULL REFERENCES messages(seq) ON DELETE CASCADE,owner TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,PRIMARY KEY(message,owner));
        CREATE TABLE IF NOT EXISTS preferences(owner TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,key TEXT NOT NULL,value TEXT NOT NULL,PRIMARY KEY(owner,key));
        """);
    public static bool IsBlocked(Store db,string chat)=>Installation.BannedPair(db,chat)||db.One("""
        SELECT 1 FROM conversations c JOIN blocks b ON ((b.owner=c.a AND b.peer=c.b) OR (b.owner=c.b AND b.peer=c.a)) WHERE c.id=$chat
        """,("chat",chat))!=null;

    public static void Map(WebApplication app,Store db) {
        static string User(HttpContext ctx)=>(string)ctx.Items["user"]!;
        static IResult Error(string msg,int code=400)=>Results.Json(new{error=msg},statusCode:code);
        string? Peer(string chat,string uid)=>db.One("SELECT CASE WHEN a=$uid THEN b ELSE a END AS peer FROM conversations WHERE id=$chat AND (a=$uid OR b=$uid)",("uid",uid),("chat",chat))?["peer"] as string;
        long Clicks(string uid)=>Convert.ToInt64(db.One("SELECT clicks FROM extras WHERE user_id=$u",("u",uid))?["clicks"]??0);
        long Owned(string uid,string gift)=>Convert.ToInt64(db.One("SELECT COUNT(*) n FROM gift_orders g WHERE owner=$u AND gift=$g AND NOT EXISTS(SELECT 1 FROM editions e WHERE e.uid=g.owner AND e.source=g.operation)",("u",uid),("g",gift))!["n"]);
        app.MapGet("/api/gifts",(HttpContext ctx)=> Results.Ok(new{clicksBalance=Clicks(User(ctx)),items=Gifts.Select(id=>new{id,price=50,owned=Owned(User(ctx),id)}).ToArray()}));
        app.MapPost("/api/gifts/purchase",(HttpContext ctx,GiftOrder r)=>{
            var uid=User(ctx);
            if(!Gifts.Contains(r.GiftId)||!Guid.TryParse(r.OperationId,out var key))return Error("Проверьте подарок.");
            using var c=db.Open();using var tx=c.BeginTransaction();
            using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.Parameters.AddWithValue("$u",uid);cmd.Parameters.AddWithValue("$g",r.GiftId);cmd.Parameters.AddWithValue("$op",key.ToString("N"));
            cmd.CommandText="SELECT gift FROM gift_orders WHERE owner=$u AND operation=$op";
            var previous=cmd.ExecuteScalar() as string;
            if(previous!=null && previous!=r.GiftId)return Error("Этот запрос уже использован.",409);
            if(previous==null){
                cmd.CommandText="UPDATE extras SET clicks=clicks-50 WHERE user_id=$u AND clicks>=50";
                if(cmd.ExecuteNonQuery()!=1)return Error("Недостаточно Кликов.",409);
                cmd.CommandText="INSERT INTO gift_orders VALUES($u,$op,$g)";cmd.ExecuteNonQuery();
            }
            tx.Commit();return Results.Ok(new{giftId=r.GiftId,owned=Owned(uid,r.GiftId),clicksBalance=Clicks(uid)});
        });
        app.MapMethods("/api/users/{chat}/block",["POST","DELETE"],(HttpContext ctx,string chat)=>{
            var uid=User(ctx);var peer=Peer(chat,uid);if(peer==null)return Error("Чат не найден.",404);
            if(HttpMethods.IsDelete(ctx.Request.Method))db.Exec("DELETE FROM device_blocks WHERE owner=$u AND peer=$p",("u",uid),("p",peer));
            var sql=HttpMethods.IsPost(ctx.Request.Method)?"INSERT OR IGNORE INTO blocks VALUES($u,$p)":"DELETE FROM blocks WHERE owner=$u AND peer=$p";
            db.Exec(sql,("u",uid),("p",peer));db.Inbox.Notify(uid,peer);return Results.Ok(new{ok=true});
        });
        app.MapDelete("/api/users/blocked",(HttpContext ctx)=>{db.Exec("DELETE FROM blocks WHERE owner=$u; DELETE FROM device_blocks WHERE owner=$u",("u",User(ctx)));return Results.Ok(new{ok=true});});
        app.MapPost("/api/reports",(HttpContext ctx,ReportInput r)=>{
            var uid=User(ctx);var peer=Peer(r.ChatId,uid);
            if(peer==null)return Error("Чат не найден.",404);
            if(string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>100)return Error("Укажите причину.");
            var last=db.All("SELECT seq FROM messages WHERE chat_id=$c ORDER BY seq DESC LIMIT 10",("c",r.ChatId)).Select(m=>Convert.ToInt64(m["seq"])).ToHashSet();
            var history=(r.Messages??[]).Where(m=>last.Contains(m.Id)&&m.Text?.Length<=4000).TakeLast(10).ToArray();
            db.Exec("INSERT INTO reports VALUES($id,$u,$p,$r,$e,$t)",("id",Guid.NewGuid().ToString("N")),("u",uid),("p",peer),("r",r.Reason),("e",JsonSerializer.Serialize(history)),("t",DateTime.UtcNow.ToString("O")));
            return Results.Ok(new{ok=true});
        });
        app.MapMethods("/api/conversations/{chat}/messages/{id:long}",["DELETE","PATCH"],async(HttpContext ctx,string chat,long id)=>{
            var uid=User(ctx);
            if(Peer(chat,uid)==null || db.One("SELECT 1 FROM messages WHERE seq=$id AND chat_id=$c AND sender=$u",("id",id),("c",chat),("u",uid))==null)return Error("Сообщение не найдено.",404);
            if(HttpMethods.IsDelete(ctx.Request.Method))db.Exec("DELETE FROM messages WHERE seq=$id AND sender=$u",("u",uid),("id",id));
            else{
                var body=(await ctx.Request.ReadFromJsonAsync<NewMessage>())?.Body?.Trim();
                if(!SignalDirectory.ValidEnvelope(db,chat,uid,body))return Error("Обновите ключи устройств.",409);
                db.Exec("UPDATE messages SET body=$b WHERE seq=$id AND sender=$u",("b",body),("u",uid),("id",id));
            }
            db.Inbox.NotifyChat(db,chat);
            return Results.Ok(new{ok=true});
        });
        app.MapPost("/api/conversations/{chat}/messages/{id:long}/reaction",(HttpContext ctx,string chat,long id)=>{
            var uid=User(ctx);if(Peer(chat,uid)==null||db.One("SELECT 1 FROM messages WHERE seq=$id AND chat_id=$c",("id",id),("c",chat))==null)return Error("Сообщение не найдено.",404);
            Toggle("reactions","message",id,uid);db.Inbox.NotifyChat(db,chat);return Results.Ok(new{ok=true});
        });
        void Toggle(string table,string column,long id,string uid){
            using var c=db.Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$u",uid);
            cmd.CommandText=$"DELETE FROM {table} WHERE {column}=$id AND owner=$u";
            if(cmd.ExecuteNonQuery()==0){cmd.CommandText=$"INSERT INTO {table}({column},owner) VALUES($id,$u)";cmd.ExecuteNonQuery();}
            tx.Commit();
        }
        app.MapGet("/api/feed",(HttpContext ctx)=>Results.Ok(new{posts=db.All("""
            SELECT f.id,u.username AS 'by',f.text,f.created AS t,(SELECT count(*) FROM likes l WHERE l.post=f.id) likes,
            EXISTS(SELECT 1 FROM likes l WHERE l.post=f.id AND owner=$u) liked FROM feed f JOIN users u ON u.id=f.owner
            WHERE NOT EXISTS(SELECT 1 FROM blocks b WHERE (b.owner=$u AND b.peer=f.owner) OR (b.owner=f.owner AND b.peer=$u)) ORDER BY f.id DESC LIMIT 100
            """,("u",User(ctx)))}));
        app.MapPost("/api/feed",async(HttpContext ctx,PostInput r)=>{
            var body=r.Text?.Trim();if(string.IsNullOrEmpty(body)||body.Length>4000)return Error("Введите текст до 4000 символов.");
            if(await Moderation.Hold(body,ctx.RequestAborted)){db.Exec("INSERT INTO publications(uid,kind,body,created,visible) VALUES($u,'feed',$b,$t,0)",("u",User(ctx)),("b",body),("t",DateTimeOffset.UtcNow.ToUnixTimeSeconds()));return Results.Ok(new{pending=true});}
            db.Exec("INSERT INTO feed(owner,text,created) VALUES($u,$t,$d)",("u",User(ctx)),("t",body),("d",DateTime.UtcNow.ToString("O")));return Results.Ok(new{ok=true});
        });
        app.MapPost("/api/feed/posts/{id:long}/likes",(HttpContext ctx,long id)=>{
            if(db.One("SELECT 1 FROM feed WHERE id=$id",("id",id))==null)return Error("Публикация не найдена.",404);
            Toggle("likes","post",id,User(ctx));return Results.Ok(new{ok=true});
        });
        app.MapGet("/api/me/settings",(HttpContext ctx)=>Results.Ok(db.All("SELECT key,value FROM preferences WHERE owner=$u",("u",User(ctx)))));
        app.MapPatch("/api/me/settings",(HttpContext ctx,PrefInput r)=>{
            if(!new[]{"last","rec","ntf","pre","grp","mkt","auto","bal","fontSize","seenMode"}.Contains(r.Key)||r.Value.GetRawText().Length>80)return Error("Настройка недоступна.");
            db.Exec("INSERT INTO preferences VALUES($u,$k,$v) ON CONFLICT(owner,key) DO UPDATE SET value=excluded.value",("u",User(ctx)),("k",r.Key),("v",r.Value.GetRawText()));return Results.Ok(new{ok=true});
        });
        app.MapDelete("/api/me",(HttpContext ctx)=>{
            var uid=User(ctx);
            db.Exec("""
                BEGIN IMMEDIATE;
                DELETE FROM messages WHERE chat_id IN(SELECT id FROM conversations WHERE a=$u OR b=$u);
                DELETE FROM conversations WHERE a=$u OR b=$u;
                DELETE FROM nft_members WHERE user_id=$u;
                DELETE FROM users WHERE id=$u;
                COMMIT;
                """,("u",uid));
            return Results.Ok(new{ok=true});
        });
    }
}
record GiftOrder(string GiftId,string OperationId);
record PostInput(string Text);
record Evidence(long Id,string Text);
record ReportInput(string ChatId,string Reason,Evidence[]? Messages);
record PrefInput(string Key,JsonElement Value);

