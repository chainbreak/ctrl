using System.Text.Json;
static class Moderation {
    static readonly HttpClient Client=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(6)};
    public static async Task<bool> Hold(string text,CancellationToken ct){
        if(Community.NeedsReview(text))return true;
        var endpoint=Environment.GetEnvironmentVariable("CTRL_CONTENT_SAFETY_ENDPOINT");var key=Environment.GetEnvironmentVariable("CTRL_CONTENT_SAFETY_KEY");
        if(string.IsNullOrEmpty(endpoint)&&string.IsNullOrEmpty(key))return false;
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var url)||url.Scheme!="https"||url.UserInfo.Length!=0||string.IsNullOrEmpty(key))return true;
        try{
            using var req=new HttpRequestMessage(HttpMethod.Post,new Uri(url,"/contentsafety/text:analyze?api-version=2024-09-01"));
            req.Headers.Add("Ocp-Apim-Subscription-Key",key);req.Content=JsonContent.Create(new{text,outputType="FourSeverityLevels"});
            using var result=await Client.SendAsync(req,ct);if(!result.IsSuccessStatusCode)return true;
            using var json=JsonDocument.Parse(await result.Content.ReadAsStringAsync(ct));var items=json.RootElement.GetProperty("categoriesAnalysis");
            return items.GetArrayLength()!=4||items.EnumerateArray().Any(x=>x.GetProperty("severity").GetInt32()>=4);
        }catch(Exception e)when(e is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or TaskCanceledException){return true;}
    }
}

