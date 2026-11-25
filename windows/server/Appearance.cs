static class Appearance {
    static readonly string[] Fields=["profileAvatar","profileBackground","profileBanner","chatBanner"];
    public static void Map(WebApplication app,Store db){
        db.Exec("CREATE TABLE IF NOT EXISTS artwork(uid TEXT REFERENCES users(id) ON DELETE CASCADE,field TEXT,data TEXT NOT NULL,PRIMARY KEY(uid,field))");
        app.MapGet("/api/me/artwork",(HttpContext ctx)=>Results.Ok(db.All("SELECT field,data FROM artwork WHERE uid=$u",("u",ctx.Items["user"]))));
        app.MapGet("/api/profile/{uid}",(HttpContext ctx,string uid)=>{
            var user=db.One("SELECT u.username,u.name,u.bio,COALESCE(e.premium,0) premium,COALESCE(o.frame,'') frame, EXISTS(SELECT 1 FROM noer WHERE uid=u.id AND room IS NULL AND state='approved') verified,EXISTS(SELECT 1 FROM preferences WHERE owner=u.id AND key='bal' AND value='true') publicBalance FROM users u LEFT JOIN extras e ON e.user_id=u.id LEFT JOIN account_options o ON o.uid=u.id WHERE u.id=$p AND NOT EXISTS(SELECT 1 FROM blocks WHERE (owner=$p AND peer=$u) OR (owner=$u AND peer=$p))",("p",uid),("u",ctx.Items["user"]));
            return user==null?Results.NotFound():Results.Ok(new{user,stories=db.All("SELECT body FROM publications WHERE uid=$p AND kind='story' AND visible=1 AND created>unixepoch()-86400 ORDER BY id DESC LIMIT 20",("p",uid)),art=db.All("SELECT field,data FROM artwork WHERE uid=$p AND field!='chatBanner'",("p",uid))});
        });
        app.MapPost("/api/me/artwork",(HttpContext ctx,ArtInput r)=>{
            var uid=(string)ctx.Items["user"]!;if(!Fields.Contains(r.Field)||r.Data==null||r.Data.Length>1600000)return Results.BadRequest();
            if(r.Data==""){db.Exec("DELETE FROM artwork WHERE uid=$u AND field=$f",("u",uid),("f",r.Field));return Results.Ok(new{ok=true});}
            var parts=r.Data.Split(',',2);if(parts.Length!=2||parts[0] is not("data:image/webp;base64" or "data:image/gif;base64"))return Results.BadRequest();
            byte[] b;try{b=Convert.FromBase64String(parts[1]);}catch(FormatException){return Results.BadRequest();}
            var gif=parts[0]=="data:image/gif;base64";if(b.Length<16||!(gif?(b.AsSpan().StartsWith("GIF89a"u8)||b.AsSpan().StartsWith("GIF87a"u8)):b.AsSpan(0,4).SequenceEqual("RIFF"u8)&&b.AsSpan(8,4).SequenceEqual("WEBP"u8)))return Results.BadRequest();
            var animated=gif||b.AsSpan().IndexOf("ANIM"u8)>=0;
            if((animated||r.Field=="chatBanner")&&(r.Field is not("profileBanner" or "chatBanner")||Convert.ToInt64(db.One("SELECT premium FROM extras WHERE user_id=$u",("u",uid))?["premium"]??0)==0))return Results.Json(new{error="Для анимации нужен Premium."},statusCode:403);
            db.Exec("INSERT INTO artwork VALUES($u,$f,$d) ON CONFLICT(uid,field) DO UPDATE SET data=$d",("u",uid),("f",r.Field),("d",parts[0]+","+Convert.ToBase64String(b)));return Results.Ok(new{ok=true});
        });
    }
}
record ArtInput(string Field,string Data);

