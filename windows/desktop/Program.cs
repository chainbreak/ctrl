using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
namespace Ctrl;
static class Program {
 [STAThread] static void Main(){ApplicationConfiguration.Initialize();Application.Run(new MessengerWindow());}
}
sealed class MessengerWindow:Form {
 readonly WebView2 web=new(){Dock=DockStyle.Fill,DefaultBackgroundColor=Color.FromArgb(8,8,10)};
 readonly Label loading=new(){Dock=DockStyle.Fill,Text="ctrl",ForeColor=Color.White,TextAlign=ContentAlignment.MiddleCenter,Font=new Font("Segoe UI",26,FontStyle.Bold)};
 readonly HttpClient http=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(42)};
 readonly string root=Environment.GetEnvironmentVariable("CTRL_CLIENT_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ctrl-connected");
 DeviceVault? vault;
 string server="http://127.0.0.1:48734";
 [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
 public MessengerWindow(){Text="ctrl";BackColor=Color.FromArgb(8,8,10);ClientSize=new Size(1280,840);MinimumSize=new Size(800,580);StartPosition=FormStartPosition.CenterScreen;using var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("Ctrl.AppIcon");if(s!=null)Icon=new Icon(s);Controls.Add(web);Controls.Add(loading);Shown+=async(_,_)=>await StartAsync();FormClosed+=(_,_)=>{web.Dispose();http.Dispose();};}
 protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);int dark=1;_=DwmSetWindowAttribute(Handle,20,ref dark,4);}
 static bool ValidServer(string s)=>Uri.TryCreate(s,UriKind.Absolute,out var u)&&string.IsNullOrEmpty(u.UserInfo)&&string.IsNullOrEmpty(u.Query)&&string.IsNullOrEmpty(u.Fragment)&&u.AbsolutePath=="/"&&(u.Scheme=="https"||(u.Scheme=="http"&&u.IsLoopback));
 string SessionFile=>Path.Combine(root,"session.dat");
 string LoadToken(){try{return File.Exists(SessionFile)?Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(SessionFile),null,DataProtectionScope.CurrentUser)):"";}catch{return "";}}
 void SaveToken(string token){if(token.Length==0){if(File.Exists(SessionFile))File.Delete(SessionFile);return;}if(token.Length!=64)throw new InvalidOperationException();File.WriteAllBytes(SessionFile,ProtectedData.Protect(Encoding.UTF8.GetBytes(token),null,DataProtectionScope.CurrentUser));}
 static async Task Resource(string name,string path){
    var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
    try {
        using var input=typeof(MessengerWindow).Assembly.GetManifestResourceStream(name)!;
        using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) await input.CopyToAsync(output);
        try {File.Move(temp,path);} catch(IOException) when(File.Exists(path)) {}
    } finally {if(File.Exists(temp)) File.Delete(temp);}
 }
 async Task EnsureServerAsync(){
 if(server!="http://127.0.0.1:48734")return;
 try{using var timeout=new CancellationTokenSource(900);using var response=await http.GetAsync(server+"/health",timeout.Token);if(response.IsSuccessStatusCode)return;}catch{}
 var directory=Path.Combine(root,"server",typeof(MessengerWindow).Assembly.ManifestModule.ModuleVersionId.ToString("N"));Directory.CreateDirectory(directory);
 var executable=Path.Combine(directory,"ctrl-server.exe");
 if(!File.Exists(executable))await Resource("Ctrl.Server",executable);
 var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppContext.BaseDirectory};
 start.Environment["CTRL_APP_DIRECTORY"]=AppContext.BaseDirectory;
 Process.Start(start);
 for(int n=0;n<30;n++){try{using var timeout=new CancellationTokenSource(700);using var response=await http.GetAsync(server+"/health",timeout.Token);if(response.IsSuccessStatusCode)return;}catch{}await Task.Delay(200);}
 }
 async Task StartAsync(){try{Directory.CreateDirectory(root);var config=Path.Combine(root,"server.txt");if(File.Exists(config)){var value=File.ReadAllText(config).Trim();if(ValidServer(value))server=value.TrimEnd('/'); if(server is "http://127.0.0.1:48731" or "http://127.0.0.1:48733")server="http://127.0.0.1:48734";}
 await EnsureServerAsync();
 var assets=Path.Combine(root,"app",typeof(MessengerWindow).Assembly.ManifestModule.ModuleVersionId.ToString("N"));
 if(!Directory.Exists(assets)){
    var stage=assets+"."+Guid.NewGuid().ToString("N")+".tmp";
    using var zip=typeof(MessengerWindow).Assembly.GetManifestResourceStream("Ctrl.Site")!;
    System.IO.Compression.ZipFile.ExtractToDirectory(zip,stage);
    try{Directory.Move(stage,assets);}catch(IOException) when(Directory.Exists(assets)){Directory.Delete(stage,true);}
 }
 var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"profile"));await web.EnsureCoreWebView2Async(env);
 web.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;web.CoreWebView2.Settings.IsStatusBarEnabled=false;web.CoreWebView2.Settings.AreDevToolsEnabled=false;
 web.CoreWebView2.SetVirtualHostNameToFolderMapping("ctrl.local",assets,CoreWebView2HostResourceAccessKind.DenyCors);
 web.CoreWebView2.NavigationStarting+=(_,e)=>{
    if(Uri.TryCreate(e.Uri,UriKind.Absolute,out var u) && u.Scheme=="https" && u.Host=="ctrl.local")return;
    e.Cancel=true; if(e.IsUserInitiated)OpenWalletLink(e.Uri);
 };
 web.CoreWebView2.NewWindowRequested+=(_,e)=>{e.Handled=true; if(e.IsUserInitiated)OpenWalletLink(e.Uri);};
 web.CoreWebView2.WebMessageReceived+=Message;
 web.CoreWebView2.NavigationCompleted+=(_,e)=>{if(e.IsSuccess){loading.Visible=false;web.Focus();}else loading.Text="Не удалось открыть ctrl.";};
 web.CoreWebView2.Navigate("https://ctrl.local/index.html");
 }catch(Exception ex){MessageBox.Show(this,"Не удалось открыть ctrl.\n"+ex.Message,"ctrl",MessageBoxButtons.OK,MessageBoxIcon.Error);Close();}}
 static void OpenWalletLink(string link) {
    if(!Uri.TryCreate(link,UriKind.Absolute,out var u) || u.UserInfo.Length>0 || u.Scheme is not ("https" or "tonkeeper" or "tc"))return;
    var query=System.Web.HttpUtility.ParseQueryString(u.Query);
    bool connect=query["v"]=="2" && query["id"] is string id && id.Length==64 && id.All(Uri.IsHexDigit);
    bool telegram=u.Scheme=="https" && u.Host=="t.me" && query["startapp"] is {Length:>0};
    if(!connect&&!telegram)return;
    try {Process.Start(new ProcessStartInfo(u.AbsoluteUri){UseShellExecute=true});}catch(System.ComponentModel.Win32Exception){}
 }
 async void Message(object? sender,CoreWebView2WebMessageReceivedEventArgs e){string id="";try{
 if(e.Source!="https://ctrl.local/index.html")return;
 var raw=e.WebMessageAsJson;if(raw.Length>2_500_000)return;using var doc=JsonDocument.Parse(raw);var p=doc.RootElement;id=p.GetProperty("id").GetString()??"";if(!Guid.TryParse(id,out _))return;
 var action=p.GetProperty("action").GetString();
 switch(action){
 case "install-proof":{
    var statement=p.GetProperty("statement").GetString()??"";if(statement.Length>220||!statement.StartsWith("ctrl-device-v1|"))throw new InvalidOperationException();
    var alias="Ctrl.Messenger.Installation.v1";
    using var key=CngKey.Exists(alias)?CngKey.Open(alias):CngKey.Create(CngAlgorithm.ECDsaP256,alias,new CngKeyCreationParameters{ExportPolicy=CngExportPolicies.None});
    using var signer=new ECDsaCng(key);
    Reply(new{id,publicKey=Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo()),signature=Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(statement),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation))});return;
 }
 case "vault":{
    var ns=p.GetProperty("scope").GetString();if(string.IsNullOrWhiteSpace(ns)||ns.Length>200)throw new InvalidOperationException();
    vault??=new DeviceVault(root);var value=vault.Run(server+"|"+ns,p);Reply(new{id,value});return;
 }
 case "checkout":{if(!Uri.TryCreate(p.GetProperty("url").GetString(),UriKind.Absolute,out var url)||url.Scheme!="https"||url.Host!="checkout.stripe.com"||url.UserInfo!="")throw new InvalidOperationException("Недопустимый адрес оплаты.");Process.Start(new ProcessStartInfo(url.ToString()){UseShellExecute=true});Reply(new{id,ok=true});return;}
 case "init":Reply(new{id,url=server,token=LoadToken()});return;
 case "session":SaveToken(p.GetProperty("token").GetString()??"");Reply(new{id,ok=true});return;
 case "connect":{var value=(p.GetProperty("url").GetString()??"").TrimEnd('/');if(!ValidServer(value))throw new InvalidOperationException("Укажите HTTPS-адрес сервера или http://127.0.0.1:48734.");using var response=await http.GetAsync(value+"/health");response.EnsureSuccessStatusCode();using var health=JsonDocument.Parse(await response.Content.ReadAsStringAsync());if(health.RootElement.GetProperty("service").GetString()!="ctrl")throw new InvalidOperationException("По этому адресу не найден сервер ctrl.");server=value;File.WriteAllText(Path.Combine(root,"server.txt"),server);SaveToken("");Reply(new{id,url=server});return;}
 case "api":{var path=p.GetProperty("path").GetString()??"";var method=p.GetProperty("method").GetString()??"GET";if(!(path.StartsWith("/api/",StringComparison.Ordinal)||path=="/health")||path.Contains("..")||path.Contains('\\')||path.Contains('#')||method is not("GET" or "POST" or "PATCH" or "DELETE"))return;
 using var request=new HttpRequestMessage(new HttpMethod(method),server+path);var token=p.TryGetProperty("token",out var t)?t.GetString():null;if(!string.IsNullOrEmpty(token))request.Headers.Authorization=new("Bearer",token);
 if(p.TryGetProperty("human",out var check)&&check.ValueKind==JsonValueKind.String)request.Headers.TryAddWithoutValidation("X-Ctrl-Turnstile",check.GetString());
 if(p.TryGetProperty("body",out var body)&&body.ValueKind!=JsonValueKind.Undefined)request.Content=new StringContent(body.GetRawText(),Encoding.UTF8,"application/json");
 using var result=await http.SendAsync(request);var text=await result.Content.ReadAsStringAsync();JsonElement? data=null;try{using var json=JsonDocument.Parse(text);data=json.RootElement.Clone();}catch(JsonException){}Reply(new{id,status=(int)result.StatusCode,data});return;}
 }
 }catch(Exception ex){var error=ex is InvalidOperationException?ex.Message:"Не удаётся связаться с сервером. Проверьте подключение.";Reply(new{id,error});}}
 void Reply(object value){if(!IsDisposed&&web.CoreWebView2!=null)web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value));}
}
