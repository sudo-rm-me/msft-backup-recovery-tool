using System.Net;
using System.Text.Json.Nodes;

namespace Mbr.Infrastructure.Demo;

/// <summary>Simulated Microsoft 365 Backup (solutions/backupRestore): policies, protection units, restore points and sessions.</summary>
public static class DemoM365Backup
{
    private const string Root = "solutions/backupRestore";

    public static void Seed(InMemoryGraph g, IReadOnlyList<(string UserId, string Email)> mailboxes, IReadOnlyList<(string UserId, string Email)> drives,
        IReadOnlyList<(string SiteId, string Name)> sites)
    {
        var service = new JsonObject
        {
            ["id"] = "backupRestore", ["serviceStatus"] = new JsonObject { ["status"] = "enabled", ["disableReason"] = "none" },
        };
        // Exact-path route: a prefix route would shadow the child collections below.
        g.Routes.Insert(0, (r => r.Method == HttpMethod.Get && InMemoryGraph.Normalize(r.RequestUri!.AbsolutePath).Equals(Root, StringComparison.OrdinalIgnoreCase),
            (_, _) => InMemoryGraph.Json(service.DeepClone())));
        var retention = new JsonArray(new JsonObject { ["period"] = "P365D", ["interval"] = "PT10M" });
        void Policy(string id, string type, string name) => g.Add($"{Root}/protectionPolicies", new JsonObject
        {
            ["@odata.type"] = $"#microsoft.graph.{type}", ["id"] = id, ["displayName"] = name, ["status"] = "active", ["retentionSettings"] = retention.DeepClone(),
        });
        Policy("pol-exo", "exchangeProtectionPolicy", "Executive mailboxes");
        Policy("pol-odb", "oneDriveForBusinessProtectionPolicy", "Finance OneDrives");
        Policy("pol-spo", "sharePointProtectionPolicy", "Critical sites");
        foreach (var (uid, email) in mailboxes)
            g.Add($"{Root}/protectionUnits", new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.mailboxProtectionUnit", ["id"] = $"mbx-{uid}", ["policyId"] = "pol-exo", ["status"] = "protected",
                ["directoryObjectId"] = uid, ["email"] = email,
            });
        foreach (var (uid, email) in drives)
            g.Add($"{Root}/protectionUnits", new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.driveProtectionUnit", ["id"] = $"drv-{uid}", ["policyId"] = "pol-odb", ["status"] = "protected",
                ["directoryObjectId"] = uid, ["email"] = email,
            });
        foreach (var (sid, name) in sites)
            g.Add($"{Root}/protectionUnits", new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.siteProtectionUnit", ["id"] = $"site-{sid.Split(',').Last()}", ["policyId"] = "pol-spo", ["status"] = "protected",
                ["siteId"] = sid, ["siteName"] = name,
            });
        g.Collection($"{Root}/restoreSessions");

        g.On(HttpMethod.Post, $"/v1.0/{Root}", (req, body) =>
        {
            var path = InMemoryGraph.Normalize(req.RequestUri!.AbsolutePath)[(Root.Length + 1)..];
            var json = JsonNode.Parse(string.IsNullOrEmpty(body) ? "{}" : body)!.AsObject();
            if (path == "restorePoints/search")
            {
                var results = new JsonArray();
                foreach (var unit in (json["protectionUnitIds"] as JsonArray ?? []).Select(u => u!.ToString()))
                    for (var d = 0; d < 3; d++)
                    {
                        var at = DateTime.UtcNow.Date.AddDays(-d).AddHours(2);
                        results.Add(new JsonObject
                        {
                            ["artifactHitCount"] = 0,
                            ["restorePoint"] = new JsonObject
                            {
                                ["id"] = $"{unit}-rp{d}", ["protectionDateTime"] = at.ToString("o"), ["expirationDateTime"] = at.AddDays(365).ToString("o"),
                                ["tags"] = d == 0 ? "fastRestore" : "none",
                            },
                        });
                    }
                return InMemoryGraph.Json(new JsonObject { ["searchResponseId"] = Guid.NewGuid().ToString(), ["searchResults"] = results });
            }
            if (path is "exchangeRestoreSessions" or "oneDriveForBusinessRestoreSessions" or "sharePointRestoreSessions")
            {
                var type = path switch { "exchangeRestoreSessions" => "exchangeRestoreSession", "oneDriveForBusinessRestoreSessions" => "oneDriveForBusinessRestoreSession", _ => "sharePointRestoreSession" };
                var session = new JsonObject
                {
                    ["@odata.type"] = $"#microsoft.graph.{type}", ["id"] = Guid.NewGuid().ToString(), ["status"] = "draft",
                    ["createdDateTime"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["createdBy"] = new JsonObject { ["application"] = new JsonObject { ["displayName"] = "MBR restore" } },
                };
                foreach (var (k, v) in json) session[k] = v?.DeepClone();
                g.Add($"{Root}/restoreSessions", session);
                return InMemoryGraph.Json(session.DeepClone(), HttpStatusCode.Created);
            }
            if (path.StartsWith("restoreSessions/") && path.EndsWith("/activate"))
            {
                var id = path.Split('/')[1];
                var s = g.Collection($"{Root}/restoreSessions").FirstOrDefault(x => x["id"]!.ToString() == id);
                if (s is null) return InMemoryGraph.Error(HttpStatusCode.NotFound, "NotFound", "Unknown restore session");
                s["status"] = "completed";
                s["completedDateTime"] = DateTimeOffset.UtcNow.ToString("o");
                return InMemoryGraph.Json(s.DeepClone());
            }
            return InMemoryGraph.Error(HttpStatusCode.NotFound, "NotFound", $"No fake for POST {path}");
        });
    }
}
