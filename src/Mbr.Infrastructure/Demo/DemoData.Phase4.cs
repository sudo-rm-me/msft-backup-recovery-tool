using System.Text.Json.Nodes;

namespace Mbr.Infrastructure.Demo;

public static partial class DemoData
{
    static partial void SeedMore(InMemoryGraph g, InMemoryGraph arm)
    {
        // A few recently deleted objects for the recycle bin page.
        g.AddDeleted("users", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.user", ["displayName"] = "Lee Gu", ["userPrincipalName"] = "lee.gu@contoso-demo.onmicrosoft.com",
            ["mailNickname"] = "lee.gu", ["accountEnabled"] = true,
        }, DateTimeOffset.UtcNow.AddDays(-3));
        g.AddDeleted("users", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.user", ["displayName"] = "Contractor - Ana", ["userPrincipalName"] = "ana.contractor@contoso-demo.onmicrosoft.com",
            ["mailNickname"] = "ana.contractor", ["accountEnabled"] = false,
        }, DateTimeOffset.UtcNow.AddDays(-27));
        g.AddDeleted("groups", new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.group", ["displayName"] = "Project Falcon", ["mailNickname"] = "falcon", ["securityEnabled"] = true,
            ["mailEnabled"] = false, ["groupTypes"] = new JsonArray(),
        }, DateTimeOffset.UtcNow.AddDays(-10));
        SeedPhase5Plus(g, arm);
    }

    static partial void SeedPhase5Plus(InMemoryGraph g, InMemoryGraph arm);
}
