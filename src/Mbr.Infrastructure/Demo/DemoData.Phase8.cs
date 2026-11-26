using System.Text.Json.Nodes;

namespace Mbr.Infrastructure.Demo;

public static partial class DemoData
{
    static partial void SeedPhase8Plus(InMemoryGraph g, InMemoryGraph arm)
    {
        DemoWorkloads.SeedGraph(g);
        SeedGdap(g);
        SeedPhase9Plus(g, arm);
    }

    static partial void SeedPhase9Plus(InMemoryGraph g, InMemoryGraph arm);

    /// <summary>GDAP relationships of the partner (the demo tenant) with two customers.</summary>
    public static void SeedGdap(InMemoryGraph g)
    {
        var customers = new[]
        {
            ("7c9e6679-7425-40de-944b-e07fc1f90ae7", "Northwind Traders", "northwindtraders.onmicrosoft.com"),
            ("b3f1e2d4-1111-4a2b-8c3d-000000000002", "Tailspin Toys", "tailspintoys.onmicrosoft.com"),
        };
        string[] baseRoles = ["f2ef992c-3afb-46b9-b7cf-a126ee74c451", "29232cdf-9323-42fd-ade2-1d097af3e4de", "3a2c62db-5318-420d-8d74-23affee5d9d5"];
        foreach (var (i, (tid, name, _)) in customers.Index())
            g.Add("tenantRelationships/delegatedAdminRelationships", new JsonObject
            {
                ["id"] = $"gdap-{i + 1}", ["displayName"] = $"MSP-{name.Replace(" ", "")}-Backup", ["status"] = "active",
                ["endDateTime"] = DateTimeOffset.UtcNow.AddDays(i == 0 ? 600 : 20).ToString("o"), ["autoExtendDuration"] = i == 0 ? "P180D" : "PT0S",
                ["customer"] = new JsonObject { ["tenantId"] = tid, ["displayName"] = name },
                ["accessDetails"] = new JsonObject
                {
                    ["unifiedRoles"] = new JsonArray((i == 0 ? baseRoles.Append("e8611ab8-c189-46e8-94e1-60213ab1f814") : baseRoles)
                        .Select(r => (JsonNode)new JsonObject { ["roleDefinitionId"] = r }).ToArray()),
                },
            });
        g.On(HttpMethod.Get, "/v1.0/tenantRelationships/findTenantInformationByTenantId", (req, _) =>
        {
            var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath);
            var c = customers.FirstOrDefault(x => path.Contains(x.Item1));
            return c.Item1 is null
                ? InMemoryGraph.Error(System.Net.HttpStatusCode.NotFound, "Request_ResourceNotFound", "Unknown tenant")
                : InMemoryGraph.Json(new JsonObject { ["tenantId"] = c.Item1, ["displayName"] = c.Item2, ["defaultDomainName"] = c.Item3 });
        });
    }

    public static void SeedPowerShell(InMemoryPowerShell ps) => DemoWorkloads.SeedPowerShell(ps, "contoso-demo.onmicrosoft.com");
}

/// <summary>Teams, SharePoint, Exchange, Defender and Purview configuration for the demo tenant and tests.</summary>
public static class DemoWorkloads
{
    public static void SeedGraph(InMemoryGraph g)
    {
        foreach (var group in g.Collection("groups").Take(2).ToList())
        {
            var id = group["id"]!.ToString();
            g.Add("teams", new JsonObject
            {
                ["id"] = id, ["displayName"] = group["displayName"]?.ToString(), ["description"] = $"{group["displayName"]} team",
                ["isArchived"] = false, ["webUrl"] = $"https://teams.microsoft.com/l/team/{id}",
                ["memberSettings"] = new JsonObject { ["allowCreateUpdateChannels"] = true, ["allowDeleteChannels"] = false, ["allowAddRemoveApps"] = true },
                ["guestSettings"] = new JsonObject { ["allowCreateUpdateChannels"] = false, ["allowDeleteChannels"] = false },
                ["messagingSettings"] = new JsonObject { ["allowUserEditMessages"] = true, ["allowUserDeleteMessages"] = true, ["allowChannelMentions"] = true },
                ["funSettings"] = new JsonObject { ["allowGiphy"] = true, ["giphyContentRating"] = "moderate" },
            });
            g.Add($"teams/{id}/channels", new JsonObject { ["displayName"] = "General", ["membershipType"] = "standard", ["description"] = "" });
            g.Add($"teams/{id}/channels", new JsonObject { ["displayName"] = "Announcements", ["membershipType"] = "standard", ["description"] = "News and updates" });
            g.Add($"teams/{id}/channels", new JsonObject { ["displayName"] = "Projects", ["membershipType"] = "standard", ["description"] = "Ongoing projects" });
        }
        DemoData.Singleton(g, "/beta/teamwork/teamsAppSettings", new JsonObject
        {
            ["id"] = "teamsAppSettings", ["isChatResourceSpecificConsentEnabled"] = false, ["allowUserRequestsForAppAccess"] = true,
        });
        DemoData.Singleton(g, "/v1.0/admin/sharepoint/settings", new JsonObject
        {
            ["sharingCapability"] = "externalUserSharingOnly", ["isResharingByExternalUsersEnabled"] = false, ["isLoopEnabled"] = true,
            ["personalSiteDefaultStorageLimitInMB"] = 1048576, ["deletedUserPersonalSiteRetentionPeriodInDays"] = 30,
            ["sharingDomainRestrictionMode"] = "none", ["isSiteCreationEnabled"] = true,
            ["availableManagedPathsForSiteCreation"] = new JsonArray("/sites/", "/teams/"),
        });
    }

