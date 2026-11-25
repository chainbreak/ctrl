using Android.App;
using Permission=Android.Content.PM.Permission;
using Android.OS;
using Android.Content;
using Android.Content.PM;
using Android.Webkit;
using Android.Widget;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebView=Android.Webkit.WebView;
using Uri=System.Uri;

namespace Ctrl.Mobile;
[Activity(Label="Ctrl",MainLauncher=true,Exported=true,ConfigurationChanges=ConfigChanges.Orientation|ConfigChanges.ScreenSize|ConfigChanges.KeyboardHidden)]
public sealed class MainActivity:Activity {
    WebView? web; string origin="";WebMessagePort? channel; DeviceVault? vault;
    string? downloadRequest,downloadId;Stream? downloadStream;Android.Net.Uri? downloadUri;
    IValueCallback? fileResult;PermissionRequest? media;
    ISharedPreferences Pref=>GetSharedPreferences("ctrl",FileCreationMode.Private)!;
    protected override void OnCreate(Bundle? state){base.OnCreate(state);origin=Pref.GetString("origin","")!;if(Valid(origin))Open();else Configure();}
    static bool Valid(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.AbsolutePath=="/"&&u.Query==""&&u.Fragment==""&&u.UserInfo=="";
    bool Internal(string? value)=>Uri.TryCreate(value,UriKind.Absolute,out var u)&&u.GetLeftPart(UriPartial.Authority)==origin&&u.UserInfo=="";
    void Configure(){
        channel?.Close();web?.Destroy();web=null;
        var layout=new LinearLayout(this){Orientation=Orientation.Vertical};layout.SetPadding(40,84,40,30);
        var title=new TextView(this){Text="Ctrl",TextSize=36};var address=new EditText(this){Hint="HTTPS-адрес сервера",Text=origin};
        var submit=new Button(this){Text="Подключить"};layout.AddView(title);layout.AddView(address);layout.AddView(submit);SetContentView(layout);
        submit.Click+=async(_,_)=>{var s=(address.Text??"").Trim().TrimEnd('/');if(!Valid(s)){address.Error="Укажите HTTPS-адрес.";return;}submit.Enabled=false;
            try{using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(8)};using var json=JsonDocument.Parse(await http.GetStringAsync(s+"/health"));if(json.RootElement.GetProperty("service").GetString()!="ctrl")throw new Exception();
                origin=new Uri(s).GetLeftPart(UriPartial.Authority);Pref.Edit()!.PutString("origin",origin)!.Apply();Open();
            }catch{address.Error="Сервер Ctrl не отвечает.";submit.Enabled=true;}};
    }
    void Open(){web=new WebView(this);var s=web.Settings;s.JavaScriptEnabled=true;s.DomStorageEnabled=true;s.AllowFileAccess=false;s.AllowContentAccess=false;s.MixedContentMode=MixedContentHandling.NeverAllow;s.UserAgentString+=" CtrlMobileAndroid/1";web.SetWebViewClient(new Navigation(this));web.SetWebChromeClient(new Chrome(this));SetContentView(web);web.LoadUrl(origin+"/");}
    sealed class Navigation(MainActivity app):WebViewClient {
        public override bool ShouldOverrideUrlLoading(WebView? view,IWebResourceRequest? req){
            if(req?.IsForMainFrame!=true)return false;var url=req.Url?.ToString();if(app.Internal(url))return false;
            if(req.HasGesture&&Uri.TryCreate(url,UriKind.Absolute,out var u)&&u.Scheme is "https" or "tonkeeper" or "tc")try{app.StartActivity(new Intent(Intent.ActionView,req.Url));}catch(ActivityNotFoundException){}
            return true;
        }
        public override void OnPageFinished(WebView? view,string? url){base.OnPageFinished(view,url);if(view==null||!app.Internal(url))return;
            app.channel?.Close();var ports=view.CreateWebMessageChannel()!;app.channel=ports[0];app.channel.SetWebMessageCallback(new Receiver(app));
            view.PostWebMessage(new WebMessage("ctrl.mobile.v1",[ports[1]]),Android.Net.Uri.Parse(app.origin)!);
        }
    }
    sealed class Receiver(MainActivity app):WebMessagePort.WebMessageCallback {
        public override void OnMessage(WebMessagePort? port,WebMessage? message){if(port==null||port!=app.channel||!app.Internal(app.web?.Url)||message?.Data==null)return;
            try{using var doc=JsonDocument.Parse(message.Data);var a=doc.RootElement;var id=a.GetProperty("id").GetString();
                try{var action=a.GetProperty("action").GetString();
                    if(action=="file-open"){
                        if(app.downloadRequest!=null||app.downloadStream!=null)throw new IOException();app.downloadRequest=id;
                        var name=Path.GetFileName(a.GetProperty("name").GetString())??"file";
                        var picker=new Intent(Intent.ActionCreateDocument);picker.AddCategory(Intent.CategoryOpenable);picker.SetType("application/octet-stream");picker.PutExtra(Intent.ExtraTitle,name);
                        try{app.StartActivityForResult(picker,43);}catch{app.downloadRequest=null;throw;}return;
                    }
                    object result=action switch{
                    "file-write" or "file-finish" or "file-cancel"=>app.SavePart(action,id,a),
                    "vault"=>new{id,value=(app.vault??=new DeviceVault(app.FilesDir!.AbsolutePath,app.VaultPassword())).Run(app.origin+"|"+a.GetProperty("scope").GetString(),a)},
                    "install-proof"=>app.Sign(id,a.GetProperty("statement").GetString()!),_=>throw new InvalidOperationException()};
                    port.PostMessage(new WebMessage(JsonSerializer.Serialize(result)));
                }catch{port.PostMessage(new WebMessage(JsonSerializer.Serialize(new{id,error="Не удалось открыть ключ устройства."})));}}
            catch(JsonException){}
        }
    }
    object SavePart(string action,string? id,JsonElement args){
        if(downloadStream==null||args.GetProperty("handle").GetString()!=downloadId)throw new IOException();
        if(action=="file-write"){var b=Convert.FromBase64String(args.GetProperty("data").GetString()!);if(b.Length>1048576)throw new IOException();downloadStream.Write(b);}
        else{downloadStream.Dispose();downloadStream=null;downloadId=null;if(action=="file-cancel"&&downloadUri!=null)Android.Provider.DocumentsContract.DeleteDocument(ContentResolver!,downloadUri);downloadUri=null;}
        return new{id,ok=true};
    }
    protected override void OnDestroy(){downloadStream?.Dispose();channel?.Close();web?.Destroy();base.OnDestroy();}
    static KeyStore Store(){var ks=KeyStore.GetInstance("AndroidKeyStore")!;ks.Load(null);return ks;}
    string VaultPassword(){
        const string name="ctrl-vault-wrap";using var ks=Store();if(!ks.ContainsAlias(name)){using var g=KeyGenerator.GetInstance("AES","AndroidKeyStore")!;
            g.Init(new KeyGenParameterSpec.Builder(name,KeyStorePurpose.Encrypt|KeyStorePurpose.Decrypt).SetBlockModes("GCM")!.SetEncryptionPaddings("NoPadding")!.Build());g.GenerateKey();}
        using var key=ks.GetKey(name,null);using var cipher=Cipher.GetInstance("AES/GCM/NoPadding")!;
        var packed=Pref.GetString("vault",null);byte[] raw;
        if(packed==null){raw=RandomNumberGenerator.GetBytes(32);cipher.Init(Javax.Crypto.CipherMode.EncryptMode,key);var enc=cipher.DoFinal(raw)!;var iv=cipher.GetIV()!;if(!Pref.Edit()!.PutString("vault",Convert.ToBase64String(iv.Concat(enc).ToArray()))!.Commit())throw new IOException();}
        else{var bytes=Convert.FromBase64String(packed);cipher.Init(Javax.Crypto.CipherMode.DecryptMode,key,new Javax.Crypto.Spec.GCMParameterSpec(128,bytes[..12]));raw=cipher.DoFinal(bytes[12..])!;}
        var hex=Convert.ToHexString(raw);CryptographicOperations.ZeroMemory(raw);return hex;
    }
    object Sign(string? id,string statement){
        if(!statement.StartsWith("ctrl-device-v1|")||statement.Length>240)throw new ArgumentException();
        string alias="ctrl-install-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin)))[..20];using var ks=Store();
        if(!ks.ContainsAlias(alias)){using var gen=KeyPairGenerator.GetInstance("EC","AndroidKeyStore")!;
            gen.Initialize(new KeyGenParameterSpec.Builder(alias,KeyStorePurpose.Sign|KeyStorePurpose.Verify).SetAlgorithmParameterSpec(new Java.Security.Spec.ECGenParameterSpec("secp256r1"))!.SetDigests("SHA-256")!.Build());gen.GenerateKeyPair();}
        using var sig=Java.Security.Signature.GetInstance("SHA256withECDSA")!;sig.InitSign((IPrivateKey)ks.GetKey(alias,null)!);sig.Update(Encoding.UTF8.GetBytes(statement));
        var asn=new AsnReader(sig.Sign()!,AsnEncodingRules.DER).ReadSequence();var raw=new byte[64];
        for(int i=0;i<2;i++){var num=asn.ReadInteger().ToByteArray(true,true);num.CopyTo(raw,(i+1)*32-num.Length);}
        return new{id,publicKey=Convert.ToBase64String(ks.GetCertificate(alias)!.PublicKey!.GetEncoded()!),signature=Convert.ToBase64String(raw)};
    }
    sealed class Chrome(MainActivity app):WebChromeClient {
        public override void OnPermissionRequest(PermissionRequest? r){if(r==null)return;if(!app.Internal(r.Origin?.ToString())||app.media!=null){r.Deny();return;}
            var p=Permissions(r);if(p.Length==0){r.Deny();return;}if(p.All(v=>app.CheckSelfPermission(v)==Permission.Granted)){r.Grant((r.GetResources()??[]).Where(x=>x is "android.webkit.resource.AUDIO_CAPTURE" or "android.webkit.resource.VIDEO_CAPTURE").ToArray());return;}app.media=r;app.RequestPermissions(p,42);}
        public override void OnPermissionRequestCanceled(PermissionRequest? r){if(app.media==r)app.media=null;}
        public override bool OnShowFileChooser(WebView? view,IValueCallback? callback,FileChooserParams? options){
            if(!app.Internal(view?.Url)||callback==null||options==null)return false;app.fileResult?.OnReceiveValue(null);app.fileResult=callback;
            try{app.StartActivityForResult(options.CreateIntent(),41);}catch(ActivityNotFoundException){callback.OnReceiveValue(null);app.fileResult=null;}return true;}
    }
    static string[] Permissions(PermissionRequest r)=> (r.GetResources()??[]).Select(x=>x switch{"android.webkit.resource.AUDIO_CAPTURE"=>Android.Manifest.Permission.RecordAudio,"android.webkit.resource.VIDEO_CAPTURE"=>Android.Manifest.Permission.Camera,_=>""}).Where(x=>x!="").ToArray();
    public override void OnRequestPermissionsResult(int code,string[] names,Permission[] results){base.OnRequestPermissionsResult(code,names,results);if(code==42&&media!=null){var r=media;media=null;
        if(Internal(web?.Url)&&results.Length>0&&results.All(x=>x==Permission.Granted))r.Grant((r.GetResources()??[]).Where(x=>x.EndsWith("_CAPTURE")).ToArray());else r.Deny();}}
    protected override void OnActivityResult(int code,Result result,Intent? data){base.OnActivityResult(code,result,data);if(code==43&&downloadRequest!=null){
        var id=downloadRequest;downloadRequest=null;
        try{if(result!=Result.Ok||data?.Data==null){channel?.PostMessage(new WebMessage(JsonSerializer.Serialize(new{id,cancelled=true})));return;}
            downloadUri=data.Data;downloadStream=ContentResolver!.OpenOutputStream(downloadUri!)??throw new IOException();
            downloadId=Guid.NewGuid().ToString("N");channel?.PostMessage(new WebMessage(JsonSerializer.Serialize(new{id,handle=downloadId})));
        }catch{channel?.PostMessage(new WebMessage(JsonSerializer.Serialize(new{id,error="Не удалось сохранить файл."})));}return;
    }if(code==41&&fileResult!=null){fileResult.OnReceiveValue(WebChromeClient.FileChooserParams.ParseResult((int)result,data));fileResult=null;}}
    public override bool OnCreateOptionsMenu(Android.Views.IMenu? menu){menu?.Add(new Java.Lang.String("Подключение"));return true;}
    public override bool OnOptionsItemSelected(Android.Views.IMenuItem item){Configure();return true;}
    public override void OnBackPressed(){if(web?.CanGoBack()==true)web.GoBack();else if(web!=null)Configure();else Finish();}
}

