using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Mbr.LiveTests;

/// <summary>
/// Smoke tests against a real test tenant. Never point them at production: the round trip creates, deletes and restores a
/// group named mbr-smoke-*, and removes it afterwards.
/// </summary>
public sealed class SmokeTests
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task Both_apps_sign_in_and_the_backup_app_has_every_required_permission()
    {
        using var app = new LiveApp();
        var client = app.CreateClient();
        var (id, _) = await app.AddTenantAsync(client);

        var check = JsonNode.Parse(await (await client.PostAsync($"/api/tenants/{id}/permission-check", null)).Content.ReadAsStringAsync())!;

        foreach (var role in new[] { "backup", "restore" })
            Assert.True(check[role]!["tokenAcquired"]!.GetValue<bool>(), $"The {role} app couldn't sign in: {check[role]!["error"]}");
        Assert.True(check["backup"]!["missing"]!.AsArray().Count == 0, $"The backup app is missing permissions: {check["backup"]!["missing"]}");
    }

    [Fact]
    public async Task Config_backup_of_entra_and_intune_completes_with_items()
    {
        using var app = new LiveApp();
        var client = app.CreateClient();
        var (id, _) = await app.AddTenantAsync(client);

        var job = await app.RunJobAsync(client, "backup.config", id, new { workloads = new[] { "entra", "intune" } }, JobTimeout);

        Assert.True(job["status"]!.GetValue<string>() == "Succeeded",
            $"Backup ended {job["status"]}: {job["error"]}\n{await client.GetStringAsync($"/api/jobs/{job["id"]}/logs")}");
        var snapshot = JsonNode.Parse(await client.GetStringAsync($"/api/snapshots/{job["result"]!["snapshotId"]}"))!;
        var types = snapshot["types"]!.AsArray().Select(t => t!["resourceType"]!.GetValue<string>()).ToList();
        Assert.Contains("entra.group", types);
        Assert.Contains("entra.authorizationPolicy", types);
    }

    [Fact]
    public async Task A_deleted_group_is_restored_from_the_recycle_bin()
    {
        using var app = new LiveApp();
        var client = app.CreateClient();
        var (id, directoryId) = await app.AddTenantAsync(client);
        using var graph = await app.GraphAsync(directoryId);

        var name = $"mbr-smoke-{Guid.NewGuid():N}"[..24];
        var created = await graph.PostAsJsonAsync("groups", new
        {
            displayName = name, mailNickname = name, mailEnabled = false, securityEnabled = true,
            description = "Created by MBR live tests; safe to delete.",
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var groupId = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        try
        {
            var backup = await app.RunJobAsync(client, "backup.config", id, new { workloads = new[] { "entra" } }, JobTimeout);
            Assert.Equal("Succeeded", backup["status"]!.GetValue<string>());

            (await graph.DeleteAsync($"groups/{groupId}")).EnsureSuccessStatusCode();

            // The recycle bin is eventually consistent: wait until MBR sees the group there.
            var until = DateTime.UtcNow.AddMinutes(2);
            while (true)
            {
                var deleted = JsonNode.Parse(await client.GetStringAsync($"/api/tenants/{id}/entra/deleted?kind=group"))!.AsArray();
                if (deleted.Any(d => d!["id"]!.GetValue<string>() == groupId)) break;
                Assert.True(DateTime.UtcNow < until, "The deleted group never appeared in the recycle bin.");
                await Task.Delay(5000);
            }

            var resp = await client.PostAsJsonAsync($"/api/tenants/{id}/entra/deleted/restore", new { ids = new[] { groupId } });
            resp.EnsureSuccessStatusCode();
            var restore = await app.WaitForJobAsync(client, JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["jobId"]!.GetValue<string>(), JobTimeout);
            Assert.Equal("Succeeded", restore["status"]!.GetValue<string>());

            var back = await graph.GetAsync($"groups/{groupId}");
            Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        }
        finally
        {
            // Delete and purge, whether the group is live or in the recycle bin.
            await graph.DeleteAsync($"groups/{groupId}");
            await graph.DeleteAsync($"directory/deletedItems/{groupId}");
        }
    }
}
