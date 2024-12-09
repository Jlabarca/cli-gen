using System.CommandLine;
using Octokit;
using GitLocal = LibGit2Sharp;
using Repository = LibGit2Sharp.Repository;

namespace CliToolGenerator;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("CLI Tool Generator - Create and publish C# CLI tools to GitHub");

        var nameOption = new Option<string>("--name", "Name of the CLI tool");
        var descriptionOption = new Option<string>("--description", "Description of the CLI tool");
        var githubTokenOption = new Option<string>("--github-token", "GitHub Personal Access Token");
        var authorOption = new Option<string>("--author", "Author name");
        var privateOption = new Option<bool>("--private", () => false, "Create as private repository");

        rootCommand.AddOption(nameOption);
        rootCommand.AddOption(descriptionOption);
        rootCommand.AddOption(githubTokenOption);
        rootCommand.AddOption(authorOption);
        rootCommand.AddOption(privateOption);

        rootCommand.SetHandler(
            async (string name, string description, string githubToken, string author, bool isPrivate) =>
            {
                await CreateCliProject(name, description, githubToken, author, isPrivate);
            },
            nameOption, descriptionOption, githubTokenOption, authorOption, privateOption);

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task ValidateGitHubToken(string token)
    {
        var github = new GitHubClient(new ProductHeaderValue("CliToolGenerator"))
        {
            Credentials = new Credentials(token)
        };

        try
        {
            // Test the token by getting the user
            await github.User.Current();
        }
        catch (AuthorizationException)
        {
            throw new Exception("Invalid GitHub token. Please check if the token is correct.");
        }
        catch (ForbiddenException)
        {
            throw new Exception(@"The GitHub token doesn't have the required permissions. 
Please create a new token with the following permissions:
- repo (Full control of private repositories)
- workflow (Update GitHub Action workflows)
- write:packages (Write packages)

You can create a new token at: https://github.com/settings/tokens/new");
        }
    }

    private static async Task CreateCliProject(string name, string description, string githubToken, string author, bool isPrivate)
    {
        Console.WriteLine($"Creating CLI project: {name}");

        // Validate GitHub token first
        await ValidateGitHubToken(githubToken);

        // Create project directory
        var projectDir = Path.Combine(Directory.GetCurrentDirectory(), name);
        Directory.CreateDirectory(projectDir);

        // Create project files
        await CreateProjectFile(projectDir, name);
        await CreateProgramFile(projectDir);
        await CreateGitIgnore(projectDir);
        await CreateReadme(projectDir, name, description);
        await CreateWorkflows(projectDir);

        // Verify all files were created
        var expectedFiles = new[]
        {
            Path.Combine(projectDir, $"{name}.csproj"),
            Path.Combine(projectDir, "Program.cs"),
            Path.Combine(projectDir, ".gitignore"),
            Path.Combine(projectDir, "README.md"),
            Path.Combine(projectDir, ".github", "workflows", "ci.yml")
        };

        foreach (var file in expectedFiles)
        {
            if (!File.Exists(file))
            {
                throw new Exception($"Failed to create file: {file}");
            }
        }

        // Initialize git repository
        GitLocal.Repository.Init(projectDir);

        // Ensure .github directory is included
        if (!Directory.Exists(Path.Combine(projectDir, ".github")))
        {
            throw new Exception("Failed to create .github directory structure");
        }

        // Create GitHub repository
        var github = new GitHubClient(new ProductHeaderValue("CliToolGenerator"))
        {
            Credentials = new Credentials(githubToken)
        };

        Octokit.Repository repo;
        try
        {
            repo = await github.Repository.Create(new NewRepository(name)
            {
                Description = description,
                Private = isPrivate,
                AutoInit = false,
                //GitignoreTemplate = "C#",
                LicenseTemplate = "mit"
            });
        }
        catch (ApiValidationException ex)
        {
            if (ex.ApiError.Errors.Any(e => e.Code == "already_exists"))
            {
                throw new Exception($"A repository named '{name}' already exists. Please choose a different name.");
            }
            throw new Exception($"GitHub API validation error: {ex.Message}");
        }
        catch (ApiException ex)
        {
            throw new Exception($"GitHub API error: {ex.Message}");
        }

        // Add remote and push
        using (var repository = new Repository(projectDir))
        {
            // Initialize the repository and create initial branch
            GitLocal.Commands.Stage(repository, "*");

            // Create a signature for the commit
            var signature = new GitLocal.Signature(author, "noreply@github.com", DateTimeOffset.Now);

            // Create initial commit
            var options = new GitLocal.CommitOptions { AllowEmptyCommit = false };
            repository.Commit("Initial commit: CLI tool template", signature, signature, options);

            // Ensure we're on the main branch
            var currentBranch = repository.Branches["main"];
            if (currentBranch == null)
            {
                currentBranch = GitLocal.RepositoryExtensions.CreateBranch(repository, "main");
                GitLocal.Commands.Checkout(repository, currentBranch);
            }

            // Add remote
            var remote = repository.Network.Remotes.Add("origin", repo.CloneUrl);

            var pushOptions = new GitLocal.PushOptions
            {
                CredentialsProvider = (_url, _user, _cred) =>
                    new GitLocal.UsernamePasswordCredentials { Username = githubToken, Password = string.Empty }
            };

            // Push to remote
            repository.Network.Push(remote, $"refs/heads/main", pushOptions);
        }

        Console.WriteLine($"\nProject created successfully!");
        Console.WriteLine($"Repository URL: {repo.CloneUrl}");
        Console.WriteLine("\nTo run the tool directly:");
        Console.WriteLine($"  dotnet run --project {repo.CloneUrl}");
        Console.WriteLine("\nTo clone and develop locally:");
        Console.WriteLine($"  git clone {repo.CloneUrl}");
        Console.WriteLine("  cd " + name);
        Console.WriteLine("  dotnet restore");
        Console.WriteLine("  dotnet run -- --help");
    }

    private static async Task CreateProjectFile(string projectDir, string name)
    {
        var projectContent = @$"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net7.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>{name}</ToolCommandName>
    <PackageOutputPath>./nupkg</PackageOutputPath>
    <Version>1.0.0</Version>
    <PublishTrimmed>true</PublishTrimmed>
    <PublishSingleFile>true</PublishSingleFile>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""System.CommandLine"" Version=""2.0.0-beta4.22272.1"" />
    <PackageReference Include=""Spectre.Console"" Version=""0.47.0"" />
    <PackageReference Include=""Microsoft.Extensions.DependencyInjection"" Version=""7.0.0"" />
    <PackageReference Include=""Microsoft.Extensions.Configuration"" Version=""7.0.0"" />
    <PackageReference Include=""Microsoft.Extensions.Configuration.Json"" Version=""7.0.0"" />
  </ItemGroup>
</Project>";

        await File.WriteAllTextAsync(
            Path.Combine(projectDir, $"{name}.csproj"),
            projectContent
        );
    }

    private static async Task CreateProgramFile(string projectDir)
    {
        var programContent = @"using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace CliTool;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // This is a template for your CLI tool
        // Prompt: ""Create a CLI tool made with C# using System.CommandLine and Spectre.Console to do X""
        
        var rootCommand = new RootCommand(""Your CLI tool description"");
        
        // Add your command options here
        var inputOption = new Option<string>(""--input"", ""Input parameter description"");
        rootCommand.AddOption(inputOption);

        rootCommand.SetHandler(
            async (string input) =>
            {
                await AnsiConsole.Status()
                    .StartAsync(""Processing..."", async ctx => 
                    {
                        // Your CLI tool logic here
                        await Task.Delay(1000); // Placeholder
                        AnsiConsole.MarkupLine(""[green]Success![/]"");
                    });
            },
            inputOption);

        return await rootCommand.InvokeAsync(args);
    }
}";

        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "Program.cs"),
            programContent
        );
    }

    private static async Task CreateGitIgnore(string projectDir)
    {
        var gitignoreContent = @"bin/
obj/
.vs/
*.user
nupkg/
.idea/";

        await File.WriteAllTextAsync(
            Path.Combine(projectDir, ".gitignore"),
            gitignoreContent
        );
    }

    private static async Task CreateReadme(string projectDir, string name, string description)
    {
        var readmeContent = @$"# {name}

{description}

## Installation

You can run this tool directly from the repository:

```bash
dotnet run --project https://github.com/yourusername/{name}
```

Or install it globally:

```bash
dotnet tool install --global {name}
```

## Usage

```bash
{name} --help
```

## Development

1. Clone the repository
2. Run `dotnet restore`
3. Run `dotnet build`
4. Run `dotnet run -- --help`

## License

MIT";

        await File.WriteAllTextAsync(
            Path.Combine(projectDir, "README.md"),
            readmeContent
        );
    }

    private static async Task CreateWorkflows(string projectDir)
    {
        var workflowsDir = Path.Combine(projectDir, ".github", "workflows");
        Directory.CreateDirectory(workflowsDir);

        var ciContent = @"name: CI

on:
  push:
    branches: [ main, master ]
  pull_request:
    branches: [ main, master ]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v3
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: 7.0.x
    - name: Restore dependencies
      run: dotnet restore
    - name: Build
      run: dotnet build --no-restore
    - name: Test
      run: dotnet test --no-build --verbosity normal";

        await File.WriteAllTextAsync(
            Path.Combine(workflowsDir, "ci.yml"),
            ciContent
        );
    }
}