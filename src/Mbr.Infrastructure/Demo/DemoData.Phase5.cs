using System.Text.Json.Nodes;

namespace Mbr.Infrastructure.Demo;

public static partial class DemoData
{
    static partial void SeedPhase5Plus(InMemoryGraph g, InMemoryGraph arm)
    {
        // OneDrives for the first few demo users.
        foreach (var u in g.Collection("users").Take(3))
        {
            var drive = g.AddDrive($"users/{u["id"]}", "OneDrive");
            var first = u["displayName"]?.ToString()?.Split(' ')[0] ?? "user";
            drive.PutFile("/Documents/Notes.txt", $"{first}'s notes\n- follow up with finance\n- book travel");
            drive.PutFile("/Documents/Q3 forecast.xlsx", new string('x', 48_000));
            drive.PutFile("/Pictures/team-offsite.jpg", new string('p', 220_000));
            drive.PutFile("/Desktop/todo.md", "# Today\n- [ ] review access\n- [x] backups");
        }

        // A SharePoint site with two libraries.
        g.Add("sites", new JsonObject
        {
            ["id"] = "contoso-demo.sharepoint.com,7d1c0b,finance", ["displayName"] = "Finance",
            ["webUrl"] = "https://contoso-demo.sharepoint.com/sites/finance",
        });
        g.Add("sites", new JsonObject
        {
            ["id"] = "contoso-demo.sharepoint.com,8e2d1c,hr", ["displayName"] = "Human Resources",
            ["webUrl"] = "https://contoso-demo.sharepoint.com/sites/hr",
        });
        g.On(HttpMethod.Get, "/v1.0/sites/getAllSites", (_, _) => InMemoryGraph.Json(new JsonObject
        {
            ["value"] = new JsonArray(g.Collection("sites").Select(s => (JsonNode)s.DeepClone()).ToArray()),
        }));
        var docs = g.AddDrive("sites/contoso-demo.sharepoint.com,7d1c0b,finance", "Documents");
        docs.PutFile("/Budgets/2026 budget.xlsx", new string('b', 96_000));
        docs.PutFile("/Budgets/2025 budget.xlsx", new string('a', 91_000));
        docs.PutFile("/Policies/Expense policy.docx", new string('e', 30_000));
        var contracts = g.AddDrive("sites/contoso-demo.sharepoint.com,7d1c0b,finance", "Contracts");
        contracts.PutFile("/Suppliers/Fabrikam MSA.pdf", new string('f', 150_000));
        var hr = g.AddDrive("sites/contoso-demo.sharepoint.com,8e2d1c,hr", "Documents");
        hr.PutFile("/Onboarding/Checklist.docx", new string('c', 22_000));
        hr.PutFile("/Handbook.pdf", new string('h', 310_000));

        SeedPhase6Plus(g, arm);
    }

    static partial void SeedPhase6Plus(InMemoryGraph g, InMemoryGraph arm);
}
