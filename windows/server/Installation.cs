using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
static class Installation {
    public static void Map(WebApplication app,Store db){
        db.Exec("""
            CREATE TABLE IF NOT EXISTS device_challenges(n TEXT PRIMARY KEY,session TEXT NOT NULL,expires INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS linked_installs(uid TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,fingerprint TEXT NOT NULL,PRIMARY KEY(uid,fingerprint));
            CREATE TABLE IF NOT EXISTS session_install(hash TEXT PRIMARY KEY REFERENCES sessions(hash) ON DELETE CASCADE,fingerprint TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS device_blocks(owner TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,peer TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,fingerprint TEXT NOT NULL,PRIMARY KEY(owner,peer,fingerprint));
            """);
        string Session(HttpContext c)=>Crypto.Hash(c.Request.Headers.Authorization.ToString()[7..]);
        static long Now()=>DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        app.MapPost("/api/device/challenge",(HttpContext ctx)=>{
            var n=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            db.Exec("DELETE FROM device_challenges WHERE expires<=$t;INSERT INTO device_challenges VALUES($n,$s,$e)",("t",Now()),("n",n),("s",Session(ctx)),("e",Now()+120));
            return Results.Ok(new{nonce=n});
        });
        app.MapPost("/api/device/bind",(HttpContext ctx,InstallProof data)=>{
            var hash=Session(ctx);var uid=(string)ctx.Items["user"]!;
            if(data.Nonce?.Length!=64 || data.PublicKey?.Length>400 || data.Signature?.Length>200)return Results.BadRequest();
            var challenge=db.One("DELETE FROM device_challenges WHERE n=$n AND session=$s AND expires>$t RETURNING n",("n",data.Nonce),("s",hash),("t",Now()));
            if(challenge==null)return Results.Unauthorized();
            string fingerprint;
            try{
                var pub=Convert.FromBase64String(data.PublicKey!);using var key=ECDsa.Create();key.ImportSubjectPublicKeyInfo(pub,out int read);
                if(read!=pub.Length||key.KeySize!=256||!key.VerifyData(Encoding.UTF8.GetBytes("ctrl-device-v1|"+uid+"|"+data.Nonce),Convert.FromBase64String(data.Signature!),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))return Results.Unauthorized();
                fingerprint=Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
            }catch(Exception e)when(e is CryptographicException or FormatException or ArgumentException){return Results.BadRequest();}
            db.Exec("""
              BEGIN IMMEDIATE;
              INSERT OR IGNORE INTO linked_installs VALUES($u,$f);
              INSERT OR REPLACE INTO session_install VALUES($s,$f);
              INSERT OR IGNORE INTO device_blocks SELECT owner,peer,$f FROM device_blocks WHERE peer=$u;
              COMMIT;
              """,("u",uid),("f",fingerprint),("s",hash));
            return Results.Ok(new{ok=true});
        });
        app.MapPost("/api/conversations/{id}/device-block",(HttpContext ctx,string id)=>{
            var uid=(string)ctx.Items["user"]!;
            if(Convert.ToInt64(db.One("SELECT premium FROM extras WHERE user_id=$u",("u",uid))?["premium"]??0)==0)return Results.StatusCode(403);
            var peer=db.One("SELECT CASE WHEN a=$u THEN b ELSE a END p FROM conversations WHERE id=$c AND (a=$u OR b=$u)",("u",uid),("c",id))?["p"] as string;
            if(peer==null)return Results.NotFound();
            db.Exec("BEGIN IMMEDIATE; INSERT OR IGNORE INTO blocks VALUES($u,$p);INSERT OR IGNORE INTO device_blocks SELECT $u,$p,fingerprint FROM linked_installs WHERE uid=$p;COMMIT;",("u",uid),("p",peer));return Results.Ok(new{ok=true});
        });
    }
    public static bool BannedPair(Store db,string chat)=>db.One("""
     SELECT 1 FROM conversations c JOIN device_blocks b ON b.owner IN(c.a,c.b)
     JOIN linked_installs d ON d.uid=CASE WHEN b.owner=c.a THEN c.b ELSE c.a END AND d.fingerprint=b.fingerprint WHERE c.id=$c
    """,("c",chat))!=null;
}
record InstallProof(string Nonce,string PublicKey,string Signature);

