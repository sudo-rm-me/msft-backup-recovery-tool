using System.Text.Json.Serialization;
using Mbr.Api.Auth;
using Mbr.Api.Endpoints;
using Mbr.Infrastructure;
using Mbr.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Identity.Web;

// When launched from another folder (e.g. `dotnet run --project src/Mbr.Api`), serve config and the UI from the build output.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json")) ? null : AppContext.BaseDirectory,
});

builder.Services.AddMbrCore(builder.Configuration);
builder.Services.AddMbrFeatures();
var demo = builder.Configuration.GetValue("Mbr:Demo:Enabled", false);
if (demo)
{
    if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Mbr:Demo:Enabled is only allowed in Development.");
    Mbr.Infrastructure.Demo.DemoMode.AddMbrDemo(builder.Services);
}
builder.Services.AddMbrWorker(); // every replica runs jobs; they share the queue through leases

builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection("Auth"));
var authSettings = builder.Configuration.GetSection("Auth").Get<AuthSettings>() ?? new AuthSettings();
if (authSettings.IsDevelopment)
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("Auth:Mode=Development is only allowed when ASPNETCORE_ENVIRONMENT=Development. Configure Auth:Mode=Entra.");
    builder.Services.AddAuthentication(DevelopmentAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthHandler>(DevelopmentAuthHandler.SchemeName, null);
}
else
{
    builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme).AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("Auth:Entra"));
}
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<OperatorContext>();
builder.Services.AddDataProtection();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();
await app.Services.InitializeMbrDatabaseAsync();
if (demo) await Mbr.Infrastructure.Demo.DemoMode.EnsureDemoTenantAsync(app.Services);

app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = ex switch
    {
        ForbiddenException => (403, "Forbidden"),
        KeyNotFoundException => (404, "Not found"),
        ArgumentException => (400, "Bad request"),
        InvalidOperationException => (409, "Conflict"),
        _ => (500, "Server error"),
    };
    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new { title, detail = status == 500 && !app.Environment.IsDevelopment() ? "An unexpected error occurred." : ex?.Message });
}));

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting(); // after static files, so the SPA fallback route doesn't swallow asset requests
app.UseAuthentication();
app.UseAuthorization();

app.MapMbrApi();
// Health probe for Azure Container Apps: the app is up and its database answers.
app.MapGet("/healthz", async (MbrDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Text("ok") : Results.StatusCode(503)).AllowAnonymous();
app.MapFallbackToFile("{*path:regex(^(?!api/).*$)}", "index.html");

app.Run();

public partial class Program;
