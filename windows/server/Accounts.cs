using System.Security.Cryptography;
using System.Net;
using System.Net.Mail;
using Microsoft.Data.Sqlite;

static class Accounts {
    static string? Host=>Environment.GetEnvironmentVariable("CTRL_SMTP_HOST");
    static string H(HttpContext ctx)=>Crypto.Hash(ctx.Request.Headers.Authorization.ToString()[7..]);
    static string U(HttpContext ctx)=>(string)ctx.Items["user"]!;
    static IResult Error(string text,int code=400)=>Results.Json(new{error=text},statusCode:code);
    public static bool MailEnabled=>!string.IsNullOrWhiteSpace(Host);
    public static async Task<IResult> RequestVerify(Store db,string name,string email,string handle,string salt,string pass,CancellationToken ct){
        if(db.One("SELECT 1 FROM users WHERE email=$e OR username=$u",("e",email),("u",handle))!=null)return Error("Эта почта или имя уже заняты.",409);
        var id=Guid.NewGuid().ToString("N");var code=RandomNumberGenerator.GetInt32(10000000,100000000).ToString();
        var from=Environment.GetEnvironmentVariable("CTRL_SMTP_FROM");if(string.IsNullOrEmpty(from))return Error("Отправка писем пока недоступна.",503);
        db.Exec("DELETE FROM pending_mail WHERE expires<=unixepoch();INSERT INTO pending_mail VALUES($i,$e,$u,$n,$s,$p,$k,unixepoch()+600,0)",("i",id),("e",email),("u",handle),("n",name),("s",salt),("p",pass),("k",Crypto.Hash(id+"|"+code)));
        try {
            using var smtp=new SmtpClient(Host!,int.TryParse(Environment.GetEnvironmentVariable("CTRL_SMTP_PORT"),out var port)?port:587){EnableSsl=true};
            if(Environment.GetEnvironmentVariable("CTRL_SMTP_USER") is string user)smtp.Credentials=new NetworkCredential(user,Environment.GetEnvironmentVariable("CTRL_SMTP_PASSWORD"));
            using var msg=new MailMessage(from,email,"Код для регистрации в Ctrl","Ваш код: "+code+". Он действует 10 минут.");await smtp.SendMailAsync(msg,ct);
        }catch(SmtpException){db.Exec("DELETE FROM pending_mail WHERE id=$i",("i",id));return Error("Не удалось отправить письмо. Попробуйте позже.",503);}
        return Results.Ok(new{challengeId=id});
    }
    public static void Map(WebApplication app,Store db,Func<string,object> session){
        db.Exec("CREATE TABLE IF NOT EXISTS pending_mail(id TEXT PRIMARY KEY,email TEXT,handle TEXT,name TEXT,salt TEXT,pass TEXT,code TEXT,expires INTEGER,tries INTEGER)");
        app.MapPost("/api/verify-email",(MailCode r)=>{
            if(r.Id?.Length!=32||r.Code?.Length!=8)return Error("Проверьте код.");
            using var c=db.Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.Parameters.AddWithValue("$i",r.Id);cmd.Parameters.AddWithValue("$k",Crypto.Hash(r.Id+"|"+r.Code));
            cmd.CommandText="UPDATE pending_mail SET tries=tries+1 WHERE id=$i AND tries<5 AND expires>unixepoch() RETURNING code";var key=cmd.ExecuteScalar() as string;
            if(key!=Crypto.Hash(r.Id+"|"+r.Code)){tx.Commit();return Error("Код неверный или срок действия истёк.");}
            var uid=Guid.NewGuid().ToString("N");cmd.Parameters.AddWithValue("$u",uid);cmd.CommandText="INSERT OR IGNORE INTO users(id,username,email,name,salt,password,bio) SELECT $u,handle,email,name,salt,pass,'' FROM pending_mail WHERE id=$i AND code=$k";
            if(cmd.ExecuteNonQuery()!=1)return Error("Эта почта или имя уже заняты.",409);cmd.CommandText="DELETE FROM pending_mail WHERE id=$i OR email=(SELECT email FROM users WHERE id=$u)";cmd.ExecuteNonQuery();tx.Commit();return Results.Ok(session(uid));
        }).RequireRateLimiting("auth");
        app.MapGet("/api/me/sessions",(HttpContext ctx)=>Results.Ok(db.All("SELECT substr(hash,1,24) id, hash=$s cur,expires FROM sessions WHERE user_id=$u AND expires>unixepoch() ORDER BY cur DESC,expires DESC",("s",H(ctx)),("u",U(ctx)))));
        app.MapDelete("/api/me/sessions/{id}",(HttpContext ctx,string id)=>{
            if(id.Length!=24||!id.All(Uri.IsHexDigit))return Results.BadRequest();
            db.Exec("DELETE FROM sessions WHERE user_id=$u AND substr(hash,1,24)=$i AND hash<>$s",("i",id),("s",H(ctx)),("u",U(ctx)));return Results.Ok(new{ok=true});
        });
        app.MapPost("/api/me/password",(HttpContext ctx,PasswordChange r)=>{
            var uid=U(ctx);if(uid.StartsWith("ton:")||r.Old?.Length is null or <1 or >128||r.Next?.Length is null or <12 or >128)return Error("Введите текущий пароль и новый пароль длиной от 12 символов.");
            var old=db.One("SELECT salt,password FROM users WHERE id=$u",("u",uid))!;
            var prior=(string)old["password"]!;if(!CryptographicOperations.FixedTimeEquals(Crypto.Password(r.Old,Convert.FromBase64String((string)old["salt"]!)),Convert.FromBase64String(prior)))return Error("Неверный текущий пароль.",403);
            var salt=RandomNumberGenerator.GetBytes(16);var pass=Convert.ToBase64String(Crypto.Password(r.Next,salt));
            using var c=db.Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.Parameters.AddWithValue("$u",uid);cmd.Parameters.AddWithValue("$s",H(ctx));cmd.Parameters.AddWithValue("$salt",Convert.ToBase64String(salt));cmd.Parameters.AddWithValue("$p",pass);cmd.Parameters.AddWithValue("$old",prior);
            cmd.CommandText="UPDATE users SET salt=$salt,password=$p WHERE id=$u AND password=$old";if(cmd.ExecuteNonQuery()!=1)return Results.Conflict();
            cmd.CommandText="DELETE FROM sessions WHERE user_id=$u AND hash<>$s";cmd.ExecuteNonQuery();tx.Commit();return Results.Ok(new{ok=true});
        }).RequireRateLimiting("auth");
    }
}
record MailCode(string Id,string Code);
record PasswordChange(string Old,string Next);

