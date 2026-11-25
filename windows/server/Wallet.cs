using System.Text.Json;
using System.Security.Cryptography;

static class Wallet {
    static string U(HttpContext c)=>(string)c.Items["user"]!;
    public static void Map(WebApplication app,Store db,TonVerifier verifier){
        db.Exec("""
          CREATE TABLE IF NOT EXISTS wallet_links(uid TEXT PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,address TEXT NOT NULL,network TEXT NOT NULL);
          CREATE TABLE IF NOT EXISTS link_proofs(hash TEXT PRIMARY KEY,uid TEXT REFERENCES users(id) ON DELETE CASCADE,expires INTEGER NOT NULL);
        """);
        string? Address(HttpContext ctx,string uid)=>uid==U(ctx)&&ctx.Items["nft-wallet"] is string wallet?wallet:db.One("SELECT address FROM wallet_links WHERE uid=$u AND network=$n",("u",uid),("n",verifier.Settings().Network))?["address"] as string;
        app.MapPost("/api/wallet/challenge",(HttpContext ctx)=>{
            var settings=verifier.Settings();var payload=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            db.Exec("DELETE FROM link_proofs WHERE expires<=unixepoch();INSERT INTO link_proofs VALUES($h,$u,unixepoch()+300)",("h",Crypto.Hash(payload)),("u",U(ctx)));return Results.Ok(new{payload,network=settings.Network});
        }).RequireRateLimiting("auth");
        app.MapPost("/api/wallet/link",async(HttpContext ctx,JsonElement request)=>{
            if(!request.TryGetProperty("proof",out var proof)||!proof.TryGetProperty("payload",out var p)||p.ValueKind!=JsonValueKind.String)return Results.BadRequest();
            var hash=Crypto.Hash(p.GetString()!);var uid=U(ctx);
            if(db.One("SELECT 1 FROM link_proofs WHERE hash=$h AND uid=$u AND expires>unixepoch()",("h",hash),("u",uid))==null)return Results.BadRequest();
            var result=await verifier.Call("link-wallet",request,ctx.RequestAborted);var addr=result.GetProperty("address").GetString();
            if(ctx.Items["nft-wallet"] is string nft && nft!=addr)return Results.Conflict(new{error="Подключите кошелёк NFT-номера."});
            using var c=db.Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.Parameters.AddWithValue("$h",hash);cmd.Parameters.AddWithValue("$u",uid);cmd.Parameters.AddWithValue("$a",addr!);cmd.Parameters.AddWithValue("$n",verifier.Settings().Network);
            cmd.CommandText="DELETE FROM link_proofs WHERE hash=$h AND uid=$u AND expires>unixepoch()";if(cmd.ExecuteNonQuery()!=1)return Results.BadRequest();
            cmd.CommandText="INSERT INTO wallet_links VALUES($u,$a,$n) ON CONFLICT(uid) DO UPDATE SET address=$a,network=$n";cmd.ExecuteNonQuery();tx.Commit();return Results.Ok(new{address=addr});
        });
        app.MapGet("/api/wallet",async(HttpContext ctx)=>{
            var address=Address(ctx,U(ctx));if(address==null)return Results.Ok(new{address=(string?)null,balance=(string?)null});
            return Results.Ok(await verifier.Call("wallet",new{address},ctx.RequestAborted));
        });
        app.MapGet("/api/users/{uid}/wallet",async(HttpContext ctx,string uid)=>{
            if(uid!=U(ctx)&&db.One("SELECT 1 FROM preferences WHERE owner=$u AND key='bal' AND value='true'",("u",uid))==null)return Results.NotFound();
            var address=Address(ctx,uid);return address==null?Results.NotFound():Results.Ok(await verifier.Call("wallet",new{address},ctx.RequestAborted));
        });
        app.MapPost("/api/wallet/transfer",async(HttpContext ctx,TapTransfer r)=>{
            var address=Address(ctx,U(ctx));if(address==null)return Results.Conflict(new{error="Подключите кошелёк."});
            return Results.Ok(await verifier.Call("transfer",new{address,recipient=r.Address,amount=r.Amount},ctx.RequestAborted));
        });
    }
}
record TapTransfer(string Address,string Amount);

