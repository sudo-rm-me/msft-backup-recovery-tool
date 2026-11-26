using System.Text;
using System.Text.Json.Nodes;

namespace Mbr.Infrastructure.Demo;

public static partial class DemoData
{
    static partial void SeedPhase6Plus(InMemoryGraph g, InMemoryGraph arm)
    {
        var start = DateTime.UtcNow.Date.AddDays(3).AddHours(10);
        foreach (var u in g.Collection("users").Take(3))
        {
            var upn = u["userPrincipalName"]?.ToString() ?? "user@contoso-demo.onmicrosoft.com";
            u["mail"] ??= upn;
            var mb = g.AddMailbox(u["id"]!.ToString());
            mb.AddFolder("/Inbox/Projects");
            mb.AddFolder("/Inbox/Receipts");
            mb.AddMessage("/Inbox", "Welcome to Contoso", "We're glad you're here. Your laptop arrives Monday.", "hr@contoso-demo.com");
            mb.AddMessage("/Inbox", "Weekly status", "All green this week.", "megan@contoso-demo.com");
            mb.AddMessage("/Inbox/Projects", "Falcon: project plan", "Plan attached.", "lee@contoso-demo.com",
                ("Falcon plan.pdf", Encoding.UTF8.GetBytes("%PDF-1.7 demo")));
            mb.AddMessage("/Inbox/Receipts", "Your travel receipt", "Flight CPH-LHR: 1,240 DKK", "travel@contoso-demo.com");
            mb.AddMessage("/Sent Items", "Re: Weekly status", "Thanks!", upn);

            g.Collection($"users/{u["id"]}/events").Add(new JsonObject
            {
                ["id"] = g.NewId(), ["changeKey"] = "1", ["subject"] = "Quarterly business review", ["type"] = "singleInstance",
                ["start"] = new JsonObject { ["dateTime"] = start.ToString("s"), ["timeZone"] = "UTC" },
                ["end"] = new JsonObject { ["dateTime"] = start.AddHours(1).ToString("s"), ["timeZone"] = "UTC" },
                ["body"] = new JsonObject { ["contentType"] = "text", ["content"] = "Agenda: numbers, risks, next quarter" },
                ["attendees"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["name"] = "Megan Bowen", ["address"] = "megan@contoso-demo.com" } }),
            });
            g.Collection($"users/{u["id"]}/contacts").Add(new JsonObject
            {
                ["id"] = g.NewId(), ["changeKey"] = "1", ["displayName"] = "Fabrikam support",
                ["emailAddresses"] = new JsonArray(new JsonObject { ["address"] = "support@fabrikam.com", ["name"] = "Fabrikam support" }),
                ["businessPhones"] = new JsonArray("+45 70 00 00 00"),
            });
        }
        SeedPhase7Plus(g, arm);
    }

    static partial void SeedPhase7Plus(InMemoryGraph g, InMemoryGraph arm);
}
