using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static class Commerce {
    static readonly HttpClient Client=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(12)};
    static string? Env(string key)=>Environment.GetEnvironmentVariable("CTRL_STRIPE_"+key);
    static int Positive(string key)=>int.TryParse(Env(key),out var n)&&n is >0 and <=100000000?n:0;
    static IResult Error(string msg,int code)=>Results.Json(new{error=msg},statusCode:code);
    public static void Map(WebApplication app,Store db){
        db.Exec("""
            CREATE TABLE IF NOT EXISTS checkout_orders(id TEXT PRIMARY KEY,uid TEXT REFERENCES users(id) ON DELETE CASCADE,kind TEXT NOT NULL,ref TEXT NOT NULL,cents INTEGER NOT NULL,units INTEGER NOT NULL,fulfilled INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS premium_terms(uid TEXT PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,ends INTEGER NOT NULL);
        """);
        string? key=Env("SECRET"), signing=Env("WEBHOOK_SECRET"),origin=Env("RETURN_ORIGIN");
        bool ready=key?.Length>=12&&signing?.Length>=12&&Uri.TryCreate(origin,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.AbsolutePath=="/"&&u.UserInfo.Length==0&&u.Query.Length==0&&u.Fragment.Length==0;
        var clickUnits=Positive("CLICK_UNITS");var clickPrice=Positive("CLICK_CENTS");var premiumPrice=Positive("PREMIUM_CENTS");
        app.MapGet("/api/shop",()=>Results.Ok(new{ready,clickUnits,clickPrice,premiumPrice}));
        app.MapPost("/api/shop/checkout",async(HttpContext ctx,Checkout order)=>{
            if(!ready)return Error("Оплата пока недоступна.",503);
            var uid=(string)ctx.Items["user"]!;string reference=order.Reference??"";int cents,units;string title;
            switch(order.Kind){
                case "noer":
                    if(db.One("SELECT 1 FROM noer WHERE id=$i AND uid=$u AND state='submitted'",("i",reference),("u",uid))==null)return Results.NotFound();cents=10000;units=1;title="Ctrl · NOER";break;
                case "clicks":cents=clickPrice;units=clickUnits;title="Ctrl · "+units+" Кликов";reference="";break;
                case "premium":cents=premiumPrice;units=30;title="Ctrl+ · 30 дней";reference="";break;
                default:return Results.BadRequest();
            }
            if(units==0||cents==0)return Error("Предложение пока недоступно.",503);
            if(!Guid.TryParse(order.Operation,out var op))return Results.BadRequest();
            var id=order.Kind=="noer"?reference:op.ToString("N");
            db.Exec("INSERT OR IGNORE INTO checkout_orders(id,uid,kind,ref,cents,units) VALUES($i,$u,$k,$r,$c,$v)",("i",id),("u",uid),("k",order.Kind),("r",reference),("c",cents),("v",units));
            if(db.One("SELECT 1 FROM checkout_orders WHERE id=$i AND uid=$u AND kind=$k AND ref=$r AND cents=$c AND units=$v AND fulfilled=0",("i",id),("u",uid),("k",order.Kind),("r",reference),("c",cents),("v",units))==null)return Results.Conflict();
            using var req=new HttpRequestMessage(HttpMethod.Post,"https://api.stripe.com/v1/checkout/sessions");
            req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);req.Headers.Add("Idempotency-Key",id);
            req.Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"mode","payment"},{"client_reference_id",id},{"line_items[0][quantity]","1"},{"line_items[0][price_data][currency]","usd"},{"line_items[0][price_data][unit_amount]",cents.ToString()},{"line_items[0][price_data][product_data][name]",title},{"success_url",origin!},{"cancel_url",origin!}});
            using var result=await Client.SendAsync(req,ctx.RequestAborted);if(!result.IsSuccessStatusCode)return Error("Платёжный сервис временно недоступен.",503);
            using var session=JsonDocument.Parse(await result.Content.ReadAsStringAsync());return Results.Ok(new{url=session.RootElement.GetProperty("url").GetString()});
        }).RequireRateLimiting("auth");
        app.MapPost("/payments/stripe",async(HttpContext ctx)=>{
            if(!ready)return Results.NotFound();
            using var reader=new StreamReader(ctx.Request.Body,Encoding.UTF8);string raw=await reader.ReadToEndAsync(ctx.RequestAborted);
            var parts=ctx.Request.Headers["Stripe-Signature"].ToString().Split(',');
            var ts=parts.FirstOrDefault(x=>x.StartsWith("t="))?[2..];
            if(!long.TryParse(ts,out var t)||Math.Abs((decimal)DateTimeOffset.UtcNow.ToUnixTimeSeconds()-t)>300)return Results.BadRequest();
            var expected=HMACSHA256.HashData(Encoding.UTF8.GetBytes(signing!),Encoding.UTF8.GetBytes(ts+"."+raw));
            if(!parts.Where(x=>x.StartsWith("v1=")&&x.Length==67&&x[3..].All(Uri.IsHexDigit)).Any(x=>CryptographicOperations.FixedTimeEquals(expected,Convert.FromHexString(x[3..]))))return Results.BadRequest();
            using var ev=JsonDocument.Parse(raw);var root=ev.RootElement;
            if(root.GetProperty("type").GetString() is not("checkout.session.completed" or "checkout.session.async_payment_succeeded"))return Results.Ok();
            var obj=root.GetProperty("data").GetProperty("object");if(obj.GetProperty("payment_status").GetString()!="paid"||obj.GetProperty("mode").GetString()!="payment")return Results.Ok();
            var id=obj.GetProperty("client_reference_id").GetString();if(obj.GetProperty("currency").GetString()!="usd")return Results.BadRequest();
            using var c=db.Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
            cmd.Parameters.AddWithValue("$i",id??"");cmd.Parameters.AddWithValue("$c",obj.GetProperty("amount_total").GetInt64());
            cmd.CommandText="UPDATE checkout_orders SET fulfilled=1 WHERE id=$i AND cents=$c AND fulfilled=0 RETURNING uid,kind,ref,units";
            using var rows=cmd.ExecuteReader();if(!rows.Read())return Results.Ok();
            var uid=rows.GetString(0);var kind=rows.GetString(1);var reference=rows.GetString(2);var units=rows.GetInt64(3);rows.Close();
            cmd.Parameters.AddWithValue("$u",uid);cmd.Parameters.AddWithValue("$r",reference);cmd.Parameters.AddWithValue("$v",units);
            cmd.CommandText=kind switch{
                "noer"=>"UPDATE noer SET state='paid' WHERE id=$r AND uid=$u AND state='submitted'",
                "clicks"=>"INSERT INTO extras(user_id,clicks) VALUES($u,$v) ON CONFLICT(user_id) DO UPDATE SET clicks=clicks+$v",
                _=>"INSERT INTO premium_terms VALUES($u,unixepoch()+86400*$v) ON CONFLICT(uid) DO UPDATE SET ends=MAX(unixepoch(),ends)+86400*$v; INSERT INTO extras(user_id,premium) VALUES($u,1) ON CONFLICT(user_id) DO UPDATE SET premium=1"};
            cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO notices(uid,body,created) VALUES($u,'Оплата подтверждена.',unixepoch())";cmd.ExecuteNonQuery();tx.Commit();return Results.Ok();
        });
    }
    public static void Expire(Store db,string uid)=>db.Exec("BEGIN IMMEDIATE;UPDATE extras SET premium=0 WHERE user_id=$u AND EXISTS(SELECT 1 FROM premium_terms WHERE uid=$u AND ends<=unixepoch());DELETE FROM premium_terms WHERE uid=$u AND ends<=unixepoch();COMMIT;",("u",uid));
}
record Checkout(string Kind,string? Reference,string Operation);

