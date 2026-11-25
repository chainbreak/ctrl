using System.ComponentModel.DataAnnotations;
using System.Text;
using Ctrl.Api.Domain;
using Ctrl.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ctrl.Api.Security;

public sealed class AdminBootstrapCommand
{
    private AdminBootstrapCommand(string operation, string email)
    {
        Operation = operation;
        Email = email;
    }

    public string Operation { get; }

    public string Email { get; }

    public static AdminBootstrapCommand? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("bootstrap-admin" or "grant-admin" or "grant-support" or "bootstrap-support"))
        {
            return null;
        }

        if (args.Length != 2)
        {
            throw new InvalidOperationException(
                "Supported console commands: bootstrap-admin <email>, grant-admin <email>.");
        }

        var email = args[1].Trim();
        if (email.Length > 256 || !new EmailAddressAttribute().IsValid(email))
        {
            throw new InvalidOperationException("Provide a valid administrator e-mail address.");
        }

        return new AdminBootstrapCommand(args[0], email);
    }

    public async Task ExecuteAsync(IServiceProvider services, IWebHostEnvironment environment)
    {
        if (!Environment.UserInteractive || Console.IsInputRedirected)
        {
            throw new InvalidOperationException("Administrator bootstrap requires an interactive server console.");
        }

        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CtrlDbContext>();
        if (environment.IsDevelopment())
        {
            await database.Database.EnsureCreatedAsync();
        }
        else
        {
            await database.Database.MigrateAsync();
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var role=Operation.EndsWith("support",StringComparison.Ordinal)?Roles.Support:Roles.Admin;
        if (!await roles.RoleExistsAsync(role))
        {
            var roleResult = await roles.CreateAsync(new IdentityRole(role));
            EnsureSucceeded(roleResult, "Could not create the administrator role.");
        }

        if (Operation=="bootstrap-support" || Operation=="grant-support"){
            var employee=await users.FindByEmailAsync(Email);
            if(Operation=="bootstrap-support"){
                if(employee!=null)throw new InvalidOperationException("Account already exists. Use grant-support.");
                employee=new AppUser{UserName=Email,Email=Email,EmailConfirmed=true};
                EnsureSucceeded(await users.CreateAsync(employee,ReadHidden("Set the support account password: ")),"Could not create support account.");
            }
            if(employee==null)throw new InvalidOperationException("Support account not found.");
            EnsureSucceeded(await users.AddToRoleAsync(employee,Roles.Support),"Could not grant support role.");
            database.AdminAuditEntries.Add(new AdminAuditEntry{ActorUserId=employee.Id,TargetUserId=employee.Id,Action="support.grant",Reason="Interactive server console"});await database.SaveChangesAsync();return;
        }
        if (Operation == "bootstrap-admin")
        {
            await CreateFirstAdminAsync(database, users);
            return;
        }

        await GrantAdminRoleAsync(database, users);
    }

    private async Task CreateFirstAdminAsync(
        CtrlDbContext database,
        UserManager<AppUser> users)
    {
        if (await users.Users.AnyAsync())
        {
            throw new InvalidOperationException("Bootstrap is only allowed before the first account exists.");
        }

        var password = ReadHidden("Set the first administrator password: ");
        var confirmation = ReadHidden("Re-enter the password: ");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Passwords did not match.");
        }

        var user = new AppUser
        {
            UserName = Email,
            Email = Email,
            EmailConfirmed = true
        };
        EnsureSucceeded(await users.CreateAsync(user, password), "Could not create the initial administrator.");
        EnsureSucceeded(
            await users.AddToRoleAsync(user, Roles.Admin),
            "Could not grant the administrator role.");

        database.AdminAuditEntries.Add(new AdminAuditEntry
        {
            ActorUserId = user.Id,
            TargetUserId = user.Id,
            Action = "admin.bootstrap",
            Reason = "Initial administrator created from the interactive server console."
        });
        await database.SaveChangesAsync();

        Console.WriteLine($"Initial administrator created for {Email}.");
        Console.WriteLine("Sign in and enroll an authenticator before using admin APIs.");
    }

    private async Task GrantAdminRoleAsync(CtrlDbContext database, UserManager<AppUser> users)
    {
        var user = await users.FindByEmailAsync(Email);
        if (user is null || !user.EmailConfirmed)
        {
            throw new InvalidOperationException("The confirmed account was not found.");
        }

        Console.Write($"Type {Email} to confirm granting administrator access: ");
        var confirmation = Console.ReadLine();
        if (!string.Equals(confirmation, Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Confirmation did not match; no role was granted.");
        }

        if (await users.IsInRoleAsync(user, Roles.Admin))
        {
            Console.WriteLine("The account already has the administrator role.");
            return;
        }

        EnsureSucceeded(await users.AddToRoleAsync(user, Roles.Admin), "Could not grant the administrator role.");
        database.AdminAuditEntries.Add(new AdminAuditEntry
        {
            ActorUserId = "system:console",
            TargetUserId = user.Id,
            Action = "admin.role.grant",
            Reason = "Administrator role granted from the interactive server console."
        });
        await database.SaveChangesAsync();
        Console.WriteLine($"Administrator role granted to {Email}. The account must enroll MFA before using admin APIs.");
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return password.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }
    }

    private static void EnsureSucceeded(IdentityResult result, string message)
    {
        if (result.Succeeded)
        {
            return;
        }

        var errors = string.Join(", ", result.Errors.Select(error => error.Code));
        throw new InvalidOperationException($"{message} Identity errors: {errors}");
    }
}
