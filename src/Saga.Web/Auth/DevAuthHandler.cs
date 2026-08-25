using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Saga.Web.Auth;

/// <summary>
/// Development-only scheme that signs every request in as the configured user
/// (Auth:DevUserEmail, default elv@mannaz.com) so the app runs without Entra ID.
/// </summary>
public class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevAuth";

    // Keeps the seeded DisplayName (SagaDbContext.OnModelCreating) from being overwritten by the
    // email on every sign-in: CurrentUserService falls back to the "name" claim, and without one
    // here it falls back further to the email address itself.
    private static readonly Dictionary<string, string> DevUserNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["elv@mannaz.com"] = "Emil Lindeløv Vestergaard",
        ["sda@mannaz.com"] = "Stefanie Baptiste",
        ["mkn@mannaz.com"] = "Mikkel Kjær Nielsen",
        ["jth@mannaz.com"] = "Pauline Thorsen Holm",
    };

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var email = configuration["Auth:DevUserEmail"] ?? "elv@mannaz.com";
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, email),
            new Claim(ClaimTypes.Email, email),
            new Claim("name", DevUserNames.GetValueOrDefault(email, email)),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
