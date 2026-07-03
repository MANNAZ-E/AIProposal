using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Saga.Core.Abstractions;
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
    // Entra ID (Microsoft.Identity.Web) is wired up in the deployment milestone.
    throw new InvalidOperationException(
        "Entra ID sign-in is not configured yet. Set Auth:DevAutoSignIn=true for development.");
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
builder.Services.AddScoped<RequirementsExtractionService>();
builder.Services.AddScoped<ContentGenerationService>();

// Bing-grounded research is wired up with the Foundry project in the deployment milestone.
builder.Services.AddSingleton<IWebResearchService, NullWebResearchService>();

builder.Services.AddSingleton<IAiService>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return string.IsNullOrEmpty(config["AzureOpenAI:Endpoint"])
        ? new FakeAiService()
        : new AzureOpenAiService(config);
});

// Azure Blob storage replaces this in production (deployment milestone).
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();

builder.Services.AddSingleton<IDocumentTextExtractor>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var extractors = new List<IDocumentTextExtractor> { new PlainTextExtractor() };
    if (!string.IsNullOrEmpty(config["DocumentIntelligence:Endpoint"]))
        extractors.Add(new DocumentIntelligenceExtractor(config));
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

// Apply pending migrations automatically in development.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SagaDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

app.Run();