    public static void SeedPowerShell(InMemoryPowerShell ps, string domain)
    {
        // ---- Exchange Online
        ps.Add("exo", "OrganizationConfig", new JsonObject
        {
            ["Name"] = domain.Split('.')[0], ["AuditDisabled"] = false, ["OAuth2ClientProfileEnabled"] = true, ["SmtpActionableMessagesEnabled"] = true,
            ["FocusedInboxOn"] = true, ["DefaultAuthenticationPolicy"] = "Block basic auth", ["MailTipsExternalRecipientsTipsEnabled"] = true,
        });
        ps.Add("exo", "TransportConfig", new JsonObject { ["Name"] = "Transport Settings", ["SmtpClientAuthenticationDisabled"] = true, ["MaxSendSize"] = "35 MB (36,700,160 bytes)" });
        ps.Add("exo", "AdminAuditLogConfig", new JsonObject { ["Name"] = "Admin Audit Log Settings", ["UnifiedAuditLogIngestionEnabled"] = true });
        ps.Add("exo", "AcceptedDomain", new JsonObject { ["Name"] = domain, ["DomainName"] = domain, ["DomainType"] = "Authoritative", ["Default"] = true });
        ps.Add("exo", "RemoteDomain", new JsonObject { ["Name"] = "Default", ["DomainName"] = "*", ["IsDefault"] = true, ["AutoForwardEnabled"] = false, ["AutoReplyEnabled"] = true });
        ps.Add("exo", "RemoteDomain", new JsonObject { ["Name"] = "Fabrikam", ["DomainName"] = "fabrikam.com", ["IsDefault"] = false, ["AutoForwardEnabled"] = true, ["TNEFEnabled"] = false });
        ps.Add("exo", "TransportRule", new JsonObject
        {
            ["Name"] = "External sender warning", ["Priority"] = 0, ["State"] = "Enabled", ["Mode"] = "Enforce",
            ["FromScope"] = "NotInOrganization", ["SentToScope"] = "InOrganization",
            ["PrependSubject"] = "[EXTERNAL] ", ["ApplyHtmlDisclaimerLocation"] = "Prepend",
            ["ApplyHtmlDisclaimerText"] = "<p style=\"color:#b00\">This message came from outside Contoso.</p>", ["ApplyHtmlDisclaimerFallbackAction"] = "Wrap",
        });
        ps.Add("exo", "TransportRule", new JsonObject
        {
            ["Name"] = "Block executable attachments", ["Priority"] = 1, ["State"] = "Enabled", ["Mode"] = "Enforce",
            ["AttachmentExtensionMatchesWords"] = new JsonArray("exe", "js", "vbs", "scr"), ["RejectMessageReasonText"] = "Executable attachments aren't allowed.",
            ["RejectMessageEnhancedStatusCode"] = "5.7.1",
        });
        ps.Add("exo", "OutboundConnector", new JsonObject
        {
            ["Name"] = "To Fabrikam (TLS)", ["Enabled"] = true, ["ConnectorType"] = "Partner", ["RecipientDomains"] = new JsonArray("fabrikam.com"),
            ["TlsSettings"] = "DomainValidation", ["TlsDomain"] = "mail.fabrikam.com", ["UseMXRecord"] = true,
        });
        ps.Add("exo", "SharingPolicy", new JsonObject { ["Name"] = "Default Sharing Policy", ["Default"] = true, ["Enabled"] = true, ["Domains"] = new JsonArray("*:CalendarSharingFreeBusySimple") });
        ps.Add("exo", "OwaMailboxPolicy", new JsonObject { ["Name"] = "OwaMailboxPolicy-Default", ["IsDefault"] = true, ["AdditionalStorageProvidersAvailable"] = false, ["WacOMEXEnabled"] = false });
        ps.Add("exo", "MobileDeviceMailboxPolicy", new JsonObject { ["Name"] = "Default", ["IsDefault"] = true, ["PasswordEnabled"] = true, ["MinPasswordLength"] = 6, ["AllowSimplePassword"] = false });
        ps.Add("exo", "AuthenticationPolicy", new JsonObject { ["Name"] = "Block basic auth", ["AllowBasicAuthSmtp"] = false, ["AllowBasicAuthImap"] = false, ["AllowBasicAuthPop"] = false });
        ps.Add("exo", "DkimSigningConfig", new JsonObject { ["Name"] = domain, ["Domain"] = domain, ["Enabled"] = true, ["Selector1PublicKey"] = "v=DKIM1; k=rsa; p=MIGf…", ["Status"] = "Valid" });

        // ---- Defender for Office 365
        ps.Add("exo", "AtpPolicyForO365", new JsonObject { ["Name"] = "Default", ["EnableATPForSPOTeamsODB"] = true, ["EnableSafeDocs"] = true, ["AllowSafeDocsOpen"] = false });
        ps.Add("exo", "AntiPhishPolicy", new JsonObject { ["Name"] = "Office365 AntiPhish Default", ["IsDefault"] = true, ["Enabled"] = true, ["PhishThresholdLevel"] = 1, ["EnableMailboxIntelligence"] = true });
        ps.Add("exo", "AntiPhishPolicy", new JsonObject
        {
            ["Name"] = "Executives", ["IsDefault"] = false, ["Enabled"] = true, ["PhishThresholdLevel"] = 3, ["EnableTargetedUserProtection"] = true,
            ["TargetedUsersToProtect"] = new JsonArray("Megan Bowen;megan@contoso-demo.com"), ["TargetedUserProtectionAction"] = "Quarantine",
            ["EnableMailboxIntelligenceProtection"] = true, ["MailboxIntelligenceProtectionAction"] = "MoveToJmf",
        });
        ps.Add("exo", "AntiPhishRule", new JsonObject
        {
            ["Name"] = "Executives", ["AntiPhishPolicy"] = "Executives", ["Priority"] = 0, ["State"] = "Enabled", ["SentTo"] = new JsonArray("megan@contoso-demo.com"),
        });
        ps.Add("exo", "HostedContentFilterPolicy", new JsonObject
        {
            ["Name"] = "Default", ["IsDefault"] = true, ["BulkThreshold"] = 7, ["SpamAction"] = "MoveToJmf", ["HighConfidenceSpamAction"] = "Quarantine",
            ["PhishSpamAction"] = "Quarantine", ["HighConfidencePhishAction"] = "Quarantine", ["QuarantineRetentionPeriod"] = 30,
        });
        ps.Add("exo", "HostedContentFilterPolicy", new JsonObject
        {
            ["Name"] = "Strict inbound", ["IsDefault"] = false, ["BulkThreshold"] = 5, ["SpamAction"] = "Quarantine", ["HighConfidenceSpamAction"] = "Quarantine",
            ["PhishSpamAction"] = "Quarantine", ["HighConfidencePhishAction"] = "Quarantine", ["MarkAsSpamBulkMail"] = "On",
        });
        ps.Add("exo", "HostedContentFilterRule", new JsonObject
        {
            ["Name"] = "Strict inbound", ["HostedContentFilterPolicy"] = "Strict inbound", ["Priority"] = 0, ["State"] = "Enabled",
            ["RecipientDomainIs"] = new JsonArray(domain),
        });
        ps.Add("exo", "HostedOutboundSpamFilterPolicy", new JsonObject { ["Name"] = "Default", ["IsDefault"] = true, ["AutoForwardingMode"] = "Off", ["RecipientLimitPerDay"] = 1000 });
        ps.Add("exo", "HostedConnectionFilterPolicy", new JsonObject { ["Name"] = "Default", ["IsDefault"] = true, ["EnableSafeList"] = false, ["IPBlockList"] = new JsonArray("203.0.113.0/24") });
        ps.Add("exo", "MalwareFilterPolicy", new JsonObject
        {
            ["Name"] = "Default", ["IsDefault"] = true, ["EnableFileFilter"] = true, ["FileTypeAction"] = "Reject", ["ZapEnabled"] = true,
            ["FileTypes"] = new JsonArray("ace", "apk", "app", "cab", "dmg", "exe", "iso", "jar", "msi", "scr", "vbs"),
        });
        ps.Add("exo", "SafeLinksPolicy", new JsonObject
        {
            ["Name"] = "Contoso Safe Links", ["IsBuiltInProtection"] = false, ["EnableSafeLinksForEmail"] = true, ["EnableSafeLinksForTeams"] = true,
            ["EnableSafeLinksForOffice"] = true, ["TrackClicks"] = true, ["AllowClickThrough"] = false, ["ScanUrls"] = true, ["DeliverMessageAfterScan"] = true,
        });
        ps.Add("exo", "SafeLinksRule", new JsonObject
        {
            ["Name"] = "Contoso Safe Links", ["SafeLinksPolicy"] = "Contoso Safe Links", ["Priority"] = 0, ["State"] = "Enabled", ["RecipientDomainIs"] = new JsonArray(domain),
        });
        ps.Add("exo", "SafeAttachmentPolicy", new JsonObject { ["Name"] = "Contoso Safe Attachments", ["IsBuiltInProtection"] = false, ["Enable"] = true, ["Action"] = "Block", ["QuarantineTag"] = "AdminOnlyAccessPolicy" });
        ps.Add("exo", "SafeAttachmentRule", new JsonObject
        {
            ["Name"] = "Contoso Safe Attachments", ["SafeAttachmentPolicy"] = "Contoso Safe Attachments", ["Priority"] = 0, ["State"] = "Enabled", ["RecipientDomainIs"] = new JsonArray(domain),
        });
        ps.Add("exo", "QuarantinePolicy", new JsonObject { ["Name"] = "AdminOnlyAccessPolicy", ["EndUserQuarantinePermissionsValue"] = 0, ["ESNEnabled"] = false });

        // ---- Purview
        var confidential = ps.Add("ipps", "Label", new JsonObject
        {
            ["Name"] = "Confidential", ["DisplayName"] = "Confidential", ["Tooltip"] = "Business data that would cause harm if shared externally.",
            ["Comment"] = "Default for finance and HR", ["ContentType"] = "File, Email", ["Priority"] = 2,
        });
        ps.Add("ipps", "Label", new JsonObject { ["Name"] = "General", ["DisplayName"] = "General", ["Tooltip"] = "Business data not meant for the public.", ["ContentType"] = "File, Email", ["Priority"] = 1 });
        ps.Add("ipps", "LabelPolicy", new JsonObject
        {
            ["Name"] = "Default label policy", ["ScopedLabels"] = new JsonArray("General", "Confidential"), ["ExchangeLocation"] = new JsonArray(new JsonObject { ["Name"] = "All", ["DisplayName"] = "All" }),
            ["Settings"] = new JsonArray("[defaultlabelid, General]", "[mandatory, false]"),
        });
        ps.Add("ipps", "ComplianceTag", new JsonObject
        {
            ["Name"] = "Financial records - 7 years", ["RetentionAction"] = "KeepAndDelete", ["RetentionDuration"] = 2555, ["RetentionType"] = "CreationAgeInDays", ["IsRecordLabel"] = false,
        });
        ps.Add("ipps", "RetentionCompliancePolicy", new JsonObject
        {
            ["Name"] = "Retain mail 3 years", ["Enabled"] = true, ["Mode"] = "Enforce", ["ExchangeLocation"] = new JsonArray(new JsonObject { ["Name"] = "All", ["DisplayName"] = "All" }),
        });
        ps.Add("ipps", "RetentionComplianceRule", new JsonObject
        {
            ["Name"] = "Retain mail 3 years", ["Policy"] = "Retain mail 3 years", ["RetentionDuration"] = "1095", ["RetentionComplianceAction"] = "Keep", ["ExpirationDateOption"] = "ModificationAgeInDays",
        });
        ps.Add("ipps", "DlpCompliancePolicy", new JsonObject
        {
            ["Name"] = "U.S. Financial Data", ["Mode"] = "Enable", ["ExchangeLocation"] = new JsonArray(new JsonObject { ["Name"] = "All", ["DisplayName"] = "All" }),
            ["SharePointLocation"] = new JsonArray(new JsonObject { ["Name"] = "All", ["DisplayName"] = "All" }), ["Comment"] = "Credit card and bank account numbers",
        });
        ps.Add("ipps", "DlpComplianceRule", new JsonObject
        {
            ["Name"] = "Block external sharing of card numbers", ["ParentPolicyName"] = "U.S. Financial Data", ["BlockAccess"] = true, ["BlockAccessScope"] = "NotInOrganization",
            ["NotifyUser"] = new JsonArray("SiteAdmin", "LastModifier"), ["ReportSeverityLevel"] = "High", ["Disabled"] = false,
        });
        ps.Add("ipps", "ProtectionAlert", new JsonObject { ["Name"] = "Elevation of Exchange admin privilege", ["IsSystemRule"] = true, ["Severity"] = "Low", ["NotifyUser"] = new JsonArray("TenantAdmins") });
        ps.Add("ipps", "ProtectionAlert", new JsonObject { ["Name"] = "Mass download by a single user", ["IsSystemRule"] = false, ["Severity"] = "High", ["Threshold"] = 500, ["NotifyUser"] = new JsonArray("secops@contoso-demo.com") });
        _ = confidential;

        // ---- Teams policies
        foreach (var (noun, extra) in new (string, JsonObject)[]
                 {
                     ("CsTeamsMeetingPolicy", new JsonObject { ["AllowCloudRecording"] = true, ["AllowTranscription"] = true, ["AutoAdmittedUsers"] = "EveryoneInCompanyExcludingGuests", ["AllowAnonymousUsersToJoinMeeting"] = false }),
                     ("CsTeamsMessagingPolicy", new JsonObject { ["AllowUserEditMessage"] = true, ["AllowUserDeleteMessage"] = true, ["AllowGiphy"] = true, ["GiphyRatingType"] = "Moderate" }),
                     ("CsTeamsAppPermissionPolicy", new JsonObject { ["DefaultCatalogAppsType"] = "BlockedAppList", ["GlobalCatalogAppsType"] = "BlockedAppList", ["PrivateCatalogAppsType"] = "BlockedAppList" }),
                     ("CsTeamsAppSetupPolicy", new JsonObject { ["AllowUserPinning"] = true, ["AllowSideLoading"] = false }),
                     ("CsTeamsCallingPolicy", new JsonObject { ["AllowPrivateCalling"] = true, ["AllowVoicemail"] = "UserOverride" }),
                     ("CsTeamsChannelsPolicy", new JsonObject { ["AllowPrivateChannelCreation"] = true, ["AllowSharedChannelCreation"] = false }),
                 })
            ps.Add("teams", noun, new JsonObject { ["Identity"] = "Global" }.Merge(extra));
        ps.Add("teams", "CsTeamsMeetingPolicy", new JsonObject
        {
            ["Identity"] = "Tag:Executives", ["Description"] = "Executives: no recording", ["AllowCloudRecording"] = false, ["AllowTranscription"] = false,
            ["AutoAdmittedUsers"] = "InvitedUsers", ["AllowAnonymousUsersToJoinMeeting"] = false,
        });
        ps.Add("teams", "CsTeamsMessagingPolicy", new JsonObject { ["Identity"] = "Tag:Frontline", ["AllowUserEditMessage"] = false, ["AllowUserDeleteMessage"] = false, ["AllowGiphy"] = false });
        ps.Add("teams", "CsTenantFederationConfiguration", new JsonObject
        {
            ["Identity"] = "Global", ["AllowFederatedUsers"] = true, ["AllowTeamsConsumer"] = false, ["AllowPublicUsers"] = false,
            ["AllowedDomains"] = new JsonArray("fabrikam.com", "northwind.com"),
        });
        ps.Add("teams", "CsTeamsClientConfiguration", new JsonObject { ["Identity"] = "Global", ["AllowDropBox"] = false, ["AllowBox"] = false, ["AllowGoogleDrive"] = false, ["AllowEmailIntoChannel"] = true });
        ps.Add("teams", "CsTeamsMeetingConfiguration", new JsonObject { ["Identity"] = "Global", ["DisableAnonymousJoin"] = true, ["EnableQoS"] = true });
        ps.Add("teams", "CsTeamsGuestMeetingConfiguration", new JsonObject { ["Identity"] = "Global", ["AllowIPVideo"] = true, ["ScreenSharingMode"] = "EntireScreen" });
        ps.Add("teams", "CsTeamsGuestMessagingConfiguration", new JsonObject { ["Identity"] = "Global", ["AllowUserEditMessage"] = true, ["AllowGiphy"] = false });
    }

    private static JsonObject Merge(this JsonObject target, JsonObject extra)
    {
        foreach (var (k, v) in extra) target[k] = v?.DeepClone();
        return target;
    }
}
