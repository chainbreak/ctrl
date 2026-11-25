using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
static class Staff {
    static IResult Error(string s,int code=400)=>Results.Json(new{error=s},statusCode:code);
    public static void Map(WebApplication app,Store db){
        db.Exec("""
          CREATE TABLE IF NOT EXISTS noer(id TEXT PRIMARY KEY,uid TEXT REFERENCES users(id) ON DELETE CASCADE,room TEXT REFERENCES rooms(id) ON DELETE CASCADE,evidence BLOB,mime TEXT NOT NULL,state TEXT NOT NULL,created INTEGER NOT NULL);
          CREATE UNIQUE INDEX IF NOT EXISTS noer_target ON noer(uid,COALESCE(room,'')) WHERE state IN('submitted','paid','approved');
        """);
        var protector=app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("ctrl.noer.v1");
        app.MapGet("/api/noer",(HttpContext ctx)=>Results.Ok(db.All("SELECT id,room,state FROM noer WHERE uid=$u ORDER BY created DESC",("u",ctx.Items["user"]))));
        app.MapPost("/api/noer",(HttpContext ctx,NoerInput r)=>{
            var uid=(string)ctx.Items["user"]!;var room=string.IsNullOrWhiteSpace(r.Room)?null:r.Room;
            if(room!=null&&db.One("SELECT 1 FROM rooms WHERE id=$r AND owner=$u",("r",room),("u",uid))==null)return Results.NotFound();
            if(r.Document==null||r.Document.Length>2800000)return Error("Выберите PNG, JPEG или PDF до 2 МБ.");
            byte[] doc;try{doc=Convert.FromBase64String(r.Document);}catch(FormatException){return Results.BadRequest();}
            string mime=doc.AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10})?"image/png":doc.AsSpan().StartsWith(new byte[]{255,216,255})?"image/jpeg":doc.AsSpan().StartsWith("%PDF-"u8)?"application/pdf":"";
            if(doc.Length is <8 or >2000000||mime=="")return Error("Формат документа не поддерживается.");
            var id=Guid.NewGuid().ToString("N");
            var result=db.One("INSERT OR IGNORE INTO noer VALUES($i,$u,$r,$b,$m,'submitted',unixepoch()) RETURNING id",("i",id),("u",uid),("r",room),("b",protector.Protect(doc)),("m",mime));
            CryptographicOperations.ZeroMemory(doc);return result==null?Error("Заявка уже существует.",409):Results.Ok(new{id});
        });
        app.MapPost("/management",(HttpContext ctx,StaffInput r)=>{
            var secret=Environment.GetEnvironmentVariable("CTRL_MANAGEMENT_KEY");
            if(secret==null||secret.Length<32||!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)),SHA256.HashData(Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Ctrl-Management"].ToString()))))return Results.NotFound();
            if(string.IsNullOrWhiteSpace(r.Actor)||r.Actor.Length>200||r.Target?.Length>180)return Results.BadRequest();
            var target=r.Target??"";
            if(r.Kind=="users")return Results.Ok(db.All("SELECT u.id,u.username,u.name,COALESCE(e.clicks,0) clicks,COALESCE(e.premium,0) premium,COALESCE(o.banned,0) banned FROM users u LEFT JOIN extras e ON e.user_id=u.id LEFT JOIN account_options o ON o.uid=u.id WHERE length($q)>=2 AND (substr(u.username,1,length($q))=$q OR u.id=$q) LIMIT 40",("q",target.ToLowerInvariant())));
            if(r.Kind=="queue")return Results.Ok(new{
                tickets=db.All("SELECT id,email,body,answer FROM support WHERE answer='' ORDER BY created LIMIT 40"),
                complaints=db.All("SELECT * FROM reports ORDER BY created DESC LIMIT 40"),
                publications=db.All("SELECT id,body,kind FROM publications WHERE visible=0 ORDER BY id LIMIT 40"),
                noer=db.All("SELECT n.id,n.state,COALESCE(r.title,u.username) target FROM noer n JOIN users u ON u.id=n.uid LEFT JOIN rooms r ON n.room=r.id WHERE state IN('submitted','paid') ORDER BY created LIMIT 40")});
            if(r.Kind=="document"){
                var item=db.One("SELECT mime,evidence,uid,room FROM noer WHERE id=$i AND state IN('submitted','paid')",("i",target));if(item==null||item["evidence"] is not byte[] blob)return Results.NotFound();
                return Results.Ok(new{mime=item["mime"],data=protector.Unprotect(blob),members=db.All("SELECT u.username FROM memberships JOIN users u ON u.id=uid WHERE room=$r ORDER BY username",("r",item["room"]))});
            }
            if(!Guid.TryParse(r.Operation,out var op)||r.Note?.Length>3000||Math.Abs((decimal)r.Amount)>100000000)return Results.BadRequest();
            using var c=db.Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
            void Set(string k,object v)=>cmd.Parameters.AddWithValue("$"+k,v);
            Set("i",op.ToString("N"));Set("a",r.Actor);Set("k",r.Kind);Set("u",target);Set("v",r.Amount);Set("n",r.Note??"");
            cmd.CommandText="INSERT OR IGNORE INTO staff_actions VALUES($i,$a,$k,$u,$v,unixepoch())";
            if(cmd.ExecuteNonQuery()==0){cmd.CommandText="SELECT 1 FROM staff_actions WHERE id=$i AND actor=$a AND kind=$k AND target=$u AND amount=$v";return cmd.ExecuteScalar()!=null?Results.Ok(new{ok=true}):Results.Conflict();}
            if(r.Kind is "premium" or "clicks" or "ban"){
                cmd.CommandText="SELECT 1 FROM users WHERE id=$u";if(cmd.ExecuteScalar()==null)return Results.NotFound();
                cmd.CommandText="INSERT OR IGNORE INTO extras(user_id) VALUES($u)";cmd.ExecuteNonQuery();
                if(r.Kind=="clicks")cmd.CommandText="UPDATE extras SET clicks=clicks+$v WHERE user_id=$u AND clicks+$v BETWEEN 0 AND 1000000000000";
                else if(r.Amount is not(0 or 1))return Results.BadRequest();
                else if(r.Kind=="premium")cmd.CommandText="UPDATE extras SET premium=$v WHERE user_id=$u";
                else cmd.CommandText="INSERT INTO account_options(uid,banned) VALUES($u,$v) ON CONFLICT(uid) DO UPDATE SET banned=$v";
                if(cmd.ExecuteNonQuery()!=1)return Error("Операция невозможна с этим балансом.",409);
                if(r.Kind=="premium"){cmd.CommandText="DELETE FROM premium_terms WHERE uid=$u";cmd.ExecuteNonQuery();}
                if(r.Kind=="ban"&&r.Amount==1){cmd.CommandText="DELETE FROM sessions WHERE user_id=$u";cmd.ExecuteNonQuery();}
                cmd.CommandText="INSERT INTO notices(uid,body,created) VALUES($u,$msg,unixepoch())";Set("msg",r.Kind switch{"clicks"=>"Изменение баланса Кликов: "+r.Amount,"premium"=>r.Amount==1?"Premium подключён.":"Premium отключён.",_=>"Обновлён статус аккаунта."});cmd.ExecuteNonQuery();
            }else{
                cmd.CommandText=r.Kind switch{
                    "staff-handle" when System.Text.RegularExpressions.Regex.IsMatch(r.Note??"",@"\A[a-z]{3}\z")=>"UPDATE OR IGNORE users SET username=$n WHERE id=$u AND id NOT LIKE 'ton:%'",
                    "reply" when !string.IsNullOrWhiteSpace(r.Note)=>"UPDATE support SET answer=$n WHERE id=$u",
                    "dismiss-report"=>"DELETE FROM reports WHERE id=$u",
                    "reject-post"=>"DELETE FROM publications WHERE id=$u AND visible=0",
                    "approve-post"=>"UPDATE publications SET visible=1 WHERE id=$u AND visible=0",
                    "approve-noer"=>"UPDATE noer SET state='approved',evidence=NULL WHERE id=$u AND state='paid'",
                    "reject-noer"=>"UPDATE noer SET state='rejected',evidence=NULL WHERE id=$u AND state IN('paid','submitted')",
                    _=>""};
                if(cmd.CommandText=="")return Results.BadRequest();if(cmd.ExecuteNonQuery()!=1)return Results.Conflict();
                if(r.Kind=="approve-post"){cmd.CommandText="INSERT INTO feed(owner,text,created) SELECT uid,body,strftime('%Y-%m-%dT%H:%M:%fZ',created,'unixepoch') FROM publications WHERE id=$u AND kind='feed'";cmd.ExecuteNonQuery();}
            }
            tx.Commit();return Results.Ok(new{ok=true});
        }).RequireRateLimiting("key-requests");
    }
}
record StaffInput(string Actor,string Kind,string? Target,long Amount,string? Note,string? Operation);
record NoerInput(string? Room,string Document);

