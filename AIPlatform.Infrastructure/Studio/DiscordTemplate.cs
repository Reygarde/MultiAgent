using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public static class DiscordTemplate
{
    public const string Id = "discord-bot-dotnet";
    public const int Version = 1;
    public const string Image = "aiplatform-sandbox:discord-net10";
    public const string EditableFile = "src/App/CommandResponses.cs";

    public static IReadOnlyList<GeneratedFile> Files =>
    [
        new("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup><PackageReference Include="Discord.Net.WebSocket" Version="[3.20.2]" /></ItemGroup>
            </Project>
            """),
        new("src/App/Program.cs", """
            using Discord;
            using Discord.WebSocket;
            using App;

            var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("DISCORD_BOT_TOKEN is required.");
                return 1;
            }

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
            await using var client = new DiscordSocketClient();
            client.Log += message =>
            {
                Console.WriteLine($"Discord event: {message.Severity}");
                return Task.CompletedTask;
            };
            client.Ready += async () =>
            {
                try
                {
                    foreach (var (name, description) in new[]
                    {
                        ("ping", "Check whether the bot is responding"),
                        ("help", "List available commands"),
                        ("info", "Show information about the bot")
                    })
                        await client.Rest.CreateGlobalCommand(new SlashCommandBuilder()
                            .WithName(name).WithDescription(description).Build());
                    Console.WriteLine("Slash commands registered.");
                }
                catch (Exception)
                {
                    Console.Error.WriteLine("Failed to register slash commands.");
                }
            };
            client.SlashCommandExecuted += async command =>
            {
                try
                {
                    await command.RespondAsync(CommandResponses.For(command.CommandName));
                }
                catch (Exception)
                {
                    Console.Error.WriteLine("Failed to respond to a slash command.");
                }
            };
            try
            {
                await client.LoginAsync(TokenType.Bot, token);
                await client.StartAsync();
                await Task.Delay(Timeout.Infinite, shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                Console.WriteLine("Stopping bot.");
            }
            catch (Exception)
            {
                Console.Error.WriteLine("Failed to start or run the bot. Check configuration and network access.");
                return 1;
            }
            finally
            {
                await client.StopAsync();
            }
            return 0;
            """),
        new(EditableFile, """
            namespace App;

            public static class CommandResponses
            {
                public static string For(string command) => command switch
                {
                    "ping" => "Pong!",
                    "help" => "Available commands: /ping, /help, /info.",
                    "info" => "Discord bot built with .NET 10.",
                    _ => "Unknown command."
                };
            }
            """),
        new("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsTestProject>true</IsTestProject>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../../src/App/App.csproj" />
                <PackageReference Include="xunit" Version="[2.9.3]" />
                <PackageReference Include="xunit.runner.visualstudio" Version="[3.1.4]" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="[17.14.1]" />
              </ItemGroup>
            </Project>
            """),
        new("tests/App.Tests/UnitTests.cs", """
            using App;
            using Xunit;

            public class UnitTests
            {
                [Fact]
                public void PingReturnsPong() => Assert.Equal("Pong!", CommandResponses.For("ping"));

                [Fact]
                public void HelpListsCommands()
                {
                    var help = CommandResponses.For("help");
                    Assert.Contains("/ping", help);
                    Assert.Contains("/help", help);
                    Assert.Contains("/info", help);
                }

                [Fact]
                public void InfoDescribesBot() => Assert.Contains("bot", CommandResponses.For("info"));

                [Fact]
                public void UnknownCommandIsReported() => Assert.Equal("Unknown command.", CommandResponses.For("other"));
            }
            """),
        new("README.md", """
            # Discord bot template v1

            Create a bot in the Discord developer portal and invite it with the `bot`
            and `applications.commands` scopes. Set the `DISCORD_BOT_TOKEN` environment
            variable in the environment where you choose to run the bot. Never commit,
            share, or paste the token into AIPlatform Studio.

            The bot registers the global slash commands /ping, /help and /info.
            Global command updates can take time to propagate. The xUnit tests exercise
            the response logic without connecting to Discord or using a token.

            The generated source is untrusted. AIPlatform's Docker verification builds
            and tests it offline after explicit approval; it does not start the bot.
            Running the bot against Discord is a separate manual decision.
            """)
    ];

    public static IReadOnlyList<PlannedFile> PlannedFiles =>
    [
        new("src/App/App.csproj", "Pinned Discord.Net project", []),
        new(EditableFile, "Offline slash command response logic", ["src/App/App.csproj"]),
        new("src/App/Program.cs", "Discord gateway and command registration", [EditableFile]),
        new("tests/App.Tests/App.Tests.csproj", "Pinned offline xUnit project", ["src/App/App.csproj"]),
        new("tests/App.Tests/UnitTests.cs", "Offline command response tests", [EditableFile, "tests/App.Tests/App.Tests.csproj"]),
        new("README.md", "Configuration and security instructions", [])
    ];
}
