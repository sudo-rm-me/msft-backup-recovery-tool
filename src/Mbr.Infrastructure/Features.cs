using Mbr.Catalog;
using Mbr.Core.Jobs;
using Mbr.Infrastructure.Backup;
using Mbr.Infrastructure.Diff;
using Mbr.Infrastructure.Restore;
using Mbr.Infrastructure.Files;
using Mbr.Catalog.Restore;
using Mbr.Core.Restore;
using Microsoft.Extensions.DependencyInjection;

namespace Mbr.Infrastructure;

public static class FeatureRegistration
{
    /// <summary>Registers resource handlers, engines and feature job handlers (backup, diff, restore, data).</summary>
    public static IServiceCollection AddMbrFeatures(this IServiceCollection services)
    {
        // Catalog + configuration backup
        foreach (var h in Mbr.Catalog.Azure.AzureHandlers.All()) services.AddSingleton(typeof(Mbr.Core.Catalog.IResourceHandler), h);
        services.AddSingleton<ResourceRegistry>();
        services.AddSingleton<IPowerShellRunnerFactory, PowerShellRunnerFactory>();
        services.AddSingleton<TenantSessionFactory>();
        services.AddScoped<ConfigBackupService>();
        services.AddScoped<SnapshotReader>();
        services.AddScoped<RetentionService>();
        services.AddScoped<IReferencedHashSource, SnapshotHashSource>();
        services.AddScoped<IJobHandler, ConfigBackupJob>();
        services.AddScoped<IJobHandler, RetentionJob>();

        // Diff and drift
        services.AddScoped<DiffService>();
        services.AddScoped<IPostBackupHook, DriftAlertHook>();
        services.AddScoped<IJobHandler, DriftCheckJob>();

        // Restore
        foreach (var c in RestoreCustomizers.All().Concat(Mbr.Catalog.Azure.ArmPutCustomizer.All())) services.AddSingleton(typeof(IRestoreCustomizer), c);
        services.AddScoped<RestoreEngine>();
        services.AddScoped<IJobHandler, RestorePlanJob>();
        services.AddScoped<IJobHandler, RestoreApplyJob>();
        services.AddScoped<EntraRecycleBin>();
        services.AddScoped<IJobHandler, EntraUndeleteJob>();
        services.AddScoped<AzureConfig.AzureInventory>();
        services.AddScoped<Reports.ProtectionReport>();
        services.AddScoped<Tenants.PartnerCustomers>();
        services.AddScoped<M365Backup.M365BackupStorage>();

        // Data (OneDrive / SharePoint files; mailboxes in a later phase)
        services.AddScoped<IDataSourceDiscoverer, OneDriveDiscoverer>();
        services.AddScoped<IDataSourceDiscoverer, SharePointDiscoverer>();
        services.AddScoped<DataDiscoveryService>();
        services.AddScoped<IDataSourceDiscoverer, MailboxDiscoverer>();
        services.AddScoped<IDataSourceBackup, DriveBackup>();
        services.AddScoped<IDataSourceBackup, MailboxBackup>();
        services.AddScoped<IDataRestorer, DriveRestorer>();
        services.AddScoped<IDataRestorer, MailboxRestorer>();
        services.AddScoped<DataRestoreService>();
        services.AddScoped<IJobHandler, DataRestoreJob>();
        services.AddScoped<DataBackupService>();
        services.AddScoped<DataReader>();
        services.AddScoped<IReferencedHashSource, DataHashSource>();
        services.AddScoped<IRetentionParticipant, DataRetention>();
        services.AddScoped<IJobHandler, DataDiscoveryJob>();
        services.AddScoped<IJobHandler, DataBackupJob>();
        return services;
    }

    /// <summary>Background services that belong to feature areas (run only where the worker runs).</summary>
    public static IServiceCollection AddMbrFeatureWorkers(this IServiceCollection services)
    {
        services.AddHostedService<MaintenanceScheduler>();
        return services;
    }
}
