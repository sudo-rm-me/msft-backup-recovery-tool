using Mbr.Core.Abstractions;
using Mbr.Core.Jobs;
using Mbr.Core.Model;
using Mbr.Infrastructure.Api;
using Mbr.Infrastructure.Audit;
using Mbr.Infrastructure.Data;
using Mbr.Infrastructure.Hosting;
using Mbr.Infrastructure.Jobs;
using Mbr.Infrastructure.Notifications;
using Mbr.Infrastructure.Security;
using Mbr.Infrastructure.Settings;
using Mbr.Infrastructure.Storage;
using Mbr.Infrastructure.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mbr.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers everything except the background worker/scheduler (see <see cref="AddMbrWorker"/>).</summary>
    public static IServiceCollection AddMbrCore(this IServiceCollection services, IConfiguration config)
    {
        var opts = config.GetSection(MbrOptions.Section).Get<MbrOptions>() ?? new MbrOptions();
        opts.DataDirectory = MbrOptions.ResolveDataDirectory(opts.DataDirectory);
        AzureHosting.Validate(opts);
        Directory.CreateDirectory(opts.DataDirectory);
        services.Configure<MbrOptions>(config.GetSection(MbrOptions.Section));
        services.PostConfigure<MbrOptions>(o =>
        {
            o.DataDirectory = opts.DataDirectory;
            AzureHosting.MaterializeCertificates(o);
        });

        if (opts.Database.Provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var cs = opts.Database.ConnectionString ?? throw new InvalidOperationException("Mbr:Database:ConnectionString is required for Postgres.");
            if (opts.Database.EntraAuth)
            {
                // Azure Database for PostgreSQL with Microsoft Entra authentication: a token for the managed identity is the password.
                var builder = new Npgsql.NpgsqlDataSourceBuilder(cs);
                var credential = new Azure.Identity.DefaultAzureCredential();
                builder.UsePeriodicPasswordProvider(async (_, ct) =>
                        (await credential.GetTokenAsync(new Azure.Core.TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]), ct)).Token,
                    TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10));
                var dataSource = builder.Build();
                services.AddDbContext<MbrDbContext, PostgresMbrDbContext>(o => o.UseNpgsql(dataSource));
            }
            else
                services.AddDbContext<MbrDbContext, PostgresMbrDbContext>(o => o.UseNpgsql(cs));
        }
        else
            services.AddDbContext<MbrDbContext, SqliteMbrDbContext>(o =>
                o.UseSqlite(opts.Database.ConnectionString ?? $"Data Source={Path.Combine(opts.DataDirectory, "mbr.db")}"));

        var endpoints = config.GetSection("Mbr:Endpoints").Get<ApiEndpoints>() ?? new ApiEndpoints();
        services.AddSingleton(endpoints);
        services.AddHttpClient("mbr-api", c => c.Timeout = TimeSpan.FromMinutes(10));
        services.AddHttpClient("mbr-anon");
        services.AddHttpClient("mbr-notify");
        services.AddSingleton<ITokenProvider, AppTokenProvider>();
        services.AddSingleton<IApiClientFactory, ApiClientFactory>();

        services.AddSingleton<IKeyWrapper>(sp =>
            opts.Encryption.KeyWrapper.Equals("KeyVault", StringComparison.OrdinalIgnoreCase)
                ? new KeyVaultKeyWrapper(sp.GetRequiredService<IOptions<MbrOptions>>())
                : new LocalKeyWrapper(sp.GetRequiredService<IOptions<MbrOptions>>()));
        services.AddSingleton<TenantKeyService>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<IBlobStoreFactory, BlobStoreFactory>();
        services.AddSingleton<StorageTargetResolver>();
        services.AddSingleton<ITenantContentStore, TenantContentStore>();

        services.AddScoped<JobQueue>();
        services.AddScoped<AuditLog>();
        services.AddScoped<TenantOnboardingService>();
        services.AddSingleton<LeaseService>();
        services.AddSingleton<INotifier, Notifier>();
        services.AddSingleton<RuntimeSettings>();
        services.AddHostedService<RuntimeSettingsRefresher>();

        services.AddScoped<IJobHandler, DiagnosticsJob>();
        services.AddScoped<IJobHandler, PermissionCheckJob>();
        return services;
    }

    public static IServiceCollection AddMbrWorker(this IServiceCollection services)
    {
        services.AddHostedService<JobWorker>();
        services.AddSingleton<SchedulerService>();
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
        services.AddMbrFeatureWorkers();
        return services;
    }

    /// <summary>Name of the storage target that mirrors Mbr:Storage:DefaultS3 (the deployment's bucket).</summary>
    public const string DeploymentStorageTargetName = "S3 (deployment)";

    /// <summary>Remembers the DefaultS3 settings last applied to the deployment's storage target.</summary>
    private const string AppliedDefaultS3Key = "_deployment:Mbr:Storage:DefaultS3";

    /// <summary>
    /// Applies pending migrations, loads the settings changed in the app, then makes sure there's a storage target: the
    /// deployment's S3 bucket (Mbr:Storage:DefaultS3), or on a fresh development install a local folder.
    /// The deployment target follows configuration on every start (e.g. the IAM role ARN added on the second Terraform
    /// apply) until someone edits it in the app; from then on the app's version is kept.
    /// </summary>
    public static async Task InitializeMbrDatabaseAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MbrDbContext>();
        await db.Database.MigrateAsync();
        await services.GetRequiredService<RuntimeSettings>().ReloadAsync();
        var s3 = scope.ServiceProvider.GetRequiredService<IOptions<MbrOptions>>().Value.Storage.DefaultS3;
        if (!string.IsNullOrWhiteSpace(s3.Bucket))
        {
            var json = StorageSettings.Serialize(s3);
            var target = await db.StorageTargets.FirstOrDefaultAsync(t => t.Name == DeploymentStorageTargetName);
            var applied = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == AppliedDefaultS3Key);
            if (target is null)
                db.StorageTargets.Add(new StorageTarget
                {
                    Name = DeploymentStorageTargetName, Kind = StorageKind.S3, SettingsJson = json,
                    IsDefault = !await db.StorageTargets.AnyAsync(t => t.IsDefault),
                });
            else if (target.SettingsJson != json)
            {
                if (applied is null || applied.Value == target.SettingsJson) target.SettingsJson = json;
                else
                    scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Mbr.Storage").LogInformation(
                        "The {Target} storage target was edited in the app; keeping that version instead of Mbr:Storage:DefaultS3.",
                        DeploymentStorageTargetName);
            }
            applied ??= db.AppSettings.Add(new AppSetting { Key = AppliedDefaultS3Key }).Entity;
            if (applied.Value != json)
            {
                applied.Value = json;
                applied.UpdatedAt = DateTimeOffset.UtcNow;
                applied.UpdatedBy = "deployment";
            }
            await db.SaveChangesAsync();
        }
        else if (!await db.StorageTargets.AnyAsync())
        {
            db.StorageTargets.Add(new StorageTarget
            {
                Name = "Local disk", Kind = StorageKind.Local, IsDefault = true,
                SettingsJson = StorageSettings.Serialize(new LocalStorageSettings { Path = "storage" }),
            });
            await db.SaveChangesAsync();
        }
    }
}
