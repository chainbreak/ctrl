using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("CTRL_LISTEN") ?? "http://127.0.0.1:48734");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512000);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("https://ctrl.local").AllowAnyHeader().WithMethods("GET", "POST", "PATCH")));
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode = 429;
    o.AddPolicy("key-requests", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"unknown", _=>new FixedWindowRateLimiterOptions{PermitLimit=120,Window=TimeSpan.FromMinutes(1)}));
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter((ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown")+(ctx.Request.Path.Value?.Contains("/parts/")==true?"-parts":""), _ => new FixedWindowRateLimiterOptions { PermitLimit = ctx.Request.Path.Value?.Contains("/parts/")==true ? 16000 : 900, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var protection = builder.Services.AddDataProtection();
if (Environment.GetEnvironmentVariable("CTRL_PROTECTION_KEYS") is { Length: > 0 } keyPath)
    protection.PersistKeysToFileSystem(Directory.CreateDirectory(keyPath)).SetApplicationName("CtrlMessenger");
var proxySetting=Environment.GetEnvironmentVariable("CTRL_TRUSTED_PROXY");
if (!string.IsNullOrWhiteSpace(proxySetting))
{
    if (!System.Net.IPAddress.TryParse(proxySetting, out var proxyAddress))
        throw new InvalidOperationException("CTRL_TRUSTED_PROXY должен содержать IP-адрес обратного прокси.");
    builder.Services.Configure<ForwardedHeadersOptions>(options => {
        options.ForwardedHeaders=Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(proxyAddress);
        options.ForwardLimit=1;
    });
}
var app = builder.Build();
if (!string.IsNullOrWhiteSpace(proxySetting)) app.UseForwardedHeaders();
var dataPath = Environment.GetEnvironmentVariable("CTRL_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ctrl-server");
Directory.CreateDirectory(dataPath);
var db = new Store(Path.Combine(dataPath, "ctrl.db"));
db.Init();
DesktopFeatures.Init(db);
Community.Init(db);
InboxChanges.Init(db);
db.Inbox.Map(app);
app.Lifetime.ApplicationStopping.Register(db.Inbox.Dispose);
var verifier=new TonVerifier(dataPath);
app.Lifetime.ApplicationStopping.Register(verifier.Dispose);
app.Use(async (ctx, next) => {
    if((ctx.Request.Path=="/api/noer"||ctx.Request.Path=="/api/me/artwork") && ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is {IsReadOnly:false} documentLimit)documentLimit.MaxRequestBodySize=2800100;
    if((ctx.Request.Path.StartsWithSegments("/api/files")||ctx.Request.Path.StartsWithSegments("/api/music")) && ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is {IsReadOnly:false} limit)limit.MaxRequestBodySize=1500000;
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-Frame-Options"]="DENY";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    try { await next(); }
    catch (TonError ex) { ctx.Response.StatusCode=ex.Status; await ctx.Response.WriteAsJsonAsync(new {error=ex.Message}); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (BadHttpRequestException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "Проверьте данные запроса." }); }
    catch (Exception ex) { app.Logger.LogError(ex, "Request failed: {Path}", ctx.Request.Path); if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "Не удалось выполнить запрос. Попробуйте ещё раз." }); } }
});
app.UseCors();
app.UseRateLimiter();
BotCheck.Map(app);
app.Use(async (ctx, next) => {
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/api/") && path is not "/api/register" and not "/api/login" and not "/api/verify-email" and not "/api/auth-options" and not "/api/support" and not "/api/support/status" and not "/api/ton/config" and not "/api/ton/challenge" and not "/api/ton/login") {
        var auth = ctx.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : "";
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) { ctx.Response.StatusCode = 401; return; }
        var row = db.One("SELECT s.user_id,t.ticket FROM sessions s LEFT JOIN ton_tickets t ON t.hash=s.hash WHERE s.hash=$hash AND s.expires>$now", ("hash", Crypto.Hash(token)), ("now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        if (row is null || Community.Banned(db,(string)row["user_id"]!)) { ctx.Response.StatusCode = 401; return; }
        if (row["user_id"] is string uid && uid.StartsWith("ton:",StringComparison.Ordinal)) {
            if (row["ticket"] is not string packed) { ctx.Response.StatusCode=401; return; }
            var jwt=TonTicket.Open(token,packed);
            if (path == "/api/logout") ctx.Items["ton-logout"]=jwt;
            else {
                try {
                    var identity=await verifier.Call("me",new {token=jwt},ctx.RequestAborted);
                    if (uid!="ton:"+identity.GetProperty("privateSubject").GetString()) throw new TonError(401,"Войдите снова.");
                    ctx.Items["nft-wallet"]=identity.GetProperty("wallet").GetString();
                    ctx.Items["nft-number"]=identity.GetProperty("number").GetString();
                } catch(TonError ex) when(ex.Status is 401 or 403) {
                    db.Exec("DELETE FROM sessions WHERE hash=$hash",("hash",Crypto.Hash(token)));
                    throw new TonError(401,"Доступ к номеру изменился или сессия истекла. Войдите снова.");
                }
            }
        }
        if(path.StartsWith("/api/signal/")||path=="/api/community/group-message"||path.Contains("/messages") && ctx.Request.Method!="GET"||path.StartsWith("/api/calls")){
            if(db.One("SELECT 1 FROM session_install WHERE hash=$s",("s",Crypto.Hash(token)))==null){ctx.Response.StatusCode=428;await ctx.Response.WriteAsJsonAsync(new {error="Подтвердите устройство, повторно открыв Ctrl."});return;}
        }
        if(path=="/api/me"||path=="/api/community/rooms"||path=="/api/files"||path=="/api/me/artwork"||path.EndsWith("/device-block"))Commerce.Expire(db,(string)row["user_id"]!);
        ctx.Items["user"] = row["user_id"];
    }
    await next();
});
string U(HttpContext c) => (string)c.Items["user"]!;
IResult Err(string text, int status = 400) => Results.Json(new { error = text }, statusCode: status);
Dictionary<string, object?>? User(string id) => db.One("SELECT u.id,username,name,NULLIF(email,'nft:' || u.id) AS email,bio,EXISTS(SELECT 1 FROM noer WHERE uid=u.id AND room IS NULL AND state='approved') verified,COALESCE(p.clicks,0) clicks,COALESCE(p.premium,0) premium FROM users u LEFT JOIN extras p ON u.id=p.user_id WHERE u.id=$id", ("id", id));
object Session(string id) {
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    db.Exec("DELETE FROM sessions WHERE expires<=$now", ("now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    db.Exec("INSERT INTO sessions(hash,user_id,expires) VALUES($hash,$id,$expires)", ("hash", Crypto.Hash(token)), ("id", id), ("expires", DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds()));
    db.Exec("INSERT INTO notices(uid,body,created) VALUES($u,'Выполнен вход в Ctrl.',unixepoch())",("u",id));
    return new { token, user = User(id) };
}
Dictionary<string, object?>? Chat(string id, string uid) => db.One("SELECT * FROM conversations WHERE id=$id AND (a=$uid OR b=$uid)", ("id", id), ("uid", uid));
app.MapGet("/health", () => Results.Ok(new { service = "ctrl", version = "0.1.2" }));
app.MapGet("/health/ready", () => {
    try {
        db.One("SELECT id FROM users LIMIT 1");
        return Results.Ok(new { service="ctrl", ready=true });
    } catch(SqliteException) {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapGet("/api/ton/config", () => {
    var settings=verifier.Settings();
    return Results.Ok(new {network=settings.Network,manifestUrl=settings.PublicOrigin+"/tonconnect-manifest.json"});
});
app.MapGet("/tonconnect-manifest.json", (HttpContext ctx) => {
    var origin=verifier.Settings().PublicOrigin;
    ctx.Response.Headers.AccessControlAllowOrigin="*";
    return Results.Ok(new {url=origin,name="Ctrl",iconUrl=origin+"/ton-icon.png"});
});
app.MapPost("/api/ton/order", async(JsonElement request,HttpContext ctx) =>
    Results.Ok(await verifier.Call("order",request,ctx.RequestAborted))).RequireRateLimiting("auth");
app.MapPost("/api/ton/challenge", async(JsonElement input,HttpContext ctx) =>
    Results.Ok(await verifier.Call("challenge",input,ctx.RequestAborted))).RequireRateLimiting("auth");
app.MapPost("/api/ton/login", async(JsonElement input,HttpContext ctx) => {
    var login=await verifier.Call("login",input,ctx.RequestAborted);
    var uid="ton:"+login.GetProperty("privateSubject").GetString();
    var number=login.GetProperty("number").GetString()!;
    var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    if(Community.Banned(db,uid))return Err("Аккаунт заблокирован.",403);
    db.NftSession(uid,login.GetProperty("subject").GetString()!,number,Crypto.Hash(token),
        TonTicket.Protect(token,login.GetProperty("token").GetString()!),login.GetProperty("expires").GetInt64());
    db.Exec("INSERT INTO wallet_links VALUES($u,$a,$n) ON CONFLICT(uid) DO UPDATE SET address=$a,network=$n; INSERT INTO notices(uid,body,created) VALUES($u,'Выполнен вход по NFT-номеру.',unixepoch())",("u",uid),("a",login.GetProperty("wallet").GetString()),("n",verifier.Settings().Network));
    var user=User(uid)!; user["number"]=number;
    return Results.Ok(new {token,user});
}).RequireRateLimiting("auth");
app.MapPost("/api/register", async (Register r,HttpContext ctx) => {
    var username = (r.Username ?? "").Trim().ToLowerInvariant();
    var email = (r.Email ?? "").Trim().ToLowerInvariant();
    var name = (r.Name ?? "").Trim();
    if (!Regex.IsMatch(username, "^[a-z][a-z0-9_]{4,31}$")) return Err("Имя пользователя: 5–32 символа, латиница, цифры и подчёркивание.");
    if (DesktopFeatures.Reserved(username) || new[] { "admin", "ctrlsup", "botmather", "official", "support", "administrator" }.Contains(username)) return Err("Это имя зарезервировано.");
    if (!Regex.IsMatch(email, "^[^\\s@]+@gmail\\.com$") || email.Length > 254) return Err("Укажите почту Gmail.");
    if (name.Length is < 1 or > 60) return Err("Введите имя до 60 символов.");
    if (r.Password is null || r.Password.Length is < 12 or > 128) return Err("Пароль должен содержать от 12 до 128 символов.");
    var id = Guid.NewGuid().ToString("N");
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Crypto.Password(r.Password, salt);
    if(Accounts.MailEnabled)return await Accounts.RequestVerify(db,name,email,username,Convert.ToBase64String(salt),Convert.ToBase64String(hash),ctx.RequestAborted);
    try { db.Exec("INSERT INTO users(id,username,email,name,salt,password,bio) VALUES($id,$username,$email,$name,$salt,$password,'')", ("id",id),("username",username),("email",email),("name",name),("salt",Convert.ToBase64String(salt)),("password",Convert.ToBase64String(hash))); }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return Err("Эта почта или имя пользователя уже заняты.",409); }
    return Results.Ok(Session(id));
}).RequireRateLimiting("auth");
app.MapPost("/api/login", (Login r) => {
    if (r.Password is null || r.Password.Length > 128 || r.LoginName is null || r.LoginName.Length > 254) return Err("Проверьте имя и пароль.",401);
    var login = r.LoginName.Trim().TrimStart('@').ToLowerInvariant();
    var row = db.One("SELECT id,salt,password FROM users WHERE (username=$login OR email=$login) AND id NOT LIKE 'ton:%'", ("login",login));
    var salt = row is null ? new byte[16] : Convert.FromBase64String((string)row["salt"]!);
    var computed = Crypto.Password(r.Password,salt);
    if (row is null || !CryptographicOperations.FixedTimeEquals(computed,Convert.FromBase64String((string)row["password"]!))) return Err("Неверная почта, имя пользователя или пароль.",401);
    if(Community.Banned(db,(string)row["id"]!))return Err("Аккаунт заблокирован.",403);
    return Results.Ok(Session((string)row["id"]!));
}).RequireRateLimiting("auth");
IResult CurrentUser(HttpContext ctx) {
    var user=User(U(ctx))!;
    if(ctx.Items["nft-number"] is string number) user["number"]=number;
    return Results.Ok(user);
}
app.MapGet("/api/me", (HttpContext ctx) => CurrentUser(ctx));
app.MapPost("/api/logout", async(HttpContext ctx) => {
    db.Exec("DELETE FROM sessions WHERE hash=$hash",("hash",Crypto.Hash(ctx.Request.Headers.Authorization.ToString()[7..])));
    if(ctx.Items["ton-logout"] is string jwt) {
        try { await verifier.Call("logout",new {token=jwt},ctx.RequestAborted); } catch(TonError) { }
    }
    return Results.Ok(new {ok=true});
});
app.MapPatch("/api/me", (HttpContext ctx, Profile p) => {
    var name=(p.Name??"").Trim();var bio=(p.Bio??"").Trim();
    if(name.Length is <1 or >60 || bio.Length>100)return Err("Имя — до 60 символов, описание — до 100.");
    if(!string.IsNullOrWhiteSpace(p.Username)){
        var handle=p.Username.Trim().TrimStart('@').ToLowerInvariant();
        var old=User(U(ctx))!["username"]?.ToString();
        if(handle!=old){
            if(!Regex.IsMatch(handle,@"\A[a-z][a-z0-9_]{4,31}\z") || DesktopFeatures.Reserved(handle))return Err("Выберите другое имя пользователя.");
            try{db.Exec("UPDATE users SET username=$h WHERE id=$id",("h",handle),("id",U(ctx)));}catch(SqliteException ex) when(ex.SqliteErrorCode==19){return Err("Имя уже занято.",409);}
        }
    }
    db.Profile(U(ctx),name,bio);
    return CurrentUser(ctx);
});
app.MapGet("/api/users", async (HttpContext ctx,string? q) => {
    q=(q??"").Trim().TrimStart('@').ToLowerInvariant();
    if(Regex.IsMatch(q,@"\A\+(333|999)[0-9]{8}\z")) {
        var owner=await verifier.Call("lookup",new {number=q},ctx.RequestAborted);
        if(!owner.TryGetProperty("privateSubject",out var subject)) return Results.Ok(Array.Empty<object>());
        return Results.Ok(db.All("SELECT id,username,name,bio FROM users WHERE id=$id AND id<>$me",("id","ton:"+subject.GetString()),("me",U(ctx))));
    }
    if(Aliases.Eligible(q)&&await Aliases.Resolve(db,verifier,q,ctx.RequestAborted) is string aliasOwner)
        return Results.Ok(db.All("SELECT id,username,name,bio,$q AS alias FROM users WHERE id=$p AND id<>$u",("p",aliasOwner),("q",q),("u",U(ctx))));
    if(q.Length<2||q.Length>32)return Results.Ok(Array.Empty<object>());
    return Results.Ok(db.All("SELECT id,username,name,bio FROM users WHERE id<>$id AND substr(username,1,length($q))=$q ORDER BY username LIMIT 20",("id",U(ctx)),("q",q)));
});
app.MapGet("/api/conversations", (HttpContext ctx, bool? compact) => {
    var uid=U(ctx);
    var rows=db.All("""
    SELECT c.id,u.id peerId,u.username,u.name,u.bio,COALESCE((SELECT fee FROM account_options WHERE uid=u.id),0) fee,COALESCE((SELECT frame FROM account_options WHERE uid=u.id),'') frame, EXISTS(SELECT 1 FROM blocks WHERE owner=$uid AND peer=u.id) blocked,
    CASE WHEN $compact=0 THEN (SELECT body FROM messages WHERE chat_id=c.id ORDER BY seq DESC LIMIT 1) END lastText,
    (SELECT MAX(seq) FROM messages WHERE chat_id=c.id) lastSeq,
    COALESCE((SELECT version FROM chat_versions WHERE chat=c.id),0) version,
    (SELECT created FROM messages WHERE chat_id=c.id ORDER BY seq DESC LIMIT 1) lastAt,
    (SELECT COUNT(*) FROM messages WHERE chat_id=c.id AND sender<>$uid AND seq>CASE WHEN c.a=$uid THEN c.read_a ELSE c.read_b END) unread,
    CASE WHEN c.a=$uid THEN c.read_b ELSE c.read_a END peerRead
    FROM conversations c JOIN users u ON u.id=CASE WHEN c.a=$uid THEN c.b ELSE c.a END
    WHERE c.a=$uid OR c.b=$uid ORDER BY COALESCE(lastAt,c.created) DESC,c.id
    """,("uid",uid),("compact",compact==true?1:0));
    return Results.Ok(rows);
});
app.MapPost("/api/conversations", (HttpContext ctx, StartChat r) => {
    var uid=U(ctx);if(r.PeerId==uid)return Err("Выберите другого пользователя.");
    if(db.One("SELECT id FROM users WHERE id=$id",("id",r.PeerId??"")) is null)return Err("Пользователь не найден.",404);
    var ids=new[]{uid,r.PeerId!}.Order(StringComparer.Ordinal).ToArray();
    var created=db.One("INSERT OR IGNORE INTO conversations(id,a,b,created) VALUES($id,$a,$b,$created) RETURNING id",("id",Guid.NewGuid().ToString("N")),("a",ids[0]),("b",ids[1]),("created",DateTimeOffset.UtcNow.ToString("O")));
    if(created!=null)db.Inbox.Notify(ids);
    return Results.Ok(db.One("SELECT id FROM conversations WHERE a=$a AND b=$b",("a",ids[0]),("b",ids[1])));
});
app.MapGet("/api/conversations/{id}/messages", (HttpContext ctx,string id,long? before) => {
    if(Chat(id,U(ctx)) is null)return Err("Чат не найден.",404);
    var rows=db.All("SELECT seq,client_id clientId,sender,body,created,EXISTS(SELECT 1 FROM edited_messages WHERE message=seq) edited,EXISTS(SELECT 1 FROM reactions WHERE message=seq) reacted FROM messages WHERE chat_id=$id AND seq<$before ORDER BY seq DESC LIMIT 100",("id",id),("before",before??long.MaxValue));rows.Reverse();
    return Results.Ok(rows);
});
app.MapPost("/api/conversations/{id}/messages", (HttpContext ctx,string id,NewMessage r) => {
    var uid=U(ctx);if(Chat(id,uid) is null)return Err("Чат не найден.",404);
    if(DesktopFeatures.IsBlocked(db,id))return Err("Пользователь заблокирован.",403);
    if(!Guid.TryParse(r.ClientId,out var nonce))return Err("Не удалось определить сообщение.");
    if(db.One("SELECT seq,client_id clientId,sender,body,created FROM messages WHERE chat_id=$c AND sender=$u AND client_id=$n",("c",id),("u",uid),("n",nonce.ToString("N"))) is {} prior)return Results.Ok(prior);
    var body=(r.Body??"").Trim();if(!SignalDirectory.ValidEnvelope(db,id,uid,body))return Err("Обновите ключи устройств и повторите отправку.",409);
    return Community.SaveMessage(db,id,uid,nonce.ToString("N"),body,r.AcceptedFee,r.FileId);
});
app.MapPost("/api/conversations/{id}/read", (HttpContext ctx,string id,ReadChat r) => {
    var uid=U(ctx);var c=Chat(id,uid);if(c is null)return Err("Чат не найден.",404);
    var col=(string)c["a"]! == uid ? "read_a":"read_b";
    var updated=db.One($"UPDATE conversations SET {col}=MIN($seq,(SELECT MAX(seq) FROM messages WHERE chat_id=$id)) WHERE id=$id AND {col}<MIN($seq,(SELECT MAX(seq) FROM messages WHERE chat_id=$id)) RETURNING id",("seq",Math.Max(0,r.Seq)),("id",id));
    if(updated!=null)db.Inbox.Notify((string)c["a"]!, (string)c["b"]!);
    return Results.Ok(new {ok=true});
});
ClientSite.Map(app);
DesktopFeatures.Map(app,db);
Community.Map(app,db);
Attachments.Map(app,db);
AudioLibrary.Map(app,db);
Staff.Map(app,db);
Commerce.Map(app,db);
Wallet.Map(app,db,verifier);
GiftEditions.Map(app,db,verifier);
Aliases.Map(app,db,verifier);
Appearance.Map(app,db);
Accounts.Map(app,db,Session);
SignalDirectory.Map(app,db);
Calls.Map(app,db);
Installation.Map(app,db);
app.Run();

record Register(string? Username,string? Email,string? Name,string? Password);
record Login(string? LoginName,string? Password);
record Profile(string? Name,string? Bio,string? Username);
record StartChat(string? PeerId);
record NewMessage(string? ClientId,string? Body,long? AcceptedFee=null,string? FileId=null);
record ReadChat(long Seq);
static class Crypto {
    public static byte[] Password(string p,byte[] salt)=>Rfc2898DeriveBytes.Pbkdf2(p,salt,600000,HashAlgorithmName.SHA256,32);
    public static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
sealed class Store(string path) {
    public InboxChanges Inbox { get; } = new();
    public SqliteConnection Open(){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,ForeignKeys=true,DefaultTimeout=15}.ToString());c.Open();return c;}
    static SqliteCommand Command(SqliteConnection c,string sql,(string,object?)[] args){var cmd=c.CreateCommand();cmd.CommandText=sql;foreach(var(k,v) in args)cmd.Parameters.AddWithValue("$"+k,v??DBNull.Value);return cmd;}
    public void Exec(string sql,params (string,object?)[] args){using var c=Open();using var cmd=Command(c,sql,args);cmd.ExecuteNonQuery();}
    public List<Dictionary<string,object?>> All(string sql,params (string,object?)[] args){using var c=Open();using var cmd=Command(c,sql,args);using var reader=cmd.ExecuteReader();var rows=new List<Dictionary<string,object?>>();while(reader.Read()){var row=new Dictionary<string,object?>();for(int i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);rows.Add(row);}return rows;}
    public Dictionary<string,object?>? One(string sql,params (string,object?)[] args)=>All(sql,args).FirstOrDefault();
    public void NftSession(string uid,string subject,string number,string hash,string ticket,long expires) {
        using var c=Open(); using var tx=c.BeginTransaction();
        void Run(string sql,params (string,object?)[] args) { using var cmd=Command(c,sql,args); cmd.Transaction=tx; cmd.ExecuteNonQuery(); }
        Run("INSERT OR IGNORE INTO nft_profiles(subject,name,bio) VALUES($subject,$number,'')",("subject",subject),("number",number));
        Run("INSERT OR IGNORE INTO users(id,username,email,name,bio,salt,password) SELECT $id,$username,$email,name,bio,'','' FROM nft_profiles WHERE subject=$subject",("id",uid),("username","n."+uid[4..24]),("email","nft:"+uid),("subject",subject));
        Run("INSERT OR IGNORE INTO nft_members(user_id,subject) VALUES($id,$subject)",("id",uid),("subject",subject));
        Run("UPDATE users SET (name,bio)=(SELECT name,bio FROM nft_profiles WHERE subject=$subject) WHERE id=$id",("id",uid),("subject",subject));
        Run("DELETE FROM sessions WHERE expires<=$now",("now",DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Run("INSERT INTO sessions(hash,user_id,expires) VALUES($hash,$id,$expires)",("hash",hash),("id",uid),("expires",expires));
        Run("INSERT INTO ton_tickets(hash,ticket) VALUES($hash,$ticket)",("hash",hash),("ticket",ticket));
        tx.Commit();
    }
    public void Profile(string uid,string name,string bio) {
        using var c=Open(); using var tx=c.BeginTransaction();
        using var cmd=Command(c,"UPDATE users SET name=$name,bio=$bio WHERE id=$id; UPDATE nft_profiles SET name=$name,bio=$bio WHERE subject=(SELECT subject FROM nft_members WHERE user_id=$id)",[("id",uid),("name",name),("bio",bio)]);
        cmd.Transaction=tx; cmd.ExecuteNonQuery(); tx.Commit();
    }
    public void Init()=>Exec("""
    PRAGMA journal_mode=WAL;
    CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY,username TEXT NOT NULL UNIQUE,email TEXT NOT NULL UNIQUE,name TEXT NOT NULL,bio TEXT NOT NULL,salt TEXT NOT NULL,password TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS sessions(hash TEXT PRIMARY KEY,user_id TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,expires INTEGER NOT NULL);
    CREATE TABLE IF NOT EXISTS nft_profiles(subject TEXT PRIMARY KEY,name TEXT NOT NULL,bio TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS nft_members(user_id TEXT PRIMARY KEY REFERENCES users(id),subject TEXT NOT NULL REFERENCES nft_profiles(subject));
    CREATE TABLE IF NOT EXISTS ton_tickets(hash TEXT PRIMARY KEY REFERENCES sessions(hash) ON DELETE CASCADE,ticket TEXT NOT NULL);
    CREATE INDEX IF NOT EXISTS session_user ON sessions(user_id);
    CREATE TABLE IF NOT EXISTS conversations(id TEXT PRIMARY KEY,a TEXT NOT NULL REFERENCES users(id),b TEXT NOT NULL REFERENCES users(id),created TEXT NOT NULL,read_a INTEGER NOT NULL DEFAULT 0,read_b INTEGER NOT NULL DEFAULT 0,UNIQUE(a,b));
    CREATE TABLE IF NOT EXISTS messages(seq INTEGER PRIMARY KEY AUTOINCREMENT,chat_id TEXT NOT NULL REFERENCES conversations(id),sender TEXT NOT NULL REFERENCES users(id),client_id TEXT NOT NULL,body TEXT NOT NULL,created TEXT NOT NULL,UNIQUE(chat_id,sender,client_id));
    CREATE INDEX IF NOT EXISTS message_chat ON messages(chat_id,seq);
    """);
}