using System.Security.Cryptography;
static class AudioLibrary {
    const int Chunk=1048576;
    static string U(HttpContext c)=>(string)c.Items["user"]!;
    public static void Map(WebApplication app,Store db){
        db.Exec("""
          CREATE TABLE IF NOT EXISTS audio_tracks(id TEXT PRIMARY KEY,uid TEXT REFERENCES users(id) ON DELETE CASCADE,title TEXT NOT NULL,mime TEXT NOT NULL,size INTEGER NOT NULL,seconds INTEGER NOT NULL,ready INTEGER NOT NULL DEFAULT 0,created INTEGER NOT NULL);
          CREATE TABLE IF NOT EXISTS audio_parts(track TEXT REFERENCES audio_tracks(id) ON DELETE CASCADE,part INTEGER,body BLOB NOT NULL,PRIMARY KEY(track,part));
          CREATE TABLE IF NOT EXISTS audio_access(token TEXT PRIMARY KEY,track TEXT REFERENCES audio_tracks(id) ON DELETE CASCADE,session TEXT REFERENCES sessions(hash) ON DELETE CASCADE,expires INTEGER NOT NULL);
        """);
        app.MapGet("/api/music/tracks",(HttpContext ctx)=>Results.Ok(db.All("SELECT id,title,seconds,mime,size,created FROM audio_tracks WHERE uid=$u AND ready=1 ORDER BY created,id",("u",U(ctx)))));
        app.MapPost("/api/music/tracks",(HttpContext ctx,NewTrack r)=>{
            if(!Guid.TryParse(r.Id,out var op)||string.IsNullOrWhiteSpace(r.Title)||r.Title.Length>240||r.Seconds is <1 or >86400||r.Size<1||r.Mime is not("audio/mpeg" or "audio/mp4" or "audio/ogg" or "audio/wav" or "audio/x-wav" or "audio/flac" or "audio/x-flac" or "audio/webm" or "audio/aac" or "audio/x-m4a"))return Results.BadRequest();
            var uid=U(ctx);var max=Convert.ToInt64(db.One("SELECT premium FROM extras WHERE user_id=$u",("u",uid))?["premium"]??0)>0?2000000000L:700000000L;
            if(r.Size>max)return Results.StatusCode(413);var id=op.ToString("N");
            db.Exec("DELETE FROM audio_tracks WHERE ready=0 AND created<unixepoch()-86400;INSERT OR IGNORE INTO audio_tracks(id,uid,title,mime,size,seconds,created) SELECT $i,$u,$t,$m,$z,$s,unixepoch() WHERE (SELECT count(*) FROM audio_tracks WHERE uid=$u AND ready=0)<6",("u",uid),("i",id),("t",r.Title.Trim()),("m",r.Mime),("z",r.Size),("s",r.Seconds));
            return db.One("SELECT 1 FROM audio_tracks WHERE uid=$u AND id=$i AND size=$z AND mime=$m",("u",uid),("i",id),("z",r.Size),("m",r.Mime))==null?Results.Conflict():Results.Ok(new{id});
        });
        app.MapPost("/api/music/{id}/parts/{n:int}",(HttpContext ctx,string id,int n,UploadPart r)=>{
            var row=db.One("SELECT size FROM audio_tracks WHERE id=$i AND uid=$u AND ready=0",("i",id),("u",U(ctx)));
            if(row==null)return Results.NotFound();long size=(long)row["size"]!;long left=size-(long)n*Chunk;
            if(n<0||left<=0)return Results.BadRequest();int count=(int)Math.Min(left,Chunk);
            if(r.Data==null||r.Data.Length!=4*((count+2)/3))return Results.BadRequest();var bytes=new byte[count];if(!Convert.TryFromBase64String(r.Data,bytes,out var len)||len!=count)return Results.BadRequest();
            return db.One("INSERT INTO audio_parts VALUES($i,$n,$b) ON CONFLICT(track,part) DO UPDATE SET body=excluded.body WHERE body=excluded.body RETURNING part",("i",id),("n",n),("b",bytes))==null?Results.Conflict():Results.Ok(new{ok=true});
        });
        app.MapPost("/api/music/{id}/finish",(HttpContext ctx,string id)=>db.One("UPDATE audio_tracks SET ready=1 WHERE id=$i AND uid=$u AND (size+$z-1)/$z=(SELECT count(*) FROM audio_parts WHERE track=$i) RETURNING id",("i",id),("u",U(ctx)),("z",Chunk)) is {} row?Results.Ok(row):Results.Conflict());
        app.MapPost("/api/music/{id}/listen",(HttpContext ctx,string id)=>{
            var uid=U(ctx);if(db.One("SELECT 1 FROM audio_tracks WHERE id=$i AND uid=$u AND ready=1",("i",id),("u",uid))==null)return Results.NotFound();
            var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));db.Exec("DELETE FROM audio_access WHERE expires<=unixepoch();INSERT INTO audio_access VALUES($t,$i,$s,unixepoch()+14400)",("t",Crypto.Hash(token)),("i",id),("s",Crypto.Hash(ctx.Request.Headers.Authorization.ToString()[7..])));
            return Results.Ok(new{path="/media/"+token});
        });
        app.MapGet("/media/{token}",(string token)=>{
            if(token.Length!=48)return Results.NotFound();var row=db.One("SELECT t.id,t.size,t.mime FROM audio_access a JOIN audio_tracks t ON t.id=a.track JOIN sessions s ON s.hash=a.session WHERE a.token=$t AND min(a.expires,s.expires)>unixepoch()",("t",Crypto.Hash(token)));
            return row==null?Results.NotFound():Results.Stream(new TrackStream(db,(string)row["id"]!,(long)row["size"]!),(string)row["mime"]!,enableRangeProcessing:true);
        });
    }
    sealed class TrackStream(Store db,string id,long length):Stream {
        int cached=-1;byte[] bytes=[];
        public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;public override long Length=>length;public override long Position{get;set;}
        public override long Seek(long offset,SeekOrigin from){var p=(from==SeekOrigin.Begin?0:from==SeekOrigin.Current?Position:Length)+offset;if(p<0)throw new IOException();return Position=p;}
        public override int Read(byte[] dest,int offset,int count){
            if(Position>=Length||count==0)return 0;int n=(int)(Position/Chunk);
            if(cached!=n){bytes=(byte[])(db.One("SELECT body FROM audio_parts WHERE track=$i AND part=$p",("i",id),("p",n))?["body"]??throw new IOException());cached=n;}
            int start=(int)(Position%Chunk),read=Math.Min(count,bytes.Length-start);Buffer.BlockCopy(bytes,start,dest,offset,read);Position+=read;return read;
        }
        public override void Flush(){}public override void SetLength(long v)=>throw new NotSupportedException();public override void Write(byte[] b,int s,int n)=>throw new NotSupportedException();
    }
}
record NewTrack(string Id,string Title,string Mime,long Size,long Seconds);

