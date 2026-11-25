static class Attachments {
    const int Chunk=1024*1024;
    static long Now()=>DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    static string U(HttpContext ctx)=>(string)ctx.Items["user"]!;
    public static void Map(WebApplication app,Store db){
        db.Exec("""
          CREATE TABLE IF NOT EXISTS uploads(id TEXT PRIMARY KEY,chat TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,owner TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,size INTEGER NOT NULL,created INTEGER NOT NULL,ready INTEGER NOT NULL DEFAULT 0);
          CREATE TABLE IF NOT EXISTS posted_files(upload TEXT PRIMARY KEY REFERENCES uploads(id) ON DELETE CASCADE,message INTEGER UNIQUE REFERENCES messages(seq) ON DELETE CASCADE);
          CREATE TABLE IF NOT EXISTS file_parts(upload TEXT NOT NULL REFERENCES uploads(id) ON DELETE CASCADE,part INTEGER NOT NULL,cipher BLOB NOT NULL,PRIMARY KEY(upload,part));
        """);
        app.MapPost("/api/files",(HttpContext ctx,UploadInput r)=>{
            var uid=U(ctx);
            if(db.One("SELECT 1 FROM conversations WHERE id=$c AND (a=$u OR b=$u)",("c",r.Chat),("u",uid))==null||DesktopFeatures.IsBlocked(db,r.Chat))return Results.NotFound();
            long limit=Convert.ToInt64(db.One("SELECT premium FROM extras WHERE user_id=$u",("u",uid))?["premium"]??0)>0?2_000_000_000L:700_000_000L;
            if(r.Size<1||r.Size>limit)return Results.Json(new{error="Превышен лимит размера файла."},statusCode:413);
            var id=Guid.NewGuid().ToString("N");
            db.Exec("DELETE FROM uploads WHERE created<$t AND NOT EXISTS(SELECT 1 FROM posted_files WHERE upload=uploads.id)",("t",Now()-86400));
            var accepted=db.One("INSERT INTO uploads(id,chat,owner,size,created) SELECT $i,$c,$u,$z,$t WHERE (SELECT COUNT(*) FROM uploads WHERE owner=$u AND ready=0)<6 RETURNING id",("i",id),("c",r.Chat),("u",uid),("z",r.Size),("t",Now()));
            return accepted==null?Results.StatusCode(429):Results.Ok(new{id,chunkSize=Chunk});
        });
        app.MapPost("/api/files/{id}/parts/{part:int}",(HttpContext ctx,string id,int part,UploadPart r)=>{
            var uid=U(ctx);var row=db.One("SELECT chat,size FROM uploads WHERE id=$i AND owner=$u AND ready=0",("i",id),("u",uid));
            if(row==null||DesktopFeatures.IsBlocked(db,(string)row["chat"]!))return Results.NotFound();
            long size=Convert.ToInt64(row["size"]);if(part<0||(long)part*Chunk>=size)return Results.BadRequest();
            var expected=(int)Math.Min(Chunk,size-(long)part*Chunk)+16;
            if(r.Data==null||r.Data.Length!=4*((expected+2)/3))return Results.BadRequest();
            var bytes=new byte[expected];if(!Convert.TryFromBase64String(r.Data,bytes,out var n)||n!=expected)return Results.BadRequest();
            var saved=db.One("INSERT INTO file_parts VALUES($i,$p,$b) ON CONFLICT(upload,part) DO UPDATE SET cipher=excluded.cipher WHERE cipher=excluded.cipher RETURNING part",("i",id),("p",part),("b",bytes));
            return saved==null?Results.Conflict():Results.Ok(new{ok=true});
        });
        app.MapPost("/api/files/{id}/complete",(HttpContext ctx,string id)=>{
            var row=db.One("UPDATE uploads SET ready=1 WHERE id=$i AND owner=$u AND (size+$chunk-1)/$chunk=(SELECT count(*) FROM file_parts WHERE upload=$i) RETURNING id",("i",id),("u",U(ctx)),("chunk",Chunk));
            return row==null?Results.Conflict():Results.Ok(row);
        });
        app.MapGet("/api/files/{id}/parts/{part:int}",(HttpContext ctx,string id,int part)=>{
            var row=db.One("SELECT cipher FROM file_parts JOIN uploads f ON f.id=upload JOIN conversations c ON c.id=f.chat WHERE upload=$i AND part=$p AND ready=1 AND EXISTS(SELECT 1 FROM posted_files WHERE upload=f.id) AND (c.a=$u OR c.b=$u)",("i",id),("p",part),("u",U(ctx)));
            return row==null?Results.NotFound():Results.Ok(new{data=(byte[])row["cipher"]!});
        });
        app.MapDelete("/api/files/{id}",(HttpContext ctx,string id)=>{db.Exec("DELETE FROM uploads WHERE id=$i AND owner=$u AND ready=0",("i",id),("u",U(ctx)));return Results.Ok(new{ok=true});});
    }
}
record UploadInput(string Chat,long Size);
record UploadPart(string Data);

