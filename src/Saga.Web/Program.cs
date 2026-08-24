using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Saga.Core.Abstractions;
using Saga.Core.Domain;
using Saga.Infrastructure.Ai;
using Saga.Infrastructure.Data;
using Saga.Infrastructure.Extraction;
using Saga.Infrastructure.Services;
using Saga.Infrastructure.Storage;
using Saga.Web.Auth;
using Saga.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<SagaDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Saga")));

if (builder.Configuration.GetValue<bool>("Auth:DevAutoSignIn"))
{
    builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
}
else
{
    // Entra ID sign-in against the Mannaz tenant (config section "AzureAd": TenantId restricts
    // the issuer, so only Mannaz accounts can sign in). First sign-in upserts the local user.
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));
    builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();
}

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = options.DefaultPolicy;
});
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<ProposalService>();
builder.Services.AddScoped<CurrentUserService>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<ArtifactService>();
builder.Services.AddScoped<GenerationService>();
builder.Services.AddScoped<CondensationService>();
builder.Services.AddScoped<WorkingContextService>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddScoped<ProposalReviewService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<RequirementsExtractionService>();
builder.Services.AddScoped<ContentGenerationService>();
builder.Services.AddScoped<AiUsageService>();
builder.Services.AddScoped<Saga.Web.Components.Layout.AppHeaderState>();

// Bing-grounded research is wired up with the Foundry project in the deployment milestone.
builder.Services.AddSingleton<IWebResearchService, NullWebResearchService>();

builder.Services.AddSingleton<PricingService>();

// Every LLM call goes through the usage decorator — the fake included, so a dev session
// produces the same usage rows as production.
builder.Services.AddSingleton<IAiService>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    IAiService inner = StandInSelection.UseFakeAi(config)
        ? new FakeAiService()
        : new AzureOpenAiService(config);
    return new UsageTrackingAiService(inner,
        sp.GetRequiredService<IDbContextFactory<SagaDbContext>>(),
        sp.GetRequiredService<PricingService>(),
        sp.GetService<ILogger<UsageTrackingAiService>>());
});

// Azure Blob (Managed Identity) when configured; local disk otherwise (dev).
if (!string.IsNullOrEmpty(builder.Configuration["Storage:BlobServiceUri"]))
    builder.Services.AddSingleton<IFileStorage, AzureBlobFileStorage>();
else
    builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();

builder.Services.AddSingleton<IDocumentTextExtractor>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    // Offline stand-in accepts the same file types, so uploads work without Azure.
    IDocumentTextExtractor billed = StandInSelection.UseFakeExtractor(config)
        ? new FakeDocumentExtractor()
        : new ContentUnderstandingExtractor(config);

    var extractors = new List<IDocumentTextExtractor>
    {
        // PlainTextExtractor reads local files and costs nothing, so it stays unmetered.
        new PlainTextExtractor(),
        new UsageTrackingTextExtractor(billed, ContentUnderstandingExtractor.AnalyzerId,
            sp.GetRequiredService<IDbContextFactory<SagaDbContext>>(),
            sp.GetRequiredService<PricingService>(),
            sp.GetService<ILogger<UsageTrackingTextExtractor>>()),
    };
    return new CompositeTextExtractor(extractors);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
if (!app.Configuration.GetValue<bool>("Auth:DevAutoSignIn"))
    app.MapControllers(); // MicrosoftIdentity/Account sign-in and sign-out endpoints.

// File download endpoint: Blazor Server cannot stream files itself, so exports go over HTTP
// using the same auth cookie/scheme and the same per-proposal role checks.
app.MapGet("/proposals/{proposalId:guid}/export", async (
    Guid proposalId, string? format, HttpContext http,
    UserService users, ExportService export, CancellationToken ct) =>
{
    var email = http.User.FindFirstValue(ClaimTypes.Email)
        ?? http.User.FindFirstValue("preferred_username");
    var user = email is null ? null : await users.FindByEmailAsync(email, ct);
    if (user is null) return Results.Unauthorized();

    OutputFormat? requested = format?.ToLowerInvariant() switch
    {
        "pptx" or "powerpoint" => OutputFormat.PowerPoint,
        "docx" or "word" => OutputFormat.Word,
        _ => null,
    };

    try
    {
        var file = await export.ExportAsync(proposalId, user.Id,
            requested ?? OutputFormat.PowerPoint, ct);
        return Results.File(file.Bytes, file.ContentType, file.FileName);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Forbid();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
});

// Apply pending migrations automatically in development.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SagaDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

app.Run();
