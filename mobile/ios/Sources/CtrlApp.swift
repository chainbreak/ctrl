import SwiftUI
import WebKit
import CryptoKit
import UIKit

private func canonical(_ raw: String) -> String? {
    guard let u=URLComponents(string:raw),u.scheme=="https",let host=u.host,!host.isEmpty,u.user==nil,u.password==nil,u.query==nil,u.fragment==nil,["","/"].contains(u.path) else { return nil }
    var v=URLComponents();v.scheme="https";v.host=host;v.port=u.port;return v.string
}
@main struct CtrlApp:App {
    var body:some Scene { WindowGroup { Root().preferredColorScheme(.dark) } }
}
struct Root:View {
    @AppStorage("ctrl-server") var origin=""
    @State private var address="",message="",pending=false
    var body:some View { if !origin.isEmpty { VStack(spacing:0){ Site(origin:origin); Button("Подключение"){address=origin;origin=""}.font(.footnote).padding(5) } } else {
        VStack(alignment:.leading,spacing:18) {Text("Ctrl").font(.largeTitle.bold()); TextField("HTTPS-адрес сервера",text:$address).textInputAutocapitalization(.never).keyboardType(.URL).textFieldStyle(.roundedBorder)
            Button("Подключить"){ Task { @MainActor in
                guard let url=canonical(address.trimmingCharacters(in:.whitespacesAndNewlines)),let health=URL(string:url+"/health") else {message="Укажите HTTPS-адрес.";return}
                pending=true;defer{pending=false}
                do{let (bytes,response)=try await URLSession.shared.data(from:health);guard response.url==health,(try JSONSerialization.jsonObject(with:bytes) as? [String:Any])?["service"] as? String=="ctrl" else{throw LocalError.input};origin=url;message=""}catch{message="Сервер Ctrl не отвечает."}
            }}.buttonStyle(.borderedProminent).tint(.gray).disabled(pending)
            if !message.isEmpty{Text(message).font(.footnote)}
        }.padding(32) }
    }
}
struct Site:UIViewRepresentable {
    let origin:String
    func makeCoordinator()->Coordinator{Coordinator(origin)}
    func makeUIView(context:Context)->WKWebView{
        let config=WKWebViewConfiguration();config.applicationNameForUserAgent="CtrlMobileiOS/1";config.allowsInlineMediaPlayback=true
        config.userContentController.add(context.coordinator,name:"ctrl")
        let view=WKWebView(frame:.zero,configuration:config);view.navigationDelegate=context.coordinator;view.uiDelegate=context.coordinator;view.isOpaque=false;view.backgroundColor = .black
        view.load(URLRequest(url:URL(string:origin+"/")!));return view
    }
    func updateUIView(_ view:WKWebView,context:Context){}
    static func dismantleUIView(_ view:WKWebView,coordinator:Coordinator){view.configuration.userContentController.removeScriptMessageHandler(forName:"ctrl");view.stopLoading()}
    final class Coordinator:NSObject,WKNavigationDelegate,WKUIDelegate,WKScriptMessageHandler,UIDocumentPickerDelegate {
        let origin:String;var vault:Vault?
        var output:FileHandle?, outputFile:URL?, exportFile:URL?, fileId:String?
        func documentPickerWasCancelled(_ controller:UIDocumentPickerViewController){finishExport()}
        func documentPicker(_ controller:UIDocumentPickerViewController,didPickDocumentsAt urls:[URL]){finishExport()}
        func finishExport(){if let url=exportFile{try? FileManager.default.removeItem(at:url.deletingLastPathComponent())};exportFile=nil}
        deinit{try? output?.close();if let url=outputFile{try? FileManager.default.removeItem(at:url.deletingLastPathComponent())}}
        init(_ origin:String){self.origin=origin}
        func internalURL(_ url:URL?)->Bool{
            guard let url=url,let v=URLComponents(url:url,resolvingAgainstBaseURL:false),v.user==nil,v.password==nil else{return false}
            var u=URLComponents();u.scheme=v.scheme;u.host=v.host;u.port=v.port;return u.string==origin
        }
        func webView(_ webView:WKWebView,decidePolicyFor action:WKNavigationAction,decisionHandler:@escaping(WKNavigationActionPolicy)->Void){
            if action.targetFrame?.isMainFrame==false || internalURL(action.request.url) { decisionHandler(.allow);return }
            if let u=action.request.url,["https","tonkeeper","tc"].contains(u.scheme ?? ""),action.navigationType == .linkActivated { UIApplication.shared.open(u) }
            decisionHandler(.cancel)
        }
        func webView(_ view:WKWebView,requestMediaCapturePermissionFor origin:WKSecurityOrigin,initiatedByFrame frame:WKFrameInfo,type:WKMediaCaptureType,decisionHandler:@escaping(WKPermissionDecision)->Void){decisionHandler(frame.isMainFrame&&internalURL(frame.request.url) ? .prompt:.deny)}
        func userContentController(_ ctl:WKUserContentController,didReceive message:WKScriptMessage){
            guard message.frameInfo.isMainFrame, internalURL(message.frameInfo.request.url),internalURL(message.webView?.url),let text=message.body as? String,let bytes=text.data(using:.utf8),let a=(try? JSONSerialization.jsonObject(with:bytes)) as? [String:Any],let id=a["id"] as? String else{return}
            var r:[String:Any]=["id":id]
            do{switch a["action"] as? String {
                case "file-open":
                    guard output==nil,exportFile==nil,let name=a["name"] as? String,!name.isEmpty else{throw LocalError.input}
                    let dir=FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString,isDirectory:true)
                    try FileManager.default.createDirectory(at:dir,withIntermediateDirectories:true)
                    let leaf=URL(fileURLWithPath:name).lastPathComponent
                    guard leaf != ".", leaf != "..", !leaf.isEmpty, leaf.utf8.count<=255 else{throw LocalError.input}
                    let url=dir.appendingPathComponent(leaf)
                    guard FileManager.default.createFile(atPath:url.path,contents:nil) else{throw LocalError.storage}
                    output=try FileHandle(forWritingTo:url);outputFile=url;fileId=UUID().uuidString;r["handle"]=fileId
                case "file-write":
                    guard let f=output,a["handle"] as? String==fileId,let s=a["data"] as? String,s.count<1400000,let b=Data(base64Encoded:s) else{throw LocalError.input}
                    try f.write(contentsOf:b)
                case "file-finish","file-cancel":
                    guard let f=output,let url=outputFile,a["handle"] as? String==fileId else{throw LocalError.input}
                    try f.close();output=nil;outputFile=nil;fileId=nil
                    if a["action"] as? String=="file-finish"{
                        guard var vc=message.webView?.window?.rootViewController else{try? FileManager.default.removeItem(at:url.deletingLastPathComponent());throw LocalError.storage}
                        while let next=vc.presentedViewController{vc=next}
                        exportFile=url;let picker=UIDocumentPickerViewController(forExporting:[url],asCopy:true);picker.delegate=self;vc.present(picker,animated:true)
                    }else{try FileManager.default.removeItem(at:url.deletingLastPathComponent())}
                case "install-proof":
                    guard let statement=a["statement"] as? String,statement.hasPrefix("ctrl-device-v1|"),statement.count<=240 else{throw LocalError.input}
                    let idfv=UIDevice.current.identifierForVendor?.uuidString ?? "vendor"
                    let raw=try secret("device/"+origin+"/"+idfv){P256.Signing.PrivateKey().rawRepresentation}
                    let key=try P256.Signing.PrivateKey(rawRepresentation:raw)
                    r["publicKey"]=key.publicKey.derRepresentation.base64EncodedString()
                    r["signature"]=try key.signature(for:Data(statement.utf8)).rawRepresentation.base64EncodedString()
                case "vault":
                    guard let scope=a["scope"] as? String else{throw LocalError.input}
                    if vault==nil{vault=try Vault()};r["value"]=try vault!.run(origin+"|"+scope,a)
                default:throw LocalError.input
            }}catch{r["error"]="Не удалось открыть ключ устройства."}
            guard let encoded=try? JSONSerialization.data(withJSONObject:r),let json=String(data:encoded,encoding:.utf8) else{return}
            message.webView?.callAsyncJavaScript("window.CtrlMobile?.receive(raw)",arguments:["raw":json],in:nil,in:.page,completionHandler:nil)
        }
    }
}

