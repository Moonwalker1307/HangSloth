using System.Text.RegularExpressions;
using Xunit;

namespace HangSloth.Tests;

/// Keeps the README's first steps and the package metadata from drifting away from the code.
public class DocsAndPackagingTests
{
    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([SampleApp.RepoRoot(), .. path]));

    [Fact]
    public void Readme_lists_every_HangApp_mode()
    {
        var readme = Read("README.md");
        var modes = Regex.Matches(Read("samples", "HangApp", "Program.cs"), @"case ""([a-z-]+)"":").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(modes);
        foreach (var mode in modes)
            Assert.Contains($"`{mode}`", readme);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("samples/DeadlockLab/README.md")]
    public void Quick_start_does_not_analyze_the_dotnet_run_launcher(string file)
    {
        // With "dotnet run ... &", $! is the SDK's launcher and the sample is its child process.
        var text = Read(file.Split('/'));
        Assert.DoesNotMatch(@"dotnet run --project samples/\w+ [^\r\n]*&\s*(#[^\r\n]*)?\r?\n\s*(sleep \d+; )?hangsloth \$!", text);
        Assert.Contains("hangsloth $!", text);
    }

    [Fact]
    public void Readme_explains_exit_codes_and_dumps_from_another_machine()
    {
        var readme = Read("README.md");
        Assert.Contains("Analyzing a dump from another machine", readme);
        Assert.Contains("--dac", readme);
        Assert.Contains("a check that failed when no other check found anything", readme);
    }

    [Fact]
    public void Package_readme_renders_on_nuget_org()
    {
        // nuget.org shows raw HTML as text and has no base for relative links or images.
        var csproj = Read("src", "HangSloth.Cli", "HangSloth.Cli.csproj");
        var readmeName = Regex.Match(csproj, @"<PackageReadmeFile>([^<]+)</PackageReadmeFile>").Groups[1].Value;
        Assert.Equal("PACKAGE.md", readmeName);
        Assert.Contains("Include=\"PACKAGE.md\"", csproj);
        var text = Read("src", "HangSloth.Cli", readmeName);
        Assert.DoesNotContain("<img", text);
        Assert.DoesNotContain("<p ", text);
        Assert.DoesNotMatch(@"\]\((?!https?://|#)", text);
    }

    [Fact]
    public void Sdk_is_pinned_and_rolls_forward_to_newer_feature_bands()
    {
        var globalJson = Read("global.json");
        Assert.Contains("\"rollForward\": \"latestFeature\"", globalJson);
        Assert.Matches("\"version\": \"10\\.0\\.\\d+\"", globalJson);
    }

    [Fact]
    public void Scripts_avoid_bash_4_builtins()
    {
        // mapfile and associative arrays need bash 4; the stock macOS shell is 3.2. Only a grep: the bash 3.2 runs are done by hand.
        foreach (var script in new[] { "try-deadlock-lab.sh", "try-diagnostic-scenarios.sh" })
        {
            var text = Read("scripts", script);
            Assert.DoesNotContain("mapfile", text);
            Assert.DoesNotContain("readarray", text);
            Assert.DoesNotContain("declare -A", text);
        }
    }
}
