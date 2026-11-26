namespace Mbr.Infrastructure.Demo;

public static partial class DemoData
{
    /// <summary>Microsoft 365 Backup protecting a few demo users and the Finance site.</summary>
    static partial void SeedPhase9Plus(InMemoryGraph g, InMemoryGraph arm)
    {
        var users = g.Collection("users").Where(u => u["jobTitle"] != null).Take(3)
            .Select(u => (u["id"]!.ToString(), u["userPrincipalName"]!.ToString())).ToList();
        var finance = g.Collection("sites").FirstOrDefault(s => s["displayName"]?.ToString() == "Finance");
        DemoM365Backup.Seed(g, users, users.Take(2).ToList(),
            finance is null ? [] : [(finance["id"]!.ToString(), "Finance")]);
    }
}
