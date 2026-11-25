using System.Text.RegularExpressions;
static class Aliases {
    public static bool Eligible(string name)=>Regex.IsMatch(name,@"\A[a-z][a-z0-9_]{2,3}\z")||name=="rebula";
    public static async Task<string?> Resolve(Store db,TonVerifier verifier,string name,CancellationToken ct){
        var r=db.One("SELECT * FROM nft_handles WHERE handle=$h",("h",name));if(r==null)return null;
        var state=await verifier.Call("handle",new{handle="@"+name},ct);
        if(!state.TryGetProperty("wallet",out var who)||who.GetString()!=(string)r["wallet"]!||state.GetProperty("version").GetString()!=(string)r["version"]!||state.GetProperty("item").GetString()!=(string)r["item"]!)return null;
        return (string)r["uid"]!;
    }
    public static void Map(WebApplication app,Store db,TonVerifier verifier){
        db.Exec("CREATE TABLE IF NOT EXISTS nft_handles(handle TEXT PRIMARY KEY,uid TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,wallet TEXT NOT NULL,item TEXT NOT NULL,version TEXT NOT NULL)");
        app.MapPost("/api/wallet/handle",async(HttpContext ctx,HandleInput r)=>{
            var name=(r.Handle??"").Trim().TrimStart('@').ToLowerInvariant();if(!Eligible(name))return Results.BadRequest();
            var uid=(string)ctx.Items["user"]!;var wallet=ctx.Items["nft-wallet"] as string??db.One("SELECT address FROM wallet_links WHERE uid=$u AND network=$n",("u",uid),("n",verifier.Settings().Network))?["address"] as string;
            if(wallet==null)return Results.Conflict(new{error="Подключите кошелёк в профиле."});
            var item=await verifier.Call("handle",new{handle="@"+name},ctx.RequestAborted);
            if(!item.TryGetProperty("wallet",out var owner)||owner.GetString()!=wallet)return Results.Json(new{error="Кошелёк не владеет этим юзернеймом."},statusCode:403);
            db.Exec("INSERT INTO nft_handles VALUES($h,$u,$w,$i,$v) ON CONFLICT(handle) DO UPDATE SET uid=$u,wallet=$w,item=$i,version=$v",("h",name),("u",uid),("w",wallet),("i",item.GetProperty("item").GetString()),("v",item.GetProperty("version").GetString()));
            return Results.Ok(new{handle=name});
        });
    }
}
record HandleInput(string Handle);

