static class GiftEditions {
    public static void Map(WebApplication app,Store db,TonVerifier verifier){
        db.Exec("CREATE TABLE IF NOT EXISTS editions(uid TEXT REFERENCES users(id) ON DELETE CASCADE,operation TEXT,gift TEXT NOT NULL,source TEXT NOT NULL,wallet TEXT NOT NULL,collection TEXT NOT NULL,network TEXT NOT NULL,PRIMARY KEY(uid,operation),UNIQUE(uid,source))");
        app.MapGet("/api/gifts/editions",(HttpContext ctx)=>Results.Ok(db.All("SELECT operation,gift FROM editions WHERE uid=$u",("u",ctx.Items["user"]))));
        app.MapPost("/api/gifts/edition",async(HttpContext ctx,EditionInput r)=>{
            var uid=(string)ctx.Items["user"]!;var cfg=verifier.Settings();if(!Guid.TryParse(r.Operation,out var nonce))return Results.BadRequest();
            var op=nonce.ToString("N");
            var prev=db.One("SELECT * FROM editions WHERE uid=$u AND operation=$o",("u",uid),("o",op));
            var wallet=prev?["wallet"] as string??ctx.Items["nft-wallet"] as string??db.One("SELECT address FROM wallet_links WHERE uid=$u AND network=$n",("u",uid),("n",cfg.Network))?["address"] as string;
            if(wallet==null)return Results.Conflict(new{error="Подключите кошелёк в профиле."});
            if(prev!=null&&((string)prev["gift"]! !=r.Gift||(string)prev["collection"]! !=cfg.UpgradeCollection||(string)prev["network"]! !=cfg.Network))return Results.Conflict();
            var cert=await verifier.Call("voucher",new{operation=op,uid,address=wallet,gift=r.Gift},ctx.RequestAborted);
            using var c=db.Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.Parameters.AddWithValue("$u",uid);cmd.Parameters.AddWithValue("$o",op);cmd.Parameters.AddWithValue("$g",r.Gift);cmd.Parameters.AddWithValue("$w",wallet);cmd.Parameters.AddWithValue("$c",cfg.UpgradeCollection!);cmd.Parameters.AddWithValue("$n",cfg.Network);
            cmd.CommandText="SELECT wallet FROM editions WHERE uid=$u AND operation=$o AND gift=$g AND collection=$c AND network=$n";
            if(cmd.ExecuteScalar() is string existing){if(existing!=wallet)return Results.Conflict();return Results.Ok(cert);}
            cmd.CommandText="INSERT OR IGNORE INTO editions SELECT $u,$o,$g,g.operation,$w,$c,$n FROM gift_orders g WHERE g.owner=$u AND g.gift=$g AND NOT EXISTS(SELECT 1 FROM editions e WHERE e.uid=$u AND e.source=g.operation) ORDER BY g.rowid LIMIT 1";
            if(cmd.ExecuteNonQuery()!=1)return Results.Conflict(new{error="Нужен свободный экземпляр подарка."});
            cmd.CommandText="UPDATE extras SET clicks=clicks-25 WHERE user_id=$u AND clicks>=25";
            if(cmd.ExecuteNonQuery()!=1)return Results.Json(new{error="Для выпуска нужны 25 Кликов."},statusCode:402);
            tx.Commit();return Results.Ok(cert);
        });
    }
}
record EditionInput(string Gift,string Operation);

