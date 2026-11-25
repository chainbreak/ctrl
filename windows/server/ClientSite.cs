using System.IO.Compression;

static class ClientSite {
    public static void Map(WebApplication app) {
        using var stream=typeof(ClientSite).Assembly.GetManifestResourceStream("Ctrl.Site")!;
        using var zip=new ZipArchive(stream);
        var files=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        foreach(var entry in zip.Entries) {
            using var input=entry.Open(); using var bytes=new MemoryStream(); input.CopyTo(bytes);
            files["/"+entry.FullName]=bytes.ToArray();
        }
        var types=new Dictionary<string,string>{
            [".html"]="text/html; charset=utf-8",[".css"]="text/css",[".js"]="text/javascript",[".png"]="image/png",
            [".tgs"]="application/octet-stream",[".md"]="text/plain",[".LICENSE"]="text/plain"
        };
        foreach(var file in files) {
            var contentType=types.GetValueOrDefault(Path.GetExtension(file.Key),"application/octet-stream");
            app.MapGet(file.Key,()=>Results.Bytes(file.Value,contentType));
        }
        app.MapGet("/",()=>Results.Bytes(files["/index.html"],"text/html; charset=utf-8"));
    }
}

