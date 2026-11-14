using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public static class FullStackTemplate
{
    public const string Id = "fullstack-react-dotnet";
    public const int Version = 1;
    public const string Image = "aiplatform-fullstack-sandbox:1.0.0";
    public const int MaxLockfileBytes = 262144;
    public const int MaxProjectBytes = 1048576;

    private static readonly string[] BackendPaths =
    [
        "backend/Api/TaskService.cs",
        "backend/Api/SqliteTaskRepository.cs",
        "backend/Api/TaskEndpoints.cs"
    ];

    private static readonly string[] FrontendPaths =
    [
        "frontend/src/api.ts",
        "frontend/src/TaskItem.tsx",
        "frontend/src/App.tsx"
    ];

    public static bool IsEditable(string path) =>
        BackendPaths.Contains(path, StringComparer.Ordinal) ||
        FrontendPaths.Contains(path, StringComparer.Ordinal);

    public static bool IsEditableForRole(string path, string role) => role switch
    {
        "Backend" => BackendPaths.Contains(path, StringComparer.Ordinal),
        "Frontend" => FrontendPaths.Contains(path, StringComparer.Ordinal),
        _ => false
    };

    public static bool IsPath(string path) => Files.Any(file => file.Path == path);

    public static bool IsTrustedLockfile(string path, string content) =>
        path == "frontend/package-lock.json" &&
        content == ReactTemplate.Files.Single(file => file.Path == "package-lock.json").Content;

    public static IReadOnlyList<GeneratedFile> ForReview(IReadOnlyList<GeneratedFile> files) =>
        files.Where(file => IsEditable(file.Path) || file.Path is
            "contracts/openapi.json" or "backend/Api.Tests/TaskApiTests.cs" or
            "frontend/tests/App.test.tsx" or "frontend/integration/api.integration.test.ts").ToArray();

    public static IReadOnlyList<PlannedFile> PlannedFiles => Files.Where(file => IsEditable(file.Path)).Select(file => new PlannedFile(
        file.Path,
        file.Path switch
        {
            "backend/Api/TaskService.cs" => "BackendAgent: validate task operations and map repository results.",
            "backend/Api/SqliteTaskRepository.cs" => "BackendAgent: implement parameterized SQLite CRUD and status filtering.",
            "backend/Api/TaskEndpoints.cs" => "BackendAgent: map the exact HTTP contract with minimal APIs.",
            "frontend/src/api.ts" => "FrontendAgent: implement the typed HTTP client from the OpenAPI contract.",
            "frontend/src/TaskItem.tsx" => "FrontendAgent: implement accessible task item actions.",
            "frontend/src/App.tsx" => "FrontendAgent: implement responsive CRUD and server-backed status filtering.",
            _ => "Trusted immutable template, API contract, configuration or contract tests."
        },
        file.Path switch
        {
            "backend/Api/SqliteTaskRepository.cs" => [],
            "backend/Api/TaskEndpoints.cs" => ["backend/Api/TaskService.cs"],
            "frontend/src/api.ts" => [],
            "frontend/src/TaskItem.tsx" => [],
            "frontend/src/App.tsx" => ["frontend/src/api.ts", "frontend/src/TaskItem.tsx"],
            "backend/Api.Tests/TaskApiTests.cs" => ["backend/Api/TaskEndpoints.cs", "backend/Api/SqliteTaskRepository.cs"],
            "frontend/tests/App.test.tsx" => ["frontend/src/App.tsx", "frontend/src/TaskItem.tsx"],
            "frontend/integration/api.integration.test.ts" => ["frontend/src/api.ts", "contracts/openapi.json"],
            _ => []
        })).ToArray();

    public static void Validate(IReadOnlyList<GeneratedFile> files) => Validate(files, allowPartial: false);

    public static void ValidatePartial(IReadOnlyList<GeneratedFile> files) => Validate(files, allowPartial: true);

    private static void Validate(IReadOnlyList<GeneratedFile> files, bool allowPartial)
    {
        var expected = Files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var suppliedPaths = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        var missing = expected.Except(suppliedPaths, StringComparer.Ordinal).ToArray();
        var unexpected = suppliedPaths.Except(expected, StringComparer.Ordinal).ToArray();
        var immutablePaths = Files.Where(file => !IsEditable(file.Path)).Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        if (files.Count != suppliedPaths.Count || unexpected.Length > 0 ||
            (!allowPartial && missing.Length > 0) ||
            (allowPartial && missing.Any(path => immutablePaths.Contains(path))))
        {
            throw new StudioValidationException(
                $"Full-stack files must match the fixed versioned template paths. Missing: {string.Join(", ", missing)}. Unexpected: {string.Join(", ", unexpected)}.");
        }
        var supplied = files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        foreach (var baseline in Files.Where(file => !IsEditable(file.Path) && suppliedPaths.Contains(file.Path)))
            if (supplied[baseline.Path].Content != baseline.Content)
                throw new StudioValidationException($"The immutable full-stack template file was modified: {baseline.Path}.");
        if (supplied.TryGetValue("frontend/package-lock.json", out var lockfile) &&
            !IsTrustedLockfile("frontend/package-lock.json", lockfile.Content))
            throw new StudioValidationException("The approved frontend lockfile must not be modified.");
    }

    public static IReadOnlyList<GeneratedFile> Files { get; } =
    [
        new("backend/Api/Api.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Data.Sqlite" Version="[10.0.15]" />
              </ItemGroup>
            </Project>
            """),
        new("backend/Api/packages.lock.json", """
            {
              "version": 1,
              "dependencies": {
                "net10.0": {
                  "Microsoft.Data.Sqlite": {
                    "type": "Direct",
                    "requested": "[10.0.15, 10.0.15]",
                    "resolved": "10.0.15",
                    "contentHash": "PtnNCUloQOLAXhB09id+0CL2c9yd+9/wi0QHCHPa+zrTKEJTdu07jlUgblFjorYhKnwcHN1VS0z75jN8vFruuQ==",
                    "dependencies": {
                      "Microsoft.Data.Sqlite.Core": "10.0.15",
                      "SQLitePCLRaw.bundle_e_sqlite3": "2.1.13",
                      "SQLitePCLRaw.core": "2.1.13"
                    }
                  },
                  "Microsoft.Data.Sqlite.Core": {
                    "type": "Transitive",
                    "resolved": "10.0.15",
                    "contentHash": "mjDz/zleAF78J1KRN9FludN2bzbsMWoXYSLVcawADle7RBhZYpFZYivGRvbvlH/RtXT/gjO0yhG99tzdFiET+A==",
                    "dependencies": {
                      "SQLitePCLRaw.core": "2.1.13"
                    }
                  },
                  "SQLitePCLRaw.bundle_e_sqlite3": {
                    "type": "Transitive",
                    "resolved": "2.1.13",
                    "contentHash": "NlDixlC7uHmo+ojXRgkNvN0tQtVGpLDZ3x2OpgNj+G07VHh5cXSszFDrAiC4BpUiPwkcaeuQkMV9WQ123xVtmQ==",
                    "dependencies": {
                      "SQLitePCLRaw.lib.e_sqlite3": "2.1.13",
                      "SQLitePCLRaw.provider.e_sqlite3": "2.1.13"
                    }
                  },
                  "SQLitePCLRaw.core": {
                    "type": "Transitive",
                    "resolved": "2.1.13",
                    "contentHash": "2joofoinuHBFs0nMX3nKuXgxpXryRkCOdM2Ua6HBqQLLy6IW+5f28ZI+HAvMyF8vG7XfNz/lwfBfE6V4juru8Q=="
                  },
                  "SQLitePCLRaw.lib.e_sqlite3": {
                    "type": "Transitive",
                    "resolved": "2.1.13",
                    "contentHash": "D2UAuiGxFYBqeRXjjjGgvJ5UQifzn6wefmSqeKU7XcNGKLbJRP6X7kcPZ6opOBJGdCIlgbSxTvDpBbLvsN594A=="
                  },
                  "SQLitePCLRaw.provider.e_sqlite3": {
                    "type": "Transitive",
                    "resolved": "2.1.13",
                    "contentHash": "ZU60dSqoSNRe49KcQDUmOVgdRtjITOssVXFtiISs+zJXDI4D2Sndwpn2NOtzDQNPu0ZAvpoLPQ+GLKv6iEZn8Q==",
                    "dependencies": {
                      "SQLitePCLRaw.core": "2.1.13"
                    }
                  }
                }
              }
            }
            """),
        new("backend/Api/Program.cs", """
            var app = await App.ApiApplication.CreateAsync(args);
            await app.RunAsync();
            public partial class Program { }
            """),
        new("backend/Api/ApiApplication.cs", """
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Http;
            using Microsoft.Data.Sqlite;
            using Microsoft.Extensions.DependencyInjection;

            namespace App;

            public static class ApiApplication
            {
                public static async Task<WebApplication> CreateAsync(
                    string[] args, string? connectionString = null, CancellationToken cancellationToken = default)
                {
                    var builder = WebApplication.CreateBuilder(args);
                    var configured = connectionString ?? builder.Configuration.GetConnectionString("Default")
                        ?? "Data Source=data/tasks.db";
                    var dataSource = new SqliteConnectionStringBuilder(configured).DataSource;
                    if (!string.IsNullOrWhiteSpace(dataSource) && dataSource != ":memory:" &&
                        !dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dataSource))!);

                    builder.Services.AddProblemDetails();
                    builder.Services.AddValidation();
                    builder.Services.AddSingleton<ITaskRepository>(_ => new SqliteTaskRepository(configured));
                    builder.Services.AddScoped<ITaskService, TaskService>();

                    var app = builder.Build();
                    app.UseExceptionHandler();
                    app.UseStatusCodePages();
                    app.MapTaskEndpoints();
                    await DatabaseInitializer.InitializeAsync(configured, cancellationToken);
                    return app;
                }
            }
            """),
        new("backend/Api/DatabaseInitializer.cs", """"
            using Microsoft.Data.Sqlite;

            namespace App;

            public static class DatabaseInitializer
            {
                public static async Task InitializeAsync(string connectionString, CancellationToken cancellationToken)
                {
                    await using var connection = new SqliteConnection(connectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        CREATE TABLE IF NOT EXISTS tasks (
                            id TEXT NOT NULL PRIMARY KEY,
                            title TEXT NOT NULL CHECK(length(trim(title)) BETWEEN 1 AND 200),
                            completed INTEGER NOT NULL CHECK(completed IN (0, 1))
                        );
                        """;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            """"),
        new("backend/Api/TaskContracts.cs", """
            using System.ComponentModel.DataAnnotations;

            namespace App;

            /// <summary>A task returned by the API.</summary>
            public sealed record TaskDto(Guid Id, string Title, bool Completed);

            /// <summary>Payload for creating a task.</summary>
            public sealed record CreateTaskRequest
            {
                [Required, StringLength(200)]
                public required string Title { get; init; }
            }

            /// <summary>Payload for updating a task.</summary>
            public sealed record UpdateTaskRequest
            {
                [Required, StringLength(200)]
                public required string Title { get; init; }
                public required bool Completed { get; init; }
            }

            public interface ITaskRepository
            {
                Task<IReadOnlyList<TaskDto>> ListAsync(string status, CancellationToken cancellationToken);
                Task<TaskDto?> GetAsync(Guid id, CancellationToken cancellationToken);
                Task<TaskDto> CreateAsync(string title, CancellationToken cancellationToken);
                Task<TaskDto?> UpdateAsync(Guid id, string title, bool completed, CancellationToken cancellationToken);
                Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
            }

            public interface ITaskService
            {
                Task<IReadOnlyList<TaskDto>> ListAsync(string status, CancellationToken cancellationToken);
                Task<TaskDto?> GetAsync(Guid id, CancellationToken cancellationToken);
                Task<TaskDto?> CreateAsync(string? title, CancellationToken cancellationToken);
                Task<TaskDto?> UpdateAsync(Guid id, string? title, bool completed, CancellationToken cancellationToken);
                Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
            }
            """),
        new("backend/Api/TaskService.cs", """
            namespace App;

            public sealed class TaskService(ITaskRepository repository) : ITaskService
            {
                public Task<IReadOnlyList<TaskDto>> ListAsync(string status, CancellationToken cancellationToken) =>
                    repository.ListAsync(status, cancellationToken);

                public Task<TaskDto?> GetAsync(Guid id, CancellationToken cancellationToken) =>
                    repository.GetAsync(id, cancellationToken);

                public Task<TaskDto?> CreateAsync(string? title, CancellationToken cancellationToken)
                {
                    var normalized = title?.Trim();
                    return string.IsNullOrEmpty(normalized) || normalized.Length > 200
                        ? Task.FromResult<TaskDto?>(null)
                        : CreateCoreAsync(normalized, cancellationToken);
                }

                public Task<TaskDto?> UpdateAsync(
                    Guid id, string? title, bool completed, CancellationToken cancellationToken)
                {
                    var normalized = title?.Trim();
                    return string.IsNullOrEmpty(normalized) || normalized.Length > 200
                        ? Task.FromResult<TaskDto?>(null)
                        : repository.UpdateAsync(id, normalized, completed, cancellationToken);
                }

                public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
                    repository.DeleteAsync(id, cancellationToken);

                private async Task<TaskDto?> CreateCoreAsync(string title, CancellationToken cancellationToken) =>
                    await repository.CreateAsync(title, cancellationToken);
            }
            """),
        new("backend/Api/SqliteTaskRepository.cs", """"
            using Microsoft.Data.Sqlite;

            namespace App;

            public sealed class SqliteTaskRepository(string connectionString) : ITaskRepository
            {
                public async Task<IReadOnlyList<TaskDto>> ListAsync(string status, CancellationToken cancellationToken)
                {
                    if (status is not ("all" or "active" or "completed"))
                        throw new ArgumentException("The status filter is invalid.", nameof(status));
                    await using var connection = await OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = status switch
                    {
                        "active" => "SELECT id, title, completed FROM tasks WHERE completed = 0 ORDER BY rowid;",
                        "completed" => "SELECT id, title, completed FROM tasks WHERE completed = 1 ORDER BY rowid;",
                        _ => "SELECT id, title, completed FROM tasks ORDER BY rowid;"
                    };
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    var tasks = new List<TaskDto>();
                    while (await reader.ReadAsync(cancellationToken))
                        tasks.Add(Read(reader));
                    return tasks;
                }

                public async Task<TaskDto?> GetAsync(Guid id, CancellationToken cancellationToken)
                {
                    await using var connection = await OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT id, title, completed FROM tasks WHERE id = $id;";
                    command.Parameters.AddWithValue("$id", id.ToString("D"));
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
                }

                public async Task<TaskDto> CreateAsync(string title, CancellationToken cancellationToken)
                {
                    var task = new TaskDto(Guid.NewGuid(), title, false);
                    await using var connection = await OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO tasks (id, title, completed) VALUES ($id, $title, 0);";
                    command.Parameters.AddWithValue("$id", task.Id.ToString("D"));
                    command.Parameters.AddWithValue("$title", task.Title);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    return task;
                }

                public async Task<TaskDto?> UpdateAsync(
                    Guid id, string title, bool completed, CancellationToken cancellationToken)
                {
                    await using var connection = await OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        UPDATE tasks SET title = $title, completed = $completed WHERE id = $id;
                        """;
                    command.Parameters.AddWithValue("$id", id.ToString("D"));
                    command.Parameters.AddWithValue("$title", title);
                    command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
                    return await command.ExecuteNonQueryAsync(cancellationToken) == 1
                        ? new TaskDto(id, title, completed) : null;
                }

                public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
                {
                    await using var connection = await OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "DELETE FROM tasks WHERE id = $id;";
                    command.Parameters.AddWithValue("$id", id.ToString("D"));
                    return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
                }

                private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
                {
                    var connection = new SqliteConnection(connectionString);
                    var opened = false;
                    try
                    {
                        await connection.OpenAsync(cancellationToken);
                        opened = true;
                        return connection;
                    }
                    finally
                    {
                        if (!opened) await connection.DisposeAsync();
                    }
                }

                private static TaskDto Read(SqliteDataReader reader) =>
                    new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2) != 0);
            }
            """"),
        new("backend/Api/TaskEndpoints.cs", """
            using Microsoft.AspNetCore.Http.HttpResults;
            using Microsoft.AspNetCore.Mvc;

            namespace App;

            public static class TaskEndpoints
            {
                public static void MapTaskEndpoints(this WebApplication app)
                {
                    app.MapGet("/api/tasks", ListAsync)
                        .WithName("ListTasks").WithSummary("List tasks with an optional status filter")
                        .WithDescription("Status is all, active or completed; omitted status means all.")
                        .Produces<IReadOnlyList<TaskDto>>(StatusCodes.Status200OK)
                        .ProducesProblem(StatusCodes.Status400BadRequest);
                    app.MapGet("/api/tasks/{id:guid}", GetAsync)
                        .WithName("GetTask").WithSummary("Get one task by UUID")
                        .Produces<TaskDto>(StatusCodes.Status200OK).Produces(StatusCodes.Status404NotFound);
                    app.MapPost("/api/tasks", CreateAsync)
                        .WithName("CreateTask").WithSummary("Create a task")
                        .Produces<TaskDto>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest);
                    app.MapPut("/api/tasks/{id:guid}", UpdateAsync)
                        .WithName("UpdateTask").WithSummary("Replace a task title and completion state")
                        .Produces<TaskDto>(StatusCodes.Status200OK).Produces(StatusCodes.Status404NotFound)
                        .ProducesProblem(StatusCodes.Status400BadRequest);
                    app.MapDelete("/api/tasks/{id:guid}", DeleteAsync)
                        .WithName("DeleteTask").WithSummary("Delete a task")
                        .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound);
                }

                private static async Task<Results<Ok<IReadOnlyList<TaskDto>>, BadRequest<ProblemDetails>>> ListAsync(
                    string? status, ITaskService service, CancellationToken cancellationToken)
                {
                    var filter = string.IsNullOrWhiteSpace(status) ? "all" : status.Trim().ToLowerInvariant();
                    if (filter is not ("all" or "active" or "completed"))
                        return TypedResults.BadRequest(new ProblemDetails
                            { Status = 400, Title = "Invalid status filter.", Detail = "Use all, active or completed." });
                    return TypedResults.Ok(await service.ListAsync(filter, cancellationToken));
                }

                private static async Task<Results<Ok<TaskDto>, NotFound>> GetAsync(
                    Guid id, ITaskService service, CancellationToken cancellationToken)
                {
                    var task = await service.GetAsync(id, cancellationToken);
                    return task is null ? TypedResults.NotFound() : TypedResults.Ok(task);
                }

                private static async Task<Results<Created<TaskDto>, BadRequest<ProblemDetails>>> CreateAsync(
                    CreateTaskRequest request, ITaskService service, CancellationToken cancellationToken)
                {
                    var task = await service.CreateAsync(request.Title, cancellationToken);
                    return task is null
                        ? TypedResults.BadRequest(new ProblemDetails
                            { Status = 400, Title = "Invalid task title.", Detail = "Title must contain 1 to 200 non-whitespace characters." })
                        : TypedResults.Created($"/api/tasks/{task.Id:D}", task);
                }

                private static async Task<Results<Ok<TaskDto>, NotFound, BadRequest<ProblemDetails>>> UpdateAsync(
                    Guid id, UpdateTaskRequest request, ITaskService service, CancellationToken cancellationToken)
                {
                    var task = await service.UpdateAsync(id, request.Title, request.Completed, cancellationToken);
                    if (task is null && (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200))
                        return TypedResults.BadRequest(new ProblemDetails
                            { Status = 400, Title = "Invalid task title.", Detail = "Title must contain 1 to 200 non-whitespace characters." });
                    return task is null ? TypedResults.NotFound() : TypedResults.Ok(task);
                }

                private static async Task<Results<NoContent, NotFound>> DeleteAsync(
                    Guid id, ITaskService service, CancellationToken cancellationToken) =>
                    await service.DeleteAsync(id, cancellationToken)
                        ? TypedResults.NoContent() : TypedResults.NotFound();
            }
            """),
        new("backend/Api/Api.http", """
            @baseUrl = http://localhost:5080
            @taskId = 00000000-0000-0000-0000-000000000001

            ### List all tasks
            GET {{baseUrl}}/api/tasks?status=all

            ### List active tasks
            GET {{baseUrl}}/api/tasks?status=active

            ### Get a task (404 until created)
            GET {{baseUrl}}/api/tasks/{{taskId}}

            ### Create a task
            POST {{baseUrl}}/api/tasks
            Content-Type: application/json

            { "title": "Review the API contract" }

            ### Update and complete a task
            PUT {{baseUrl}}/api/tasks/{{taskId}}
            Content-Type: application/json

            { "title": "Review the API contract", "completed": true }

            ### Delete a task
            DELETE {{baseUrl}}/api/tasks/{{taskId}}
            """),
        new("backend/Api.Tests/Api.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsTestProject>true</IsTestProject>
                <IsPackable>false</IsPackable>
                <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
              </PropertyGroup>
              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
                <ProjectReference Include="../Api/Api.csproj" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" PrivateAssets="all" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
              </ItemGroup>
            </Project>
            """),
        new("backend/Api.Tests/packages.lock.json", """
            {
              "version": 1,
              "dependencies": {
                "net10.0": {
                  "Microsoft.NET.Test.Sdk": {
                    "type": "Direct",
                    "requested": "[17.14.1, )",
                    "resolved": "17.14.1",
                    "contentHash": "HJKqKOE+vshXra2aEHpi2TlxYX7Z9VFYkr+E5rwEvHC8eIXiyO+K9kNm8vmNom3e2rA56WqxU+/N9NJlLGXsJQ==",
                    "dependencies": {
                      "Microsoft.CodeCoverage": "17.14.1",
                      "Microsoft.TestPlatform.TestHost": "17.14.1"
                    }
                  },
                  "xunit": {
                    "type": "Direct",
                    "requested": "[2.9.3, )",
                    "resolved": "2.9.3",
                    "contentHash": "TlXQBinK35LpOPKHAqbLY4xlEen9TBafjs0V5KnA4wZsoQLQJiirCR4CbIXvOH8NzkW4YeJKP5P/Bnrodm0h9Q==",
                    "dependencies": {
                      "xunit.analyzers": "1.18.0",
                      "xunit.assert": "2.9.3",
                      "xunit.core": "[2.9.3]"
                    }
                  },
                  "xunit.runner.visualstudio": {
                    "type": "Direct",
                    "requested": "[3.1.4, )",
                    "resolved": "3.1.4",
                    "contentHash": "5mj99LvCqrq3CNi06xYdyIAXOEh+5b33F2nErCzI5zWiDdLHXiPXEWFSUAF8zlIv0ZWqjZNCwHTQeAPYbF3pCg=="
                  },
                  "Microsoft.CodeCoverage": {
                    "type": "Transitive",
                    "resolved": "17.14.1",
                    "contentHash": "pmTrhfFIoplzFVbhVwUquT+77CbGH+h4/3mBpdmIlYtBi9nAB+kKI6dN3A/nV4DFi3wLLx/BlHIPK+MkbQ6Tpg=="
                  },
                  "Microsoft.Data.Sqlite": {
                    "type": "Transitive",
                    "resolved": "10.0.15",
                    "contentHash": "PtnNCUloQOLAXhB09id+0CL2c9yd+9/wi0QHCHPa+zrTKEJTdu07jlUgblFjorYhKnwcHN1VS0z75jN8vFruuQ==",
                    "dependencies": {
                      "Microsoft.Data.Sqlite.Core": "10.0.15",
                      "SQLitePCLRaw.bundle_e_sqlite3": "2.1.13",
                      "SQLitePCLRaw.core": "2.1.13"
                    }
                  },
                  "Microsoft.Data.Sqlite.Core": {
                    "type": "Transitive",
                    "resolved": "10.0.15",
                    "contentHash": "mjDz/zleAF78J1KRN9FludN2bzbsMWoXYSLVcawADle7RBhZYpFZYivGRvbvlH/RtXT/gjO0yhG99tzdFiET+A==",
                    "dependencies": {
                      "SQLitePCLRaw.core": "2.1.13"
                    }
                  },
                  "Microsoft.TestPlatform.ObjectModel": {
                    "type": "Transitive",
                    "resolved": "17.14.1",
                    "contentHash": "xTP1W6Mi6SWmuxd3a+jj9G9UoC850WGwZUps1Wah9r1ZxgXhdJfj1QqDLJkFjHDCvN42qDL2Ps5KjQYWUU0zcQ=="
                  },
                  "Microsoft.TestPlatform.TestHost": {
                    "type": "Transitive",
                    "resolved": "17.14.1",
                    "contentHash": "d78LPzGKkJwsJXAQwsbJJ7LE7D1wB+rAyhHHAaODF+RDSQ0NgMjDFkSA1Djw18VrxO76GlKAjRUhl+H8NL8Z+Q==",
                    "dependencies": {
                      "Microsoft.TestPlatform.ObjectModel": "17.14.1",
                      "Newtonsoft.Json": "13.0.3"
                    }
                  },
                  "Newtonsoft.Json": {
                    "type": "Transitive",
                    "resolved": "13.0.3",
                    "contentHash": "HrC5BXdl00IP9zeV+0Z848QWPAoCr9P3bDEZguI+gkLcBKAOxix/tLEAAHC+UvDNPv4a2d18lOReHMOagPa+zQ=="
                  },
                  "SQLitePCLRaw.bundle_e_sqlite3": {
                    "type": "Transitive",
                    "resolved": "2.1.13",
                    "contentHash": "NlDixlC7uHmo+ojXRgkNvN0tQtVGpLDZ3x2OpgNj+G07VHh5cXSszFDrAiC4BpUiPwkcaeuQkMV9WQ123xVtmQ==",
                    "dependencies": {
                      "SQLitePCLRaw.lib.e_sqlite3": "2.1.13",
                      "SQLitePCLRaw.provider.e_sqlite3": "2.1.13"
                    }
                  },
                  "SQLitePCLRaw.core": {
                    "type": "Transitive",
                    "resolved": "2.1.13",
                    "contentHash": "2joofoinuHBFs0nMX3nKuXgxpXryRkCOdM2Ua6HBqQLLy6IW+5f28ZI+HAvMyF8vG7XfNz/lwfBfE6V4juru8Q=="
                  },
                  "SQLitePCLRaw.lib.e_sqlite3": {
                    "type": "Transitive",
                    "resolved": "2.1.13",
                    "contentHash": "D2UAuiGxFYBqeRXjjjGgvJ5UQifzn6wefmSqeKU7XcNGKLbJRP6X7kcPZ6opOBJGdCIlgbSxTvDpBbLvsN594A=="
                  },
                  "SQLitePCLRaw.provider.e_sqlite3": {
                    "type": "Transitive",
                    "resolved": "2.1.13",
                    "contentHash": "ZU60dSqoSNRe49KcQDUmOVgdRtjITOssVXFtiISs+zJXDI4D2Sndwpn2NOtzDQNPu0ZAvpoLPQ+GLKv6iEZn8Q==",
                    "dependencies": {
                      "SQLitePCLRaw.core": "2.1.13"
                    }
                  },
                  "xunit.abstractions": {
                    "type": "Transitive",
                    "resolved": "2.0.3",
                    "contentHash": "pot1I4YOxlWjIb5jmwvvQNbTrZ3lJQ+jUGkGjWE3hEFM0l5gOnBWS+H3qsex68s5cO52g+44vpGzhAt+42vwKg=="
                  },
                  "xunit.analyzers": {
                    "type": "Transitive",
                    "resolved": "1.18.0",
                    "contentHash": "OtFMHN8yqIcYP9wcVIgJrq01AfTxijjAqVDy/WeQVSyrDC1RzBWeQPztL49DN2syXRah8TYnfvk035s7L95EZQ=="
                  },
                  "xunit.assert": {
                    "type": "Transitive",
                    "resolved": "2.9.3",
                    "contentHash": "/Kq28fCE7MjOV42YLVRAJzRF0WmEqsmflm0cfpMjGtzQ2lR5mYVj1/i0Y8uDAOLczkL3/jArrwehfMD0YogMAA=="
                  },
                  "xunit.core": {
                    "type": "Transitive",
                    "resolved": "2.9.3",
                    "contentHash": "BiAEvqGvyme19wE0wTKdADH+NloYqikiU0mcnmiNyXaF9HyHmE6sr/3DC5vnBkgsWaE6yPyWszKSPSApWdRVeQ==",
                    "dependencies": {
                      "xunit.extensibility.core": "[2.9.3]",
                      "xunit.extensibility.execution": "[2.9.3]"
                    }
                  },
                  "xunit.extensibility.core": {
                    "type": "Transitive",
                    "resolved": "2.9.3",
                    "contentHash": "kf3si0YTn2a8J8eZNb+zFpwfoyvIrQ7ivNk5ZYA5yuYk1bEtMe4DxJ2CF/qsRgmEnDr7MnW1mxylBaHTZ4qErA==",
                    "dependencies": {
                      "xunit.abstractions": "2.0.3"
                    }
                  },
                  "xunit.extensibility.execution": {
                    "type": "Transitive",
                    "resolved": "2.9.3",
                    "contentHash": "yMb6vMESlSrE3Wfj7V6cjQ3S4TXdXpRqYeNEI3zsX31uTsGMJjEw6oD5F5u1cHnMptjhEECnmZSsPxB6ChZHDQ==",
                    "dependencies": {
                      "xunit.extensibility.core": "[2.9.3]"
                    }
                  },
                  "api": {
                    "type": "Project",
                    "dependencies": {
                      "Microsoft.Data.Sqlite": "[10.0.15, 10.0.15]"
                    }
                  }
                }
              }
            }
            """),
        new("backend/Api.Tests/TaskApiTests.cs", """
            using System.Net;
            using System.Net.Http.Json;
            using App;
            using Microsoft.AspNetCore.Hosting.Server;
            using Microsoft.AspNetCore.Hosting.Server.Features;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.DependencyInjection;
            using Xunit;

            public sealed class TaskApiTests
            {
                [Fact]
                public async Task CrudFilteringAndFilePersistenceFollowTheContract()
                {
                    var database = Path.Combine(Path.GetTempPath(), "aiplatform-" + Guid.NewGuid().ToString("N"), "tasks.db");
                    Directory.CreateDirectory(Path.GetDirectoryName(database)!);
                    var databaseDataSource = "Data Source=" + database;
                    try
                    {
                        var first = await StartAsync(databaseDataSource);
                        using (first.Client)
                        await using (first.App)
                        {
                            Assert.Empty(await first.Client.GetFromJsonAsync<TaskDto[]>("/api/tasks?status=all"));
                            using var created = await first.Client.PostAsJsonAsync("/api/tasks", new { title = "  Prepare tests  " });
                            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                            Assert.NotNull(created.Headers.Location);
                            var task = await created.Content.ReadFromJsonAsync<TaskDto>();
                            Assert.NotNull(task);
                            Assert.Equal("Prepare tests", task.Title);
                            Assert.False(task.Completed);

                            using var fetched = await first.Client.GetAsync($"/api/tasks/{task.Id:D}");
                            Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
                            using var updated = await first.Client.PutAsJsonAsync($"/api/tasks/{task.Id:D}",
                                new { title = task.Title, completed = true });
                            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
                            Assert.True((await updated.Content.ReadFromJsonAsync<TaskDto>())!.Completed);
                            Assert.Empty(await first.Client.GetFromJsonAsync<TaskDto[]>("/api/tasks?status=active"));
                            Assert.Single(await first.Client.GetFromJsonAsync<TaskDto[]>("/api/tasks?status=completed"));
                        }

                        var restarted = await StartAsync(databaseDataSource);
                        using (restarted.Client)
                        await using (restarted.App)
                        {
                            var persisted = Assert.Single(await restarted.Client.GetFromJsonAsync<TaskDto[]>("/api/tasks"));
                            using var deleted = await restarted.Client.DeleteAsync($"/api/tasks/{persisted.Id:D}");
                            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                            Assert.Equal(HttpStatusCode.NotFound,
                                (await restarted.Client.GetAsync($"/api/tasks/{persisted.Id:D}")).StatusCode);
                        }
                    }
                    finally
                    {
                        var directory = Path.GetDirectoryName(database)!;
                        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                    }
                }

                [Fact]
                public async Task InvalidStatusAndWhitespaceTitlesReturnBadRequest()
                {
                    var database = Path.Combine(Path.GetTempPath(), "aiplatform-" + Guid.NewGuid().ToString("N"), "tasks.db");
                    Directory.CreateDirectory(Path.GetDirectoryName(database)!);
                    var fixture = await StartAsync("Data Source=" + database);
                    using (fixture.Client)
                    await using (fixture.App)
                    {
                        Assert.Equal(HttpStatusCode.BadRequest,
                            (await fixture.Client.GetAsync("/api/tasks?status=unknown")).StatusCode);
                        Assert.Equal(HttpStatusCode.BadRequest,
                            (await fixture.Client.PostAsJsonAsync("/api/tasks", new { title = "   " })).StatusCode);
                    }
                    var directory = Path.GetDirectoryName(database)!;
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }

                private static async Task<(WebApplication App, HttpClient Client)> StartAsync(string databaseDataSource)
                {
                    var app = await ApiApplication.CreateAsync([], databaseDataSource);
                    app.Urls.Add("http://127.0.0.1:0");
                    await app.StartAsync();
                    var address = app.Services.GetRequiredService<IServer>().Features
                        .Get<IServerAddressesFeature>()!.Addresses.Single();
                    return (app, new HttpClient { BaseAddress = new Uri(address) });
                }
            }
            """),
        new("frontend/package.json", ReactTemplate.Files.Single(file => file.Path == "package.json").Content),
        new("frontend/package-lock.json", ReactTemplate.Files.Single(file => file.Path == "package-lock.json").Content),
        new("frontend/index.html", """
            <!doctype html>
            <html lang="fr"><head><meta charset="UTF-8" /><meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>Task Studio</title></head><body><div id="root"></div><script type="module" src="/src/main.tsx"></script></body></html>
            """),
        new("frontend/tsconfig.json", """
            {
              "compilerOptions": {
                "target": "ES2022", "lib": ["ES2022", "DOM", "DOM.Iterable"],
                "module": "ESNext", "moduleResolution": "bundler", "jsx": "react-jsx",
                "strict": true, "noEmit": true, "skipLibCheck": true,
                "esModuleInterop": true, "resolveJsonModule": true,
                "types": ["vitest/globals", "@testing-library/jest-dom", "node", "vite/client"]
              },
              "include": ["src", "tests", "integration", "vite.config.ts", "vite.integration.config.ts", "test-setup.ts"]
            }
            """),
        new("frontend/vite.config.ts", """
            import { defineConfig } from 'vitest/config'
            import react from '@vitejs/plugin-react'
            export default defineConfig({
              plugins: [react()],
              server: { proxy: { '/api': { target: process.env.VITE_API_PROXY_TARGET ?? 'http://localhost:5080' } } },
              test: { environment: 'jsdom', globals: true, setupFiles: ['./test-setup.ts'],
                include: ['tests/**/*.test.ts', 'tests/**/*.test.tsx'], maxWorkers: 1, fileParallelism: false }
            })
            """),
        new("frontend/vite.integration.config.ts", """
            import { defineConfig } from 'vitest/config'
            import react from '@vitejs/plugin-react'
            export default defineConfig({
              plugins: [react()],
              test: { environment: 'node', globals: true,
                include: ['integration/**/*.test.ts'], maxWorkers: 1, fileParallelism: false, testTimeout: 30000 }
            })
            """),
        new("frontend/test-setup.ts", """
            import '@testing-library/jest-dom/vitest'
            import { cleanup } from '@testing-library/react'
            import { afterEach } from 'vitest'
            afterEach(cleanup)
            """),
        new("frontend/src/main.tsx", """
            import { StrictMode } from 'react'
            import { createRoot } from 'react-dom/client'
            import App from './App'
            import './styles.css'
            createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>)
            """),
        new("frontend/src/types.ts", """
            export interface Task { id: string; title: string; completed: boolean }
            export type TaskFilter = 'all' | 'active' | 'completed'
            export interface CreateTaskRequest { title: string }
            export interface UpdateTaskRequest { title: string; completed: boolean }
            """),
        new("frontend/src/api.ts", """
            import type { CreateTaskRequest, Task, TaskFilter, UpdateTaskRequest } from './types'

            const configuredBaseUrl = import.meta.env.VITE_API_BASE_URL ?? ''

            export interface TaskApi {
              list(filter?: TaskFilter): Promise<Task[]>
              get(id: string): Promise<Task>
              create(request: CreateTaskRequest): Promise<Task>
              update(id: string, request: UpdateTaskRequest): Promise<Task>
              delete(id: string): Promise<void>
            }

            export function createTaskApi(baseUrl = configuredBaseUrl, fetcher: typeof fetch = fetch): TaskApi {
              async function read<T>(path: string, init?: RequestInit): Promise<T> {
                const response = await fetcher(`${baseUrl}${path}`, {
                  ...init, headers: { 'Content-Type': 'application/json', ...init?.headers }
                })
                if (!response.ok) throw new Error(`Task API request failed (${response.status}).`)
                return response.status === 204 ? undefined as T : await response.json() as T
              }
              return {
                list: filter => read<Task[]>(`/api/tasks?status=${encodeURIComponent(filter ?? 'all')}`),
                get: id => read<Task>(`/api/tasks/${encodeURIComponent(id)}`),
                create: request => read<Task>('/api/tasks', { method: 'POST', body: JSON.stringify(request) }),
                update: (id, request) => read<Task>(`/api/tasks/${encodeURIComponent(id)}`,
                  { method: 'PUT', body: JSON.stringify(request) }),
                delete: async id => { await read<void>(`/api/tasks/${encodeURIComponent(id)}`, { method: 'DELETE' }) }
              }
            }

            export const taskApi = createTaskApi()
            """),
        new("frontend/src/TaskItem.tsx", """
            import type { Task } from './types'

            interface Props {
              task: Task
              onToggle: (task: Task) => void
              onDelete: (task: Task) => void
            }

            export default function TaskItem({ task, onToggle, onDelete }: Props) {
              return <li>
                <label>
                  <input type="checkbox" checked={task.completed} onChange={() => onToggle(task)} />
                  <span>{task.title}</span>
                </label>
                <button type="button" onClick={() => onDelete(task)} aria-label={`Supprimer ${task.title}`}>
                  Supprimer
                </button>
              </li>
            }
            """),
        new("frontend/src/App.tsx", """
            import { useEffect, useState, type FormEvent } from 'react'
            import { taskApi } from './api'
            import TaskItem from './TaskItem'
            import type { Task, TaskFilter } from './types'

            export default function App() {
              const [tasks, setTasks] = useState<Task[]>([])
              const [title, setTitle] = useState('')
              const [filter, setFilter] = useState<TaskFilter>('all')
              const [error, setError] = useState<string | null>(null)
              const [busy, setBusy] = useState(false)

              async function refresh(nextFilter = filter) {
                try { setError(null); setTasks(await taskApi.list(nextFilter)) }
                catch (failure) { setError(failure instanceof Error ? failure.message : 'Chargement impossible.') }
              }

              useEffect(() => { void refresh(filter) }, [filter])

              async function addTask(event: FormEvent<HTMLFormElement>) {
                event.preventDefault()
                const normalized = title.trim()
                if (!normalized || busy) return
                setBusy(true)
                try { await taskApi.create({ title: normalized }); setTitle(''); await refresh() }
                catch (failure) { setError(failure instanceof Error ? failure.message : 'Ajout impossible.') }
                finally { setBusy(false) }
              }

              async function updateTask(task: Task, completed: boolean) {
                setBusy(true)
                try { await taskApi.update(task.id, { title: task.title, completed }); await refresh() }
                catch (failure) { setError(failure instanceof Error ? failure.message : 'Modification impossible.') }
                finally { setBusy(false) }
              }

              async function deleteTask(task: Task) {
                setBusy(true)
                try { await taskApi.delete(task.id); await refresh() }
                catch (failure) { setError(failure instanceof Error ? failure.message : 'Suppression impossible.') }
                finally { setBusy(false) }
              }

              return <main>
                <h1>Mes tâches</h1>
                <form onSubmit={event => void addTask(event)}>
                  <label htmlFor="new-task">Nouvelle tâche</label>
                  <input id="new-task" value={title} onChange={event => setTitle(event.target.value)} maxLength={200} />
                  <button type="submit" disabled={busy || !title.trim()}>Ajouter</button>
                </form>
                <label htmlFor="task-filter">Filtrer les tâches</label>
                <select id="task-filter" value={filter} onChange={event => setFilter(event.target.value as TaskFilter)}>
                  <option value="all">Toutes</option><option value="active">Actives</option>
                  <option value="completed">Terminées</option>
                </select>
                {error && <p role="alert">{error}</p>}
                <ul>{tasks.map(task => <TaskItem key={task.id} task={task}
                  onToggle={item => void updateTask(item, !item.completed)}
                  onDelete={item => void deleteTask(item)} />)}</ul>
              </main>
            }
            """),
        new("frontend/src/styles.css", """
            :root { font-family: system-ui, sans-serif; color: #172554; background: #eff6ff; }
            * { box-sizing: border-box; }
            body { margin: 0; padding: 1rem; }
            main { max-width: 48rem; margin: 2rem auto; padding: 1.5rem; border-radius: 1rem; background: white; }
            form, li, label { display: flex; gap: .75rem; align-items: center; flex-wrap: wrap; }
            input:not([type=checkbox]) { flex: 1; min-width: 0; padding: .75rem; }
            button, select { padding: .65rem; cursor: pointer; }
            ul { padding: 0; list-style: none; }
            li { padding: .75rem 0; justify-content: space-between; border-bottom: 1px solid #dbeafe; overflow-wrap: anywhere; }
            input:checked + span { text-decoration: line-through; }
            @media (max-width: 480px) { main { padding: 1rem; margin: 0 auto; } form { align-items: stretch; flex-direction: column; } }
            """),
        new("frontend/tests/App.test.tsx", """
            import { fireEvent, render, screen, waitFor } from '@testing-library/react'
            import { beforeEach, expect, test, vi } from 'vitest'
            import { taskApi } from '../src/api'
            import App from '../src/App'
            import type { Task } from '../src/types'

            vi.mock('../src/api', () => ({
              taskApi: { list: vi.fn(), get: vi.fn(), create: vi.fn(), update: vi.fn(), delete: vi.fn() }
            }))

            const first: Task = { id: 'one', title: 'Alpha', completed: false }
            const second: Task = { id: 'two', title: 'Beta', completed: true }

            beforeEach(() => {
              vi.resetAllMocks()
              vi.mocked(taskApi.list).mockResolvedValue([first, second])
              vi.mocked(taskApi.create).mockImplementation(async request =>
                ({ id: 'three', title: request.title, completed: false }))
              vi.mocked(taskApi.update).mockImplementation(async (id, request) => ({ id, ...request }))
              vi.mocked(taskApi.delete).mockResolvedValue()
            })

            test('creates a task through the API client and rejects blank input', async () => {
              render(<App />)
              const input = screen.getByRole('textbox', { name: 'Nouvelle tâche' })
              fireEvent.change(input, { target: { value: '  Gamma  ' } })
              fireEvent.click(screen.getByRole('button', { name: 'Ajouter' }))
              await waitFor(() => expect(taskApi.create).toHaveBeenCalledWith({ title: 'Gamma' }))
              fireEvent.change(input, { target: { value: '   ' } })
              fireEvent.click(screen.getByRole('button', { name: 'Ajouter' }))
              expect(taskApi.create).toHaveBeenCalledTimes(1)
            })

            test('updates completion, deletes tasks, and requests server-side filters', async () => {
              render(<App />)
              await screen.findByText('Alpha')
              fireEvent.click(screen.getByRole('checkbox', { name: 'Alpha' }))
              await waitFor(() => expect(taskApi.update).toHaveBeenCalledWith('one',
                { title: 'Alpha', completed: true }))
              fireEvent.click(screen.getByRole('button', { name: 'Supprimer Alpha' }))
              await waitFor(() => expect(taskApi.delete).toHaveBeenCalledWith('one'))
              fireEvent.change(screen.getByRole('combobox', { name: 'Filtrer les tâches' }),
                { target: { value: 'completed' } })
              await waitFor(() => expect(taskApi.list).toHaveBeenLastCalledWith('completed'))
            })
            """),
        new("frontend/integration/api.integration.test.ts", """
            import { expect, test } from 'vitest'
            import { createTaskApi } from '../src/api'

            test('the frontend API client performs real HTTP CRUD against the running ASP.NET API', async () => {
              const baseUrl = process.env.FULLSTACK_API_BASE_URL
              if (!baseUrl) throw new Error('FULLSTACK_API_BASE_URL must point to the isolated local API.')
              const api = createTaskApi(baseUrl)
              const title = `Integration ${crypto.randomUUID()}`
              const created = await api.create({ title })
              expect(created.title).toBe(title)
              expect(created.completed).toBe(false)
              expect((await api.get(created.id)).id).toBe(created.id)
              const updated = await api.update(created.id, { title, completed: true })
              expect(updated.completed).toBe(true)
              expect((await api.list('completed')).some(task => task.id === created.id)).toBe(true)
              expect((await api.list('active')).some(task => task.id === created.id)).toBe(false)
              await api.delete(created.id)
              expect((await api.list('all')).some(task => task.id === created.id)).toBe(false)
            })
            """),
        new("contracts/openapi.json", """
            {
              "openapi": "3.1.0",
              "info": { "title": "Task API", "version": "1.0.0" },
              "paths": {
                "/api/tasks": {
                  "get": {
                    "operationId": "listTasks",
                    "parameters": [{ "name": "status", "in": "query", "required": false,
                      "schema": { "type": "string", "enum": ["all", "active", "completed"], "default": "all" } }],
                    "responses": { "200": { "description": "Tasks matching the status filter",
                      "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Task" } } } } },
                      "400": { "$ref": "#/components/responses/BadRequest" } }
                  },
                  "post": {
                    "operationId": "createTask",
                    "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/CreateTask" } } } },
                    "responses": { "201": { "description": "Task created", "headers": { "Location": { "schema": { "type": "string" } } },
                      "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Task" } } } },
                      "400": { "$ref": "#/components/responses/BadRequest" } }
                  }
                },
                "/api/tasks/{id}": {
                  "parameters": [{ "name": "id", "in": "path", "required": true,
                    "schema": { "type": "string", "format": "uuid" } }],
                  "get": { "operationId": "getTask", "responses": {
                    "200": { "description": "Task found", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Task" } } } },
                    "404": { "$ref": "#/components/responses/NotFound" } } },
                  "put": { "operationId": "updateTask",
                    "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/UpdateTask" } } } },
                    "responses": {
                      "200": { "description": "Task updated", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Task" } } } },
                      "400": { "$ref": "#/components/responses/BadRequest" }, "404": { "$ref": "#/components/responses/NotFound" } } },
                  "delete": { "operationId": "deleteTask", "responses": {
                    "204": { "description": "Task deleted" }, "404": { "$ref": "#/components/responses/NotFound" } } }
                }
              },
              "components": {
                "schemas": {
                  "Task": { "type": "object", "required": ["id", "title", "completed"], "properties": {
                    "id": { "type": "string", "format": "uuid" }, "title": { "type": "string", "maxLength": 200 },
                    "completed": { "type": "boolean" } } },
                  "CreateTask": { "type": "object", "required": ["title"], "properties": {
                    "title": { "type": "string", "minLength": 1, "maxLength": 200 } } },
                  "UpdateTask": { "type": "object", "required": ["title", "completed"], "properties": {
                    "title": { "type": "string", "minLength": 1, "maxLength": 200 }, "completed": { "type": "boolean" } } },
                  "ProblemDetails": { "type": "object", "properties": {
                    "type": { "type": "string" }, "title": { "type": "string" }, "status": { "type": "integer" },
                    "detail": { "type": "string" }, "instance": { "type": "string" } } }
                },
                "responses": {
                  "BadRequest": { "description": "Invalid request or status filter",
                    "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/ProblemDetails" } } } },
                  "NotFound": { "description": "Task does not exist" }
                }
              }
            }
            """),
        new("README.md", """
            # Full-Stack React + ASP.NET Core template v1

            This trusted template implements the fixed contract in `contracts/openapi.json`.
            BackendAgent owns the SQLite repository, service and minimal API endpoint files.
            FrontendAgent owns the typed HTTP client and React task UI. Both receive the exact
            same contract; routes and identifier types must not diverge from it.

            The API uses `Microsoft.Data.Sqlite` 10.0.15 and stores local data in `data/tasks.db`.
            Override `ConnectionStrings__Default` to select another database. Do not place secrets
            in prompts or generated source. The project is untrusted; never execute it automatically.
            Run the API on `http://localhost:5080` and Vite on its default port; Vite proxies `/api`
            to the API. For another deployment, set `VITE_API_BASE_URL` to the API origin.

            Offline validation requires explicit human approval. Docker runs .NET Release build,
            xUnit, TypeScript, Vite, Vitest and a real HTTP integration test on container loopback.
            External networking is disabled; tests use temporary SQLite files and no production data.
            """),
        new(".gitignore", """
            bin/
            obj/
            node_modules/
            dist/
            coverage/
            data/
            *.db
            """)
    ];
}
