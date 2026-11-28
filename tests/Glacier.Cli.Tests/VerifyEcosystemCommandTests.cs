using System;
using System.IO;
using System.Text.Json;
using Glacier.Cli.Commands;
using Xunit;

namespace Glacier.Cli.Tests;

public class VerifyEcosystemCommandTests
{
    [Fact]
    public void VerifyEcosystem_ValidConditionedCrossRepoRefs_PassesAndEmitsJson()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"glacier_eco_pass_{Guid.NewGuid():N}");
        try
        {
            string repoA = Path.Combine(tempRoot, "Glacier.Alpha");
            string repoB = Path.Combine(tempRoot, "Glacier.Beta");
            Directory.CreateDirectory(repoA);
            Directory.CreateDirectory(repoB);

            File.WriteAllText(
                Path.Combine(repoA, "Glacier.Alpha.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(repoB, "Glacier.Beta.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup Condition="'$(UseLocalProjectReferences)' == 'true' and Exists('..\\Glacier.Alpha\\Glacier.Alpha.csproj')">
                    <ProjectReference Include="..\Glacier.Alpha\Glacier.Alpha.csproj" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(UseLocalProjectReferences)' != 'true' or !Exists('..\\Glacier.Alpha\\Glacier.Alpha.csproj')">
                    <PackageReference Include="Glacier.Alpha" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            int exitCode = VerifyEcosystemCommandHandler.Execute([tempRoot, "--json"], stdout, stderr);

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr.ToString());

            using var doc = JsonDocument.Parse(stdout.ToString());
            JsonElement root = doc.RootElement;
            Assert.True(root.GetProperty("passed").GetBoolean());
            Assert.Equal(2, root.GetProperty("reposScanned").GetInt32());
            Assert.Equal(2, root.GetProperty("projectFilesScanned").GetInt32());
            Assert.Equal(0, root.GetProperty("violationCount").GetInt32());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void VerifyEcosystem_DetectsUnconditionedCrossRepoRef_MissingFallback_AndRetiredPackages()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"glacier_eco_fail_{Guid.NewGuid():N}");
        try
        {
            string repoA = Path.Combine(tempRoot, "Glacier.Alpha");
            string repoB = Path.Combine(tempRoot, "Glacier.Beta");
            Directory.CreateDirectory(repoA);
            Directory.CreateDirectory(repoB);

            File.WriteAllText(
                Path.Combine(repoB, "Directory.Packages.props"),
                """
                <Project>
                  <ItemGroup>
                    <PackageVersion Include="Glacier.StatsViz" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(repoB, "Glacier.Beta.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="..\Glacier.Alpha\Glacier.Alpha.csproj" />
                    <PackageReference Include="TinyPdf" Version="0.1.0" />
                  </ItemGroup>
                </Project>
                """);

            VerifyEcosystemResult result = VerifyEcosystemCommandHandler.VerifyWorkspace(tempRoot);

            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Rule == "UNCONDITIONED_CROSS_REPO_REF" && v.RepoName == "Glacier.Beta");
            Assert.Contains(result.Violations, v => v.Rule == "MISSING_NUGET_FALLBACK" && v.RepoName == "Glacier.Beta");
            Assert.Contains(result.Violations, v => v.Rule == "RETIRED_PACKAGE" && v.Message.Contains("Glacier.StatsViz"));
            Assert.Contains(result.Violations, v => v.Rule == "RETIRED_PACKAGE" && v.Message.Contains("TinyPdf"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void VerifyEcosystem_CheckSecrets_DetectsExposedCredentialAndRespectsExclusions()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"glacier_eco_sec_{Guid.NewGuid():N}");
        try
        {
            string repoClean = Path.Combine(tempRoot, "Glacier.Gamma");
            string repoPredict = Path.Combine(tempRoot, "Glacier.Predict");
            Directory.CreateDirectory(repoClean);
            Directory.CreateDirectory(repoPredict);

            File.WriteAllText(
                Path.Combine(repoClean, "Glacier.Gamma.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(repoPredict, "Glacier.Predict.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Glacier.StatsViz" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """);

            // Assemble synthetic credential parts at runtime so static scanners do not flag test source.
            string syntheticToken = string.Concat("ghp_", "9xK2mP7qR4tV8wZ1", "cN5bJ3hL6fD0sA2eY4uI");
            File.WriteAllText(
                Path.Combine(repoClean, "Config.cs"),
                $"internal static class Cfg {{ internal const string T = \"{syntheticToken}\"; }}");

            VerifyEcosystemResult result = VerifyEcosystemCommandHandler.VerifyWorkspace(
                tempRoot,
                new VerifyEcosystemOptions(CheckSecrets: true, IncludePredict: false));

            Assert.False(result.Passed);
            Assert.Equal(1, result.ReposScanned);
            Assert.True(result.SecretFilesScanned >= 2);
            Assert.DoesNotContain(result.Violations, v => v.RepoName == "Glacier.Predict");
            Assert.Contains(result.Violations, v => v.Rule == "EXPOSED_SECRET" && v.RepoName == "Glacier.Gamma");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
