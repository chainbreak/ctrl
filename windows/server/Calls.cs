using System.Text.Json;
using Microsoft.Data.Sqlite;

static class Calls {
    public static void Map(WebApplication app,Store db) {
        db.Exec("""
            CREATE TABLE IF NOT EXISTS calls(id TEXT PRIMARY KEY,chat TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,caller TEXT NOT NULL,callee TEXT NOT NULL,mode TEXT NOT NULL,state TEXT NOT NULL,offer TEXT NOT NULL,answer TEXT,expires INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS current_calls ON calls(callee,expires);
            """);
        static string U(HttpContext c)=>(string)c.Items["user"]!;
        static long Now()=>DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        app.MapGet("/api/calls/ice",()=>{
            var json=Environment.GetEnvironmentVariable("CTRL_ICE_SERVERS")??"[]";
            using var settings=JsonDocument.Parse(json);return Results.Ok(settings.RootElement.Clone());
        });
        app.MapPost("/api/calls",(HttpContext ctx,CallOffer request)=>{
            db.Exec("DELETE FROM calls WHERE expires<=$t",("t",Now()));
            var peer=db.One("SELECT CASE WHEN a=$u THEN b ELSE a END peer FROM conversations WHERE id=$c AND (a=$u OR b=$u)",("u",U(ctx)),("c",request.Chat));
            if(peer==null||DesktopFeatures.IsBlocked(db,request.Chat))return Results.NotFound();
            if(request.Mode is not("audio" or "video")||!SignalDirectory.ValidEnvelope(db,request.Chat,U(ctx),request.Offer))return Results.BadRequest();
            var id=Guid.NewGuid().ToString("N");using var c=db.Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.Parameters.AddWithValue("$u",U(ctx));cmd.Parameters.AddWithValue("$p",peer["peer"]!);
            cmd.CommandText="SELECT 1 FROM calls WHERE caller IN($p,$u) OR callee IN($p,$u) LIMIT 1";
            if(cmd.ExecuteScalar()!=null)return Results.Conflict(new{error="Линия занята."});
            cmd.CommandText="INSERT INTO calls VALUES($i,$c,$u,$p,$m,'ringing',$o,NULL,$e)";
            cmd.Parameters.AddWithValue("$i",id);cmd.Parameters.AddWithValue("$c",request.Chat);cmd.Parameters.AddWithValue("$m",request.Mode);
            cmd.Parameters.AddWithValue("$o",request.Offer);cmd.Parameters.AddWithValue("$e",Now()+60);cmd.ExecuteNonQuery();tx.Commit();return Results.Ok(new{id});
        });
        app.MapGet("/api/calls",(HttpContext ctx)=>Results.Ok(db.One("SELECT * FROM calls WHERE (caller=$u OR callee=$u) AND expires>$t ORDER BY expires DESC LIMIT 1",("u",U(ctx)),("t",Now()))??new Dictionary<string,object?>()));
        app.MapPost("/api/calls/{id}/answer",(HttpContext ctx,string id,CallAnswer r)=>{
            var row=db.One("SELECT chat FROM calls WHERE id=$i AND callee=$u AND state='ringing' AND expires>$t",("i",id),("u",U(ctx)),("t",Now()));
            if(row==null)return Results.NotFound();
            var chat=(string)row["chat"]!;if(DesktopFeatures.IsBlocked(db,chat)||!SignalDirectory.ValidEnvelope(db,chat,U(ctx),r.Answer))return Results.BadRequest();
            var changed=db.One("UPDATE calls SET state='active',answer=$a,expires=$t WHERE id=$i AND state='ringing' RETURNING id",("a",r.Answer),("t",Now()+180),("i",id));
            return changed!=null?Results.Ok(changed):Results.Conflict();
        });
        app.MapPost("/api/calls/{id}/pulse",(HttpContext ctx,string id)=>{
            db.Exec("UPDATE calls SET expires=$t WHERE id=$i AND state='active' AND expires>$n AND (caller=$u OR callee=$u)",("n",Now()),("t",Now()+60),("i",id),("u",U(ctx)));return Results.Ok(new{ok=true});
        });
        app.MapDelete("/api/calls/{id}",(HttpContext ctx,string id)=>{db.Exec("DELETE FROM calls WHERE id=$i AND (caller=$u OR callee=$u)",("i",id),("u",U(ctx)));return Results.Ok(new{ok=true});});
    }
}
record CallOffer(string Chat,string Mode,string Offer);
record CallAnswer(string Answer);

