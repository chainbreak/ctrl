using System.Text.Json;
static class BotCheck {
    static readonly HttpClient Cloudflare=new(){Timeout=TimeSpan.FromSeconds(8)};
    public static void Map(WebApplication app) {
        string? site=Environment.GetEnvironmentVariable("CTRL_TURNSTILE_SITE_KEY"), secret=Environment.GetEnvironmentVariable("CTRL_TURNSTILE_SECRET");
        var hosts=(Environment.GetEnvironmentVariable("CTRL_TURNSTILE_HOSTS")??string.Empty).Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        var active=!string.IsNullOrWhiteSpace(site)||!string.IsNullOrWhiteSpace(secret);
        app.MapGet("/api/auth-options",()=>Results.Ok(new{turnstile=active?site:null}));
        app.Use(async(ctx,next)=>{
            if(!active||ctx.Request.Method!="POST"||ctx.Request.Path.Value is not("/api/register" or "/api/login" or "/api/ton/challenge" or "/api/support")){await next();return;}
            if(string.IsNullOrWhiteSpace(site)||string.IsNullOrWhiteSpace(secret)||hosts.Length==0)throw new TonError(503,"Защита входа ещё не настроена.");
            var token=ctx.Request.Headers["X-Ctrl-Turnstile"].ToString();
            if(token.Length is <1 or >2048)throw new TonError(400,"Подтвердите проверку и попробуйте ещё раз.");
            bool passed;
            try{
                using var form=new FormUrlEncodedContent(new Dictionary<string,string>{{"secret",secret},{"response",token}});
                using var response=await Cloudflare.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify",form,ctx.RequestAborted);
                response.EnsureSuccessStatusCode();using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ctx.RequestAborted));var root=doc.RootElement;
                passed=root.GetProperty("success").GetBoolean()&&root.TryGetProperty("hostname",out var h)&&hosts.Contains(h.GetString(),StringComparer.OrdinalIgnoreCase)&&root.TryGetProperty("action",out var a)&&a.GetString()=="ctrl_access";
            }catch(HttpRequestException){throw new TonError(503,"Проверка входа временно недоступна.");}
            if(!passed)throw new TonError(403,"Повторите проверку входа.");await next();
        });
    }
}

