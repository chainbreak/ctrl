using System.Security.Claims;
using System.Net.Http.Json;
using Ctrl.Api.Domain;
using Ctrl.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
namespace Ctrl.Api.Controllers;

[ApiController,Route("api/admin/messenger"),Authorize]
public sealed class MessengerController(UserManager<AppUser> users):ControllerBase {
    static readonly HttpClient Client=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};
    [HttpPost]
    public async Task<IActionResult> Handle(ManagementRequest input,CancellationToken ct){
        var user=await users.GetUserAsync(User);
        if(user==null||!user.TwoFactorEnabled||!User.HasClaim("amr","mfa")||
           !(await users.IsInRoleAsync(user,Roles.Admin)||await users.IsInRoleAsync(user,Roles.Support)))return Forbid();
        if(input.Kind=="staff-handle" && !await users.IsInRoleAsync(user,Roles.Admin))return Forbid();
        var endpoint=Environment.GetEnvironmentVariable("CTRL_MESSENGER_ORIGIN")??"http://127.0.0.1:48734";
        var secret=Environment.GetEnvironmentVariable("CTRL_MANAGEMENT_KEY");
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||!(uri.Scheme=="https"||uri.Scheme=="http"&&uri.IsLoopback)||uri.UserInfo.Length>0||string.IsNullOrWhiteSpace(secret)||secret.Length<32)
            return StatusCode(503,new{message="Соединение с сервером Ctrl ещё не настроено."});
        using var req=new HttpRequestMessage(HttpMethod.Post,new Uri(uri,"/management"));
        req.Headers.Add("X-Ctrl-Management",secret);
        req.Content=JsonContent.Create(new{actor=user.Id,input.Kind,input.Target,input.Amount,input.Note,input.Operation});
        using var response=await Client.SendAsync(req,ct);
        return new ContentResult{StatusCode=(int)response.StatusCode,ContentType="application/json",Content=await response.Content.ReadAsStringAsync(ct)};
    }
}
public sealed record ManagementRequest(string Kind,string? Target,long Amount,string? Note,string? Operation);

