using System.CommandLine;
using Mbr.Cli;

var root = new RootCommand("mbr - Microsoft tenant backup & recovery");
root.Options.Add(Cli.JsonOption);
root.Subcommands.Add(CoreCommands.Login());
root.Subcommands.Add(CoreCommands.Tenant());
root.Subcommands.Add(CoreCommands.Storage());
root.Subcommands.Add(CoreCommands.Job());
root.Subcommands.Add(CoreCommands.Schedule());
foreach (var c in FeatureCommands.All()) root.Subcommands.Add(c);

return await root.Parse(args).InvokeAsync();
