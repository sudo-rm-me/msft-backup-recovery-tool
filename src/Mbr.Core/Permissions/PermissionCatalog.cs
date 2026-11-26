using Mbr.Core.Abstractions;

namespace Mbr.Core.Permissions;

public record RequiredPermission(string Workload, string Api, string Name, AppRole App, string Reason);

/// <summary>
/// The application permissions each workload needs. Used by the permission check, the onboarding wizard
/// and the Terraform app registrations (deploy/terraform/permissions.json, kept identical by a unit test).
/// </summary>
public static class PermissionCatalog
{
    public const string Graph = "Microsoft Graph";
    public const string Exchange = "Office 365 Exchange Online";

    public static readonly IReadOnlyList<RequiredPermission> All = new List<RequiredPermission>
    {
        // ---- Backup (read-only) app ----
        new("entra", Graph, "Directory.Read.All", AppRole.Backup, "Users, groups, roles, directory settings"),
        new("entra", Graph, "Policy.Read.All", AppRole.Backup, "Conditional Access, authentication and authorization policies"),
        new("entra", Graph, "Application.Read.All", AppRole.Backup, "App registrations and service principals"),
        new("entra", Graph, "RoleManagement.Read.Directory", AppRole.Backup, "Directory role assignments and PIM"),
        new("entra", Graph, "GroupMember.Read.All", AppRole.Backup, "Group memberships"),
        new("intune", Graph, "DeviceManagementConfiguration.Read.All", AppRole.Backup, "Configuration, compliance and settings catalog policies"),
        new("intune", Graph, "DeviceManagementApps.Read.All", AppRole.Backup, "App protection/configuration policies and apps"),
        new("intune", Graph, "DeviceManagementServiceConfig.Read.All", AppRole.Backup, "Enrollment and service configuration"),
        new("intune", Graph, "DeviceManagementRBAC.Read.All", AppRole.Backup, "Scope tags and Intune roles"),
        new("intune", Graph, "DeviceManagementScripts.Read.All", AppRole.Backup, "Scripts and remediations"),
        new("files", Graph, "Files.Read.All", AppRole.Backup, "OneDrive and SharePoint file content"),
        new("files", Graph, "Sites.Read.All", AppRole.Backup, "SharePoint sites and libraries"),
        new("sharepoint", Graph, "SharePointTenantSettings.Read.All", AppRole.Backup, "SharePoint tenant settings"),
        new("mail", Graph, "Mail.Read", AppRole.Backup, "Mailbox content"),
        new("mail", Graph, "MailboxSettings.Read", AppRole.Backup, "Mailbox settings"),
        new("mail", Graph, "Calendars.Read", AppRole.Backup, "Calendars"),
        new("mail", Graph, "Contacts.Read", AppRole.Backup, "Contacts"),
        new("mail", Graph, "User.Read.All", AppRole.Backup, "Enumerate mailboxes"),
        new("teams", Graph, "TeamSettings.Read.All", AppRole.Backup, "Team settings"),
        new("teams", Graph, "Channel.ReadBasic.All", AppRole.Backup, "Channels"),
        new("teams", Graph, "Team.ReadBasic.All", AppRole.Backup, "List teams"),
        new("teams", Graph, "TeamworkAppSettings.Read.All", AppRole.Backup, "Teams app settings"),
        new("purview", Graph, "RecordsManagement.Read.All", AppRole.Backup, "Retention labels"),
        new("m365backup", Graph, "BackupRestore-Configuration.Read.All", AppRole.Backup, "Microsoft 365 Backup policies and protection units"),
        new("m365backup", Graph, "BackupRestore-Monitor.Read.All", AppRole.Backup, "Microsoft 365 Backup restore sessions"),
        new("exchange", Exchange, "Exchange.ManageAsApp", AppRole.Backup, "Exchange Online and Security & Compliance PowerShell (also needs a directory role)"),

        // ---- Restore (write) app ----
        new("entra", Graph, "Directory.ReadWrite.All", AppRole.Restore, "Restore deleted objects, recreate users and groups"),
        new("entra", Graph, "Policy.ReadWrite.ConditionalAccess", AppRole.Restore, "Restore Conditional Access policies and named locations"),
        new("entra", Graph, "Policy.Read.All", AppRole.Restore, "Read policies while planning"),
        new("entra", Graph, "Application.ReadWrite.All", AppRole.Restore, "Restore app registrations"),
        new("entra", Graph, "RoleManagement.ReadWrite.Directory", AppRole.Restore, "Restore role assignments"),
        new("entra", Graph, "Group.ReadWrite.All", AppRole.Restore, "Restore groups and memberships"),
        new("entra", Graph, "User.ReadWrite.All", AppRole.Restore, "Restore users"),
        new("intune", Graph, "DeviceManagementConfiguration.ReadWrite.All", AppRole.Restore, "Restore Intune policies"),
        new("intune", Graph, "DeviceManagementApps.ReadWrite.All", AppRole.Restore, "Restore app policies"),
        new("intune", Graph, "DeviceManagementServiceConfig.ReadWrite.All", AppRole.Restore, "Restore enrollment configuration"),
        new("intune", Graph, "DeviceManagementRBAC.ReadWrite.All", AppRole.Restore, "Restore scope tags"),
        new("intune", Graph, "DeviceManagementScripts.ReadWrite.All", AppRole.Restore, "Restore scripts"),
        new("files", Graph, "Files.ReadWrite.All", AppRole.Restore, "Restore files"),
        new("files", Graph, "Sites.ReadWrite.All", AppRole.Restore, "Restore files to SharePoint"),
        new("sharepoint", Graph, "SharePointTenantSettings.ReadWrite.All", AppRole.Restore, "Restore SharePoint tenant settings"),
        new("mail", Graph, "Mail.ReadWrite", AppRole.Restore, "Restore mail"),
        new("mail", Graph, "Calendars.ReadWrite", AppRole.Restore, "Restore calendar items"),
        new("mail", Graph, "Contacts.ReadWrite", AppRole.Restore, "Restore contacts"),
        new("mail", Graph, "User.Read.All", AppRole.Restore, "Resolve mailboxes"),
        new("teams", Graph, "TeamSettings.ReadWrite.All", AppRole.Restore, "Restore team settings"),
        new("teams", Graph, "Channel.Create", AppRole.Restore, "Restore channels"),
        new("teams", Graph, "Team.ReadBasic.All", AppRole.Restore, "Find teams while planning"),
        new("teams", Graph, "TeamworkAppSettings.ReadWrite.All", AppRole.Restore, "Restore Teams app settings"),
        new("purview", Graph, "RecordsManagement.ReadWrite.All", AppRole.Restore, "Restore retention labels"),
        new("m365backup", Graph, "BackupRestore-Restore.ReadWrite.All", AppRole.Restore, "Microsoft 365 Backup restore points and native restores"),
        new("exchange", Exchange, "Exchange.ManageAsApp", AppRole.Restore, "Exchange Online and Security & Compliance PowerShell"),
    };

    public static IEnumerable<RequiredPermission> For(AppRole app, string? api = null) =>
        All.Where(p => p.App == app && (api is null || p.Api == api));
}
