using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Ccee.PldApp.Application;
using Ccee.PldApp.Application.Abstractions;
using Ccee.PldApp.Application.Exceptions;
using Ccee.PldApp.Domain;
using Ccee.PldApp.Infrastructure.Clients;
using Ccee.PldApp.Infrastructure.Configuration;
using Ccee.PldApp.Infrastructure.Persistence;
using Serilog;

const string DashboardAuthCookieName = "pld_dashboard_session";
var allowedSubmercados = new HashSet<string>(StringComparer.Ordinal)
{
    "SUDESTE",
    "SUL",
    "NORDESTE",
    "NORTE"
};

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("Configuration/SecurityConfig.json", optional: true, reloadOnChange: true);

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "Ccee.PldApp.Api");
});

var gatewayOptions = LoadGatewayOptions(builder.Configuration);
if (string.IsNullOrWhiteSpace(gatewayOptions.BrowserExecutablePath))
{
    var foundPath = gatewayOptions.AutoDetectBrowserPath();
    if (!string.IsNullOrWhiteSpace(foundPath))
        gatewayOptions.BrowserExecutablePath = foundPath;
}

var dashboardAuthOptions = LoadDashboardAuthOptions(builder.Configuration);
var uiOptions = LoadUiOptions(builder.Configuration);
var loginRequestJsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};
var appStartedAtUtc = DateTimeOffset.UtcNow;
var requestMetricsStorageDirectory = Path.Combine(builder.Environment.ContentRootPath, "logs", "metrics");
var requestMetrics = new RequestMetricsCollector(
    maxRecentEntries: 300,
    storageDirectoryPath: requestMetricsStorageDirectory,
    retentionDays: 30);

builder.Services.AddSingleton(gatewayOptions);
builder.Services.AddSingleton(dashboardAuthOptions);
builder.Services.AddSingleton(uiOptions);
builder.Services.AddSingleton(new ConcurrentDictionary<string, DashboardSession>(StringComparer.Ordinal));
builder.Services.AddSingleton(new ConcurrentDictionary<int, string>());
builder.Services.AddSingleton<SqliteDatabaseInitializer>();
builder.Services.AddSingleton<IPldCacheRepository, SqlitePldCacheRepository>();
builder.Services.AddSingleton<ICceePldSource, CceePldSource>();
builder.Services.AddSingleton<GetPldDataUseCase>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<JsonOptions>(opts =>
{
    opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowUIClients", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

if (app.Environment.IsProduction())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline' cdn.jsdelivr.net; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

app.UseCors("AllowUIClients");
app.Use(async (context, next) =>
{
    if (context.Request.Path.Equals("/dashboard.html", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/admin");
        return;
    }

    if (context.Request.Path.Equals("/admin.html", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/admin");
        return;
    }

    if (context.Request.Path.Equals("/login.html", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect("/login");
        return;
    }

    await next();
});
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    var stopwatch = Stopwatch.StartNew();
    try
    {
        await next();
    }
    finally
    {
        stopwatch.Stop();
        requestMetrics.Record(context, stopwatch.Elapsed);
    }
});

var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Aplicacao iniciando...");

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "CCEE PLD Gateway API v1");
    c.RoutePrefix = "swagger";
});

if (!string.IsNullOrWhiteSpace(gatewayOptions.BrowserExecutablePath) && File.Exists(gatewayOptions.BrowserExecutablePath))
{
    logger.LogInformation("Browser encontrado em: {BrowserExecutablePath}", gatewayOptions.BrowserExecutablePath);
}
else if (!gatewayOptions.DownloadBrowserIfMissing)
{
    logger.LogCritical("Erro critico: nenhum browser configurado e DownloadBrowserIfMissing=false.");
    logger.LogCritical("Configure PldGateway:BrowserExecutablePath, instale Chrome/Chromium, ou habilite DownloadBrowserIfMissing.");
    Environment.Exit(1);
}
else
{
    logger.LogWarning("Nenhum browser configurado. DownloadBrowserIfMissing=true e o download ocorrera sob demanda.");
}

if (dashboardAuthOptions.RequireAuthentication && dashboardAuthOptions.Users.Count == 0)
{
    logger.LogCritical("Erro critico: autenticacao do painel admin habilitada sem usuarios configurados.");
    Environment.Exit(1);
}

if (gatewayOptions.ResourceIdsByYear.Count == 0 && !gatewayOptions.EnableResourceIdAutoDiscovery)
{
    logger.LogCritical("Erro critico: nenhum resource_id foi configurado em PldGateway:ResourceIdsByYear.");
    logger.LogCritical("Ative PldGateway:EnableResourceIdAutoDiscovery=true ou configure os anos manualmente.");
    Environment.Exit(1);
}
else if (gatewayOptions.ResourceIdsByYear.Count == 0)
{
    logger.LogWarning("Nenhum resource_id configurado. A aplicacao tentara descobrir automaticamente via catalogo da CCEE.");
}

await app.Services.GetRequiredService<SqliteDatabaseInitializer>().InitializeAsync();
logger.LogInformation("Banco de dados inicializado");

app.MapGet("/", (PldGatewayOptions options) => Results.Ok(new
{
    service = "ccee-pld-gateway",
    status = "online",
    databasePath = options.ResolveDatabasePath(),
    endpoints = new[]
    {
        "/swagger",
        "/login",
        "/admin",
        "/health",
        "/api/pld?date=2026-04-11&submercado=SUL",
        "/api/pld?submercado=SUDESTE&limit=24",
        "/api/admin/overview"
    }
}))
.WithName("GetIndex")
.Produces(StatusCodes.Status200OK);

app.MapGet("/health", (PldGatewayOptions options) => Results.Ok(new
{
    status = "ok",
    databasePath = options.ResolveDatabasePath()
}))
.WithName("GetHealth")
.Produces(StatusCodes.Status200OK);

app.MapGet("/login", (HttpContext httpContext, DashboardAuthOptions authOptions, UiOptions localUiOptions, IWebHostEnvironment environment, ConcurrentDictionary<string, DashboardSession> sessions, ILogger<Program> localLogger) =>
{
    if (!authOptions.RequireAuthentication)
        return Results.Redirect("/admin");

    if (TryGetAuthenticatedUser(httpContext, sessions, out _))
        return Results.Redirect("/admin");

    return ServeHtml("login.html", localUiOptions, environment, localLogger);
})
.WithName("GetLogin")
.Produces(StatusCodes.Status200OK);

app.MapPost("/api/auth/login", async (HttpContext httpContext, DashboardAuthOptions authOptions, ConcurrentDictionary<string, DashboardSession> sessions, CancellationToken cancellationToken) =>
{
    if (!authOptions.RequireAuthentication)
        return Results.Ok(new { message = "Autenticacao do painel admin esta desabilitada." });

    LoginRequest? request;
    try
    {
        request = await JsonSerializer.DeserializeAsync<LoginRequest>(
            httpContext.Request.Body,
            loginRequestJsonOptions,
            cancellationToken);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { message = "Payload de login invalido." });
    }

    if (request is null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { message = "Usuario e senha sao obrigatorios." });

    CleanupExpiredSessions(sessions);

    var user = authOptions.Users.FirstOrDefault(candidate =>
        string.Equals(candidate.Username, request.Username, StringComparison.Ordinal) &&
        string.Equals(candidate.Password, request.Password, StringComparison.Ordinal));

    if (user is null)
        return Results.Unauthorized();

    var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, authOptions.SessionTimeoutMinutes));
    var token = CreateSessionToken();
    sessions[token] = new DashboardSession(user.Username, expiresAtUtc);

    httpContext.Response.Cookies.Append(DashboardAuthCookieName, token, new CookieOptions
    {
        HttpOnly = true,
        Secure = httpContext.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Expires = expiresAtUtc.UtcDateTime,
        Path = "/"
    });

    return Results.Ok(new
    {
        username = user.Username,
        expiresAtUtc
    });
})
.WithName("PostLogin")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized);

app.MapPost("/api/auth/logout", (HttpContext httpContext, ConcurrentDictionary<string, DashboardSession> sessions) =>
{
    if (httpContext.Request.Cookies.TryGetValue(DashboardAuthCookieName, out var token) && !string.IsNullOrWhiteSpace(token))
        sessions.TryRemove(token, out _);

    httpContext.Response.Cookies.Delete(DashboardAuthCookieName, new CookieOptions
    {
        HttpOnly = true,
        Secure = httpContext.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/"
    });

    return Results.Ok(new { success = true });
})
.WithName("PostLogout")
.Produces(StatusCodes.Status200OK);

app.MapGet("/api/auth/me", (HttpContext httpContext, DashboardAuthOptions authOptions, ConcurrentDictionary<string, DashboardSession> sessions) =>
{
    if (!authOptions.RequireAuthentication)
        return Results.Ok(new { authenticated = false, authenticationDisabled = true });

    if (!TryGetAuthenticatedUser(httpContext, sessions, out var username))
        return Results.Unauthorized();

    return Results.Ok(new { authenticated = true, username });
})
.WithName("GetAuthMe")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

app.MapGet("/admin", (HttpContext httpContext, DashboardAuthOptions authOptions, UiOptions localUiOptions, IWebHostEnvironment environment, ConcurrentDictionary<string, DashboardSession> sessions, ILogger<Program> localLogger) =>
{
    if (authOptions.RequireAuthentication && !TryGetAuthenticatedUser(httpContext, sessions, out _))
        return Results.Redirect("/login");

    return ServeHtml("admin.html", localUiOptions, environment, localLogger);
})
.WithName("GetAdmin")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status302Found)
.Produces(StatusCodes.Status404NotFound);

app.MapGet("/dashboard", () => Results.Redirect("/admin"))
.WithName("GetDashboardRedirect")
.Produces(StatusCodes.Status302Found);

app.MapGet("/api/pld", async (
    [FromQuery(Name = "date")] string? date,
    [FromQuery(Name = "submercado")] string? submercado,
    [FromQuery(Name = "limit")] int? limit,
    HttpContext httpContext,
    GetPldDataUseCase useCase,
    PldGatewayOptions options,
    ConcurrentDictionary<int, string> discoveredResourceIds,
    IWebHostEnvironment environment,
    ILogger<Program> localLogger,
    CancellationToken cancellationToken) =>
{
    if (!TryBuildQuery(date, submercado, limit, httpContext.Request.Query, allowedSubmercados, out var query, out var problem))
    {
        localLogger.LogWarning("Parametros invalidos na requisicao: {Problem}", problem.Detail);
        return Results.BadRequest(problem);
    }

    var resourceResolution = await ResolveResourceIdAsync(
        query.Dia,
        options,
        discoveredResourceIds,
        environment.ContentRootPath,
        localLogger,
        cancellationToken);

    if (!resourceResolution.Success)
    {
        var resolutionProblem = CreateValidationProblem("Configuracao invalida", resourceResolution.ErrorMessage ?? "Falha ao resolver resource_id.");
        localLogger.LogWarning("Falha ao resolver resource_id: {Problem}", resolutionProblem.Detail);
        return Results.BadRequest(resolutionProblem);
    }

    query = query with { ResourceId = resourceResolution.ResourceId! };

    localLogger.LogInformation(
        "Consultando PLD: Dia={Dia}, Submercado={Submercado}, Limit={Limit}, ResourceId={ResourceId}",
        query.Dia?.ToString("yyyy-MM-dd") ?? "todos",
        query.Submercado ?? "todos",
        query.Limit,
        query.ResourceId);

    try
    {
        var result = await useCase.ExecuteAsync(query, cancellationToken);
        return Results.Ok(result);
    }
    catch (PldQueryNotFoundException ex)
    {
        return Results.NotFound(new ProblemDetails
        {
            Title = "Nenhum dado encontrado",
            Detail = ex.Message,
            Status = StatusCodes.Status404NotFound
        });
    }
    catch (CceeGatewayException ex)
    {
        localLogger.LogError(ex, "Falha ao consultar a CCEE");
        return Results.Problem(
            title: "Falha ao consultar a CCEE",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ProblemDetails
        {
            Title = "Parametro invalido",
            Detail = ex.Message,
            Status = StatusCodes.Status400BadRequest
        });
    }
})
.WithName("GetPldData")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status502BadGateway);

app.MapGet("/api/admin/overview", (
    HttpContext httpContext,
    DashboardAuthOptions authOptions,
    ConcurrentDictionary<string, DashboardSession> sessions,
    PldGatewayOptions options,
    UiOptions localUiOptions,
    IWebHostEnvironment environment) =>
{
    if (!TryAuthorizeAdminRequest(httpContext, authOptions, sessions, out var authenticatedUser))
        return Results.Unauthorized();

    var databasePath = options.ResolveDatabasePath();
    var databaseFileInfo = File.Exists(databasePath) ? new FileInfo(databasePath) : null;
    var uptime = DateTimeOffset.UtcNow - appStartedAtUtc;
    var configuredResourceYears = options.ResourceIdsByYear.Keys.OrderBy(year => year).ToArray();

    return Results.Ok(new
    {
        service = "ccee-pld-gateway",
        environment = environment.EnvironmentName,
        startedAtUtc = appStartedAtUtc,
        uptimeSeconds = (long)Math.Max(0, uptime.TotalSeconds),
        machineName = Environment.MachineName,
        processId = Environment.ProcessId,
        authenticatedUser,
        activeSessions = sessions.Count,
        authenticationRequired = authOptions.RequireAuthentication,
        database = new
        {
            path = databasePath,
            exists = databaseFileInfo is not null,
            sizeBytes = databaseFileInfo?.Length ?? 0L
        },
        browser = new
        {
            executablePath = options.BrowserExecutablePath,
            exists = !string.IsNullOrWhiteSpace(options.BrowserExecutablePath) && File.Exists(options.BrowserExecutablePath)
        },
        pldGateway = new
        {
            options.BaseUrl,
            options.HttpTimeoutSeconds,
            options.HttpMaxAttempts,
            options.EnableResourceIdAutoDiscovery,
            configuredResourceYearsCount = configuredResourceYears.Length,
            latestConfiguredResourceYear = configuredResourceYears.Length == 0
                ? (int?)null
                : configuredResourceYears[^1]
        },
        ui = new
        {
            pagesPath = localUiOptions.PagesPath
        }
    });
})
.WithName("GetAdminOverview")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

app.MapGet("/api/admin/metrics", (
    HttpContext httpContext,
    DashboardAuthOptions authOptions,
    ConcurrentDictionary<string, DashboardSession> sessions,
    [FromQuery] DateTimeOffset? fromUtc,
    [FromQuery] DateTimeOffset? toUtc,
    [FromQuery] string? method,
    [FromQuery] string? pathContains,
    [FromQuery] int? statusCode) =>
{
    if (!TryAuthorizeAdminRequest(httpContext, authOptions, sessions, out _))
        return Results.Unauthorized();

    if (!TryBuildRequestMetricsQuery(fromUtc, toUtc, method, pathContains, statusCode, out var query, out var problem))
        return Results.BadRequest(problem);

    var snapshot = requestMetrics.CreateSnapshot(query);
    return Results.Ok(new
    {
        startedAtUtc = appStartedAtUtc,
        uptimeSeconds = (long)Math.Max(0, (DateTimeOffset.UtcNow - appStartedAtUtc).TotalSeconds),
        fromUtc = query.FromUtc,
        toUtc = query.ToUtc,
        filters = new
        {
            query.Method,
            query.PathContains,
            query.StatusCode
        },
        snapshot.TotalRequests,
        snapshot.TotalErrors,
        snapshot.AverageDurationMs,
        snapshot.MaxDurationMs,
        byRoute = snapshot.ByRoute,
        recentRequests = snapshot.RecentRequests
    });
})
.WithName("GetAdminMetrics")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status401Unauthorized);

app.MapGet("/api/admin/logs/files", (
    HttpContext httpContext,
    DashboardAuthOptions authOptions,
    ConcurrentDictionary<string, DashboardSession> sessions,
    IWebHostEnvironment environment) =>
{
    if (!TryAuthorizeAdminRequest(httpContext, authOptions, sessions, out _))
        return Results.Unauthorized();

    var logsDirectory = ResolveLogsDirectory(environment.ContentRootPath);
    var files = GetLogFiles(logsDirectory)
        .Select(file => new
        {
            file.Name,
            file.Length,
            file.LastWriteTimeUtc
        })
        .ToArray();

    return Results.Ok(new
    {
        logsDirectory,
        files
    });
})
.WithName("GetAdminLogFiles")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

app.MapGet("/api/admin/logs", (
    HttpContext httpContext,
    DashboardAuthOptions authOptions,
    ConcurrentDictionary<string, DashboardSession> sessions,
    IWebHostEnvironment environment,
    [FromQuery] string? file,
    [FromQuery] int? lines) =>
{
    if (!TryAuthorizeAdminRequest(httpContext, authOptions, sessions, out _))
        return Results.Unauthorized();

    var maxLines = Math.Clamp(lines ?? 200, 20, 2000);
    var logsDirectory = ResolveLogsDirectory(environment.ContentRootPath);
    var availableFiles = GetLogFiles(logsDirectory).ToArray();
    if (availableFiles.Length == 0)
    {
        return Results.NotFound(new ProblemDetails
        {
            Title = "Logs nao encontrados",
            Detail = $"Nenhum arquivo de log foi encontrado em '{logsDirectory}'.",
            Status = StatusCodes.Status404NotFound
        });
    }

    FileInfo? selectedFile;
    if (string.IsNullOrWhiteSpace(file))
    {
        selectedFile = availableFiles[0];
    }
    else
    {
        var requestedFileName = Path.GetFileName(file.Trim());
        selectedFile = availableFiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, requestedFileName, StringComparison.OrdinalIgnoreCase));

        if (selectedFile is null)
        {
            return Results.BadRequest(new ProblemDetails
            {
                Title = "Arquivo de log invalido",
                Detail = $"Arquivo de log '{requestedFileName}' nao encontrado.",
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    IReadOnlyList<string> contentLines;
    try
    {
        contentLines = ReadLastLines(selectedFile!.FullName, maxLines);
    }
    catch (IOException)
    {
        return Results.Problem(
            title: "Arquivo de log em uso",
            detail: "O arquivo de log esta em uso no momento. Tente novamente em alguns segundos.",
            statusCode: StatusCodes.Status423Locked);
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Problem(
            title: "Sem acesso ao arquivo de log",
            detail: "Nao foi possivel ler o arquivo de log devido a permissoes de acesso.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    return Results.Ok(new
    {
        logsDirectory,
        selectedFile = selectedFile.Name,
        selectedFile.LastWriteTimeUtc,
        requestedLines = maxLines,
        returnedLines = contentLines.Count,
        lines = contentLines
    });
})
.WithName("GetAdminLogContent")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status400BadRequest)
.Produces(StatusCodes.Status403Forbidden)
.Produces(StatusCodes.Status423Locked)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound);

app.MapGet("/api/admin/configs", (
    HttpContext httpContext,
    DashboardAuthOptions authOptions,
    ConcurrentDictionary<string, DashboardSession> sessions,
    IWebHostEnvironment environment) =>
{
    if (!TryAuthorizeAdminRequest(httpContext, authOptions, sessions, out _))
        return Results.Unauthorized();

    var targets = new[]
    {
        "appsettings.json",
        Path.Combine("Configuration", "SecurityConfig.json"),
        Path.Combine("Properties", "launchSettings.json")
    };

    var files = new List<object>();

    foreach (var relativePath in targets)
    {
        var fullPath = Path.Combine(environment.ContentRootPath, relativePath);
        if (!File.Exists(fullPath))
            continue;

        var rawContent = File.ReadAllText(fullPath);
        var sanitizedContent = SanitizeConfigContent(rawContent);

        files.Add(new
        {
            fileName = Path.GetFileName(relativePath),
            relativePath = relativePath.Replace('\\', '/'),
            content = sanitizedContent
        });
    }

    return Results.Ok(new
    {
        generatedAtUtc = DateTimeOffset.UtcNow,
        files
    });
})
.WithName("GetAdminConfigs")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized);

app.MapGet("/api/admin/cache", async (
    HttpContext httpContext,
    DashboardAuthOptions authOptions,
    ConcurrentDictionary<string, DashboardSession> sessions,
    PldGatewayOptions options,
    [FromQuery] int? limit,
    CancellationToken cancellationToken) =>
{
    if (!TryAuthorizeAdminRequest(httpContext, authOptions, sessions, out _))
        return Results.Unauthorized();

    var databasePath = options.ResolveDatabasePath();
    if (!File.Exists(databasePath))
    {
        return Results.NotFound(new ProblemDetails
        {
            Title = "Cache nao encontrado",
            Detail = $"Arquivo de cache nao encontrado: {databasePath}",
            Status = StatusCodes.Status404NotFound
        });
    }

    var queryLimit = Math.Clamp(limit ?? 50, 1, 500);
    var databaseInfo = new FileInfo(databasePath);

    await using var connection = new SqliteConnection($"Data Source={databasePath}");
    await connection.OpenAsync(cancellationToken);

    var queryCount = await ExecuteSqliteCountAsync(connection, "SELECT COUNT(*) FROM pld_query_cache;", cancellationToken);
    var recordCount = await ExecuteSqliteCountAsync(connection, "SELECT COUNT(*) FROM pld_record_cache;", cancellationToken);

    string? oldestRetrievedAtUtc;
    string? latestRetrievedAtUtc;
    await using (var boundsCommand = connection.CreateCommand())
    {
        boundsCommand.CommandText = "SELECT MIN(retrieved_at_utc), MAX(retrieved_at_utc) FROM pld_query_cache;";
        await using var reader = await boundsCommand.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        oldestRetrievedAtUtc = reader.IsDBNull(0) ? null : reader.GetString(0);
        latestRetrievedAtUtc = reader.IsDBNull(1) ? null : reader.GetString(1);
    }

    var recentQueries = new List<object>();
    await using (var recentCommand = connection.CreateCommand())
    {
        recentCommand.CommandText =
            """
            SELECT query_key, resource_id, day, submarket, limit_value, retrieved_at_utc
            FROM pld_query_cache
            ORDER BY retrieved_at_utc DESC
            LIMIT $limit;
            """;
        recentCommand.Parameters.AddWithValue("$limit", queryLimit);

        await using var recentReader = await recentCommand.ExecuteReaderAsync(cancellationToken);
        while (await recentReader.ReadAsync(cancellationToken))
        {
            recentQueries.Add(new
            {
                queryKey = recentReader.GetString(0),
                resourceId = recentReader.GetString(1),
                day = recentReader.IsDBNull(2) ? null : recentReader.GetString(2),
                submercado = recentReader.IsDBNull(3) ? null : recentReader.GetString(3),
                limit = recentReader.GetInt32(4),
                retrievedAtUtc = recentReader.GetString(5)
            });
        }
    }

    var recordsBySubmercado = new List<object>();
    await using (var distributionCommand = connection.CreateCommand())
    {
        distributionCommand.CommandText =
            """
            SELECT submarket, COUNT(*)
            FROM pld_record_cache
            GROUP BY submarket
            ORDER BY COUNT(*) DESC;
            """;

        await using var distributionReader = await distributionCommand.ExecuteReaderAsync(cancellationToken);
        while (await distributionReader.ReadAsync(cancellationToken))
        {
            recordsBySubmercado.Add(new
            {
                submercado = distributionReader.GetString(0),
                totalRecords = distributionReader.GetInt64(1)
            });
        }
    }

    return Results.Ok(new
    {
        database = new
        {
            path = databasePath,
            sizeBytes = databaseInfo.Length,
            lastWriteTimeUtc = databaseInfo.LastWriteTimeUtc
        },
        totals = new
        {
            queryCount,
            recordCount,
            oldestRetrievedAtUtc,
            latestRetrievedAtUtc
        },
        recordsBySubmercado,
        recentQueries
    });
})
.WithName("GetAdminCache")
.Produces(StatusCodes.Status200OK)
.Produces(StatusCodes.Status401Unauthorized)
.Produces(StatusCodes.Status404NotFound);

app.Run();

static bool TryAuthorizeAdminRequest(
    HttpContext httpContext,
    DashboardAuthOptions authOptions,
    ConcurrentDictionary<string, DashboardSession> sessions,
    out string username)
{
    if (!authOptions.RequireAuthentication)
    {
        username = "authentication-disabled";
        return true;
    }

    return TryGetAuthenticatedUser(httpContext, sessions, out username);
}

static string ResolveLogsDirectory(string contentRootPath)
{
    return Path.GetFullPath(Path.Combine(contentRootPath, "logs"));
}

static IEnumerable<FileInfo> GetLogFiles(string logsDirectory)
{
    if (!Directory.Exists(logsDirectory))
        return Array.Empty<FileInfo>();

    return new DirectoryInfo(logsDirectory)
        .GetFiles("*.txt", SearchOption.TopDirectoryOnly)
        .OrderByDescending(file => file.LastWriteTimeUtc);
}

static IReadOnlyList<string> ReadLastLines(string filePath, int lineCount)
{
    var queue = new Queue<string>(lineCount);

    using var stream = new FileStream(
        filePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        if (queue.Count == lineCount)
            queue.Dequeue();

        queue.Enqueue(line);
    }

    return queue.ToArray();
}

static string SanitizeConfigContent(string rawContent)
{
    try
    {
        var node = JsonNode.Parse(rawContent);
        if (node is null)
            return rawContent;

        MaskSensitiveJsonValues(node);
        return node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
    catch
    {
        return rawContent;
    }
}

static void MaskSensitiveJsonValues(JsonNode node, string? parentPropertyName = null)
{
    if (node is JsonObject jsonObject)
    {
        var propertyNames = jsonObject.Select(property => property.Key).ToArray();

        foreach (var propertyName in propertyNames)
        {
            var childNode = jsonObject[propertyName];
            if (childNode is null)
                continue;

            if (IsSensitiveKey(propertyName))
            {
                jsonObject[propertyName] = "***";
                continue;
            }

            MaskSensitiveJsonValues(childNode, propertyName);
        }

        return;
    }

    if (node is JsonArray jsonArray)
    {
        foreach (var childNode in jsonArray)
        {
            if (childNode is null)
                continue;

            MaskSensitiveJsonValues(childNode, parentPropertyName);
        }
    }
}

static bool IsSensitiveKey(string key)
{
    var normalized = key.Trim().ToLowerInvariant();
    return normalized.Contains("password", StringComparison.Ordinal)
           || normalized.Contains("secret", StringComparison.Ordinal)
           || normalized.Contains("token", StringComparison.Ordinal)
           || normalized.Equals("api_key", StringComparison.Ordinal)
           || normalized.Equals("apikey", StringComparison.Ordinal);
}

static async Task<long> ExecuteSqliteCountAsync(
    SqliteConnection connection,
    string commandText,
    CancellationToken cancellationToken)
{
    await using var command = connection.CreateCommand();
    command.CommandText = commandText;
    var result = await command.ExecuteScalarAsync(cancellationToken);

    if (result is null || result is DBNull)
        return 0;

    return Convert.ToInt64(result, CultureInfo.InvariantCulture);
}

static bool TryBuildRequestMetricsQuery(
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    string? method,
    string? pathContains,
    int? statusCode,
    out RequestMetricsQuery query,
    out ProblemDetails problem)
{
    query = default!;
    problem = null!;

    var normalizedToUtc = (toUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
    var normalizedFromUtc = (fromUtc ?? normalizedToUtc.AddHours(-24)).ToUniversalTime();

    if (normalizedFromUtc > normalizedToUtc)
    {
        problem = CreateValidationProblem(
            "Parametros invalidos",
            "O parametro 'fromUtc' nao pode ser maior que 'toUtc'.");
        return false;
    }

    if ((normalizedToUtc - normalizedFromUtc).TotalDays > 31)
    {
        problem = CreateValidationProblem(
            "Parametros invalidos",
            "A janela de consulta de metricas deve ser de no maximo 31 dias.");
        return false;
    }

    string? normalizedMethod = null;
    if (!string.IsNullOrWhiteSpace(method))
    {
        normalizedMethod = method.Trim().ToUpperInvariant();
        if (normalizedMethod.Any(character => !char.IsLetter(character)))
        {
            problem = CreateValidationProblem(
                "Parametro invalido",
                "O parametro 'method' deve conter apenas letras (por exemplo: GET, POST).");
            return false;
        }
    }

    string? normalizedPathContains = null;
    if (!string.IsNullOrWhiteSpace(pathContains))
    {
        normalizedPathContains = pathContains.Trim();
        if (normalizedPathContains.Length > 120)
        {
            problem = CreateValidationProblem(
                "Parametro invalido",
                "O parametro 'pathContains' deve ter no maximo 120 caracteres.");
            return false;
        }
    }

    int? normalizedStatusCode = null;
    if (statusCode.HasValue)
    {
        if (statusCode.Value < 100 || statusCode.Value > 599)
        {
            problem = CreateValidationProblem(
                "Parametro invalido",
                "O parametro 'statusCode' deve estar entre 100 e 599.");
            return false;
        }

        normalizedStatusCode = statusCode.Value;
    }

    query = new RequestMetricsQuery(
        FromUtc: normalizedFromUtc,
        ToUtc: normalizedToUtc,
        Method: normalizedMethod,
        PathContains: normalizedPathContains,
        StatusCode: normalizedStatusCode);

    return true;
}

static bool TryBuildQuery(
    string? rawDate,
    string? rawSubmercado,
    int? rawLimit,
    IQueryCollection queryCollection,
    ISet<string> allowedSubmercados,
    out PldQuery query,
    out ProblemDetails problem)
{
    query = default!;
    problem = null!;

    if (HasAnyQueryKey(queryCollection, "resourceId", "forceRefresh"))
    {
        problem = CreateValidationProblem(
            "Parametro invalido",
            "Os parametros 'resourceId' e 'forceRefresh' nao sao suportados por este endpoint.");
        return false;
    }

    if (!TryParseDate(rawDate, out var dia))
    {
        problem = CreateValidationProblem("Parametro invalido", "O parametro 'date' deve estar no formato YYYY-MM-DD.");
        return false;
    }

    if (!TryParseLimit(rawLimit, out var limit))
    {
        problem = CreateValidationProblem("Parametro invalido", $"O parametro 'limit' deve ser um inteiro entre 1 e {PldQuery.MaxLimit}.");
        return false;
    }

    if (!TryParseSubmercado(rawSubmercado, allowedSubmercados, out var submercado))
    {
        problem = CreateValidationProblem(
            "Parametro invalido",
            "O parametro 'submercado' deve ser exatamente um dos valores: SUDESTE, SUL, NORDESTE, NORTE.");
        return false;
    }

    query = new PldQuery
    {
        ResourceId = PldQuery.DefaultResourceId,
        Dia = dia,
        Submercado = submercado,
        Limit = limit
    }.Normalize();

    return true;
}

static bool HasAnyQueryKey(IQueryCollection queryCollection, params string[] keys)
{
    foreach (var key in keys)
    {
        if (queryCollection.ContainsKey(key))
            return true;
    }

    return false;
}

static async Task<ResourceIdResolution> ResolveResourceIdAsync(
    DateOnly? date,
    PldGatewayOptions options,
    ConcurrentDictionary<int, string> discoveredResourceIds,
    string contentRootPath,
    Microsoft.Extensions.Logging.ILogger logger,
    CancellationToken cancellationToken)
{
    if (options.TryResolveResourceId(date, out var configuredResourceId, out _))
        return ResourceIdResolution.FromSuccess(configuredResourceId, autoDiscovered: false);

    var targetYear = date?.Year ?? DateTime.Now.Year;

    if (discoveredResourceIds.TryGetValue(targetYear, out var cachedResourceId))
        return ResourceIdResolution.FromSuccess(cachedResourceId, autoDiscovered: true);

    if (!options.EnableResourceIdAutoDiscovery)
    {
        options.TryResolveResourceId(date, out _, out var disabledErrorMessage);
        return ResourceIdResolution.FromFailure(disabledErrorMessage);
    }

    var discoveredResourceId = await DiscoverResourceIdForYearAsync(targetYear, options, cancellationToken);
    if (string.IsNullOrWhiteSpace(discoveredResourceId))
    {
        options.TryResolveResourceId(date, out _, out var baseErrorMessage);
        return ResourceIdResolution.FromFailure(
            $"{baseErrorMessage} A descoberta automatica tambem nao encontrou o recurso para o ano {targetYear}.");
    }

    discoveredResourceIds[targetYear] = discoveredResourceId;
    options.ResourceIdsByYear[targetYear] = discoveredResourceId;

    var persisted = await TryUpsertResourceIdInAppSettingsAsync(
        contentRootPath,
        targetYear,
        discoveredResourceId,
        logger,
        cancellationToken);

    if (persisted)
    {
        logger.LogWarning(
            "resource_id do ano {Year} descoberto automaticamente: {ResourceId}. Valor salvo em appsettings.json.",
            targetYear,
            discoveredResourceId);
    }
    else
    {
        logger.LogWarning(
            "resource_id do ano {Year} descoberto automaticamente: {ResourceId}, mas nao foi possivel salvar em appsettings.json.",
            targetYear,
            discoveredResourceId);
    }

    return ResourceIdResolution.FromSuccess(discoveredResourceId, autoDiscovered: true);
}

static async Task<string?> DiscoverResourceIdForYearAsync(int year, PldGatewayOptions options, CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(options.ResourceCatalogUrl))
        return null;

    try
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, options.HttpTimeoutSeconds))
        };

        if (!string.IsNullOrWhiteSpace(options.UserAgent))
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);

        using var response = await client.GetAsync(options.ResourceCatalogUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            return null;

        if (!root.TryGetProperty("result", out var result))
            return null;

        if (!result.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
            return null;

        var expectedName = $"pld_horario_{year}";
        foreach (var resource in resources.EnumerateArray())
        {
            var resourceName = GetJsonStringProperty(resource, "name");
            if (!string.Equals(resourceName, expectedName, StringComparison.OrdinalIgnoreCase))
                continue;

            var resourceId = GetJsonStringProperty(resource, "id");
            if (!string.IsNullOrWhiteSpace(resourceId))
                return resourceId.Trim();
        }
    }
    catch
    {
        // Ignore discovery errors and keep the normal configured flow.
    }

    return null;
}

static string? GetJsonStringProperty(JsonElement element, string propertyName)
{
    if (element.TryGetProperty(propertyName, out var directProperty))
        return directProperty.ValueKind == JsonValueKind.String ? directProperty.GetString() : directProperty.ToString();

    foreach (var property in element.EnumerateObject())
    {
        if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            continue;

        return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
    }

    return null;
}

static async Task<bool> TryUpsertResourceIdInAppSettingsAsync(
    string contentRootPath,
    int year,
    string resourceId,
    Microsoft.Extensions.Logging.ILogger logger,
    CancellationToken cancellationToken)
{
    var appsettingsPath = Path.Combine(contentRootPath, "appsettings.json");
    if (!File.Exists(appsettingsPath))
    {
        logger.LogWarning("Arquivo appsettings.json nao encontrado para persistir resource_id: {Path}", appsettingsPath);
        return false;
    }

    await ResourceIdAppSettingsSync.Semaphore.WaitAsync(cancellationToken);

    try
    {
        var json = await File.ReadAllTextAsync(appsettingsPath, cancellationToken);
        var rootNode = JsonNode.Parse(json) as JsonObject;
        if (rootNode is null)
        {
            logger.LogWarning("Nao foi possivel interpretar appsettings.json como objeto JSON: {Path}", appsettingsPath);
            return false;
        }

        var pldGatewayNode = rootNode["PldGateway"] as JsonObject ?? new JsonObject();
        rootNode["PldGateway"] = pldGatewayNode;

        var resourceIdsNode = pldGatewayNode["ResourceIdsByYear"] as JsonObject ?? new JsonObject();
        pldGatewayNode["ResourceIdsByYear"] = resourceIdsNode;

        var yearKey = year.ToString(CultureInfo.InvariantCulture);
        var currentValue = resourceIdsNode[yearKey]?.GetValue<string>();
        if (string.Equals(currentValue, resourceId, StringComparison.Ordinal))
            return true;

        resourceIdsNode[yearKey] = resourceId;

        var updatedJson = rootNode.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(appsettingsPath, updatedJson + Environment.NewLine, cancellationToken);
        return true;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Falha ao persistir resource_id descoberto automaticamente em appsettings.json.");
        return false;
    }
    finally
    {
        ResourceIdAppSettingsSync.Semaphore.Release();
    }
}

static bool TryParseDate(string? rawValue, out DateOnly? value)
{
    if (string.IsNullOrWhiteSpace(rawValue))
    {
        value = null;
        return true;
    }

    if (DateOnly.TryParseExact(rawValue, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
    {
        value = parsed;
        return true;
    }

    value = null;
    return false;
}

static bool TryParseLimit(int? rawValue, out int value)
{
    if (!rawValue.HasValue)
    {
        value = PldQuery.DefaultLimit;
        return true;
    }

    value = rawValue.Value;
    return value > 0 && value <= PldQuery.MaxLimit;
}

static bool TryParseSubmercado(string? rawValue, ISet<string> allowedSubmercados, out string? value)
{
    if (string.IsNullOrWhiteSpace(rawValue))
    {
        value = null;
        return true;
    }

    var candidate = rawValue.Trim();
    if (allowedSubmercados.Contains(candidate))
    {
        value = candidate;
        return true;
    }

    value = null;
    return false;
}

static IResult ServeHtml(
    string fileName,
    UiOptions uiOptions,
    IWebHostEnvironment environment,
    Microsoft.Extensions.Logging.ILogger logger)
{
    var configuredPagesRoot = Path.IsPathRooted(uiOptions.PagesPath)
        ? uiOptions.PagesPath
        : Path.GetFullPath(Path.Combine(environment.ContentRootPath, uiOptions.PagesPath));

    var configuredHtmlPath = Path.Combine(configuredPagesRoot, fileName);
    if (File.Exists(configuredHtmlPath))
    {
        var configuredHtml = File.ReadAllText(configuredHtmlPath);
        return Results.Content(configuredHtml, "text/html");
    }

    logger.LogWarning(
        "Arquivo HTML nao encontrado no caminho configurado: {ConfiguredPath}",
        configuredHtmlPath);

    return Results.NotFound(new { error = "Pagina indisponivel" });
}

static bool TryGetAuthenticatedUser(
    HttpContext httpContext,
    ConcurrentDictionary<string, DashboardSession> sessions,
    out string username)
{
    username = string.Empty;

    CleanupExpiredSessions(sessions);

    if (!httpContext.Request.Cookies.TryGetValue(DashboardAuthCookieName, out var token) || string.IsNullOrWhiteSpace(token))
        return false;

    if (!sessions.TryGetValue(token, out var session))
        return false;

    if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
    {
        sessions.TryRemove(token, out _);
        return false;
    }

    username = session.Username;
    return true;
}

static void CleanupExpiredSessions(ConcurrentDictionary<string, DashboardSession> sessions)
{
    var now = DateTimeOffset.UtcNow;

    foreach (var entry in sessions)
    {
        if (entry.Value.ExpiresAtUtc <= now)
            sessions.TryRemove(entry.Key, out _);
    }
}

static string CreateSessionToken()
{
    var bytes = RandomNumberGenerator.GetBytes(48);
    return Convert.ToBase64String(bytes)
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');
}

static PldGatewayOptions LoadGatewayOptions(IConfiguration configuration)
{
    var options = new PldGatewayOptions();

    var databasePath = configuration[$"{PldGatewayOptions.SectionName}:DatabasePath"];
    if (!string.IsNullOrWhiteSpace(databasePath))
        options.DatabasePath = databasePath;

    var baseUrl = configuration[$"{PldGatewayOptions.SectionName}:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(baseUrl))
        options.BaseUrl = baseUrl;

    var userAgent = configuration[$"{PldGatewayOptions.SectionName}:UserAgent"];
    if (!string.IsNullOrWhiteSpace(userAgent))
        options.UserAgent = userAgent;

    if (bool.TryParse(configuration[$"{PldGatewayOptions.SectionName}:DownloadBrowserIfMissing"], out var downloadBrowserIfMissing))
        options.DownloadBrowserIfMissing = downloadBrowserIfMissing;

    if (int.TryParse(configuration[$"{PldGatewayOptions.SectionName}:HttpTimeoutSeconds"], out var httpTimeoutSeconds) && httpTimeoutSeconds > 0)
        options.HttpTimeoutSeconds = httpTimeoutSeconds;

    if (int.TryParse(configuration[$"{PldGatewayOptions.SectionName}:HttpMaxAttempts"], out var httpMaxAttempts) && httpMaxAttempts > 0)
        options.HttpMaxAttempts = httpMaxAttempts;

    var browserExecutablePath = configuration[$"{PldGatewayOptions.SectionName}:BrowserExecutablePath"];
    if (!string.IsNullOrWhiteSpace(browserExecutablePath))
        options.BrowserExecutablePath = browserExecutablePath;

    if (bool.TryParse(configuration[$"{PldGatewayOptions.SectionName}:EnableResourceIdAutoDiscovery"], out var enableResourceIdAutoDiscovery))
        options.EnableResourceIdAutoDiscovery = enableResourceIdAutoDiscovery;

    var resourceCatalogUrl = configuration[$"{PldGatewayOptions.SectionName}:ResourceCatalogUrl"];
    if (!string.IsNullOrWhiteSpace(resourceCatalogUrl))
        options.ResourceCatalogUrl = resourceCatalogUrl;

    var resourceIdsByYearSection = configuration.GetSection($"{PldGatewayOptions.SectionName}:ResourceIdsByYear");
    foreach (var child in resourceIdsByYearSection.GetChildren())
    {
        if (!int.TryParse(child.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            continue;

        if (string.IsNullOrWhiteSpace(child.Value))
            continue;

        options.ResourceIdsByYear[year] = child.Value.Trim();
    }

    return options;
}

static UiOptions LoadUiOptions(IConfiguration configuration)
{
    var options = new UiOptions();
    var pagesPath = configuration["Ui:PagesPath"];

    if (!string.IsNullOrWhiteSpace(pagesPath))
        options.PagesPath = pagesPath.Trim();

    return options;
}

static DashboardAuthOptions LoadDashboardAuthOptions(IConfiguration configuration)
{
    var options = new DashboardAuthOptions();
    var section = configuration.GetSection("Security:Dashboard");

    if (!section.Exists())
        return options;

    if (bool.TryParse(section["RequireAuthentication"], out var requireAuthentication))
        options.RequireAuthentication = requireAuthentication;

    if (int.TryParse(section["SessionTimeoutMinutes"], out var sessionTimeoutMinutes) && sessionTimeoutMinutes > 0)
        options.SessionTimeoutMinutes = sessionTimeoutMinutes;

    var users = section
        .GetSection("Users")
        .GetChildren()
        .Select(child => new DashboardUser
        {
            Username = child["Username"]?.Trim() ?? string.Empty,
            Password = child["Password"] ?? string.Empty
        })
        .Where(user => !string.IsNullOrWhiteSpace(user.Username) && !string.IsNullOrWhiteSpace(user.Password))
        .ToArray();

    if (users.Length > 0)
        options.Users = users;

    return options;
}

static ProblemDetails CreateValidationProblem(string title, string detail)
{
    return new ProblemDetails
    {
        Title = title,
        Detail = detail,
        Status = StatusCodes.Status400BadRequest
    };
}

internal sealed class UiOptions
{
    public string PagesPath { get; set; } = Path.Combine("Configuration", "Ui");
}

internal static class ResourceIdAppSettingsSync
{
    public static readonly SemaphoreSlim Semaphore = new(1, 1);
}

internal sealed record ResourceIdResolution(bool Success, string? ResourceId, string? ErrorMessage, bool AutoDiscovered)
{
    public static ResourceIdResolution FromSuccess(string resourceId, bool autoDiscovered)
        => new(true, resourceId, null, autoDiscovered);

    public static ResourceIdResolution FromFailure(string errorMessage)
        => new(false, null, errorMessage, false);
}

internal sealed class DashboardAuthOptions
{
    public bool RequireAuthentication { get; set; } = true;
    public int SessionTimeoutMinutes { get; set; } = 60;
    public IReadOnlyList<DashboardUser> Users { get; set; } = Array.Empty<DashboardUser>();
}

internal sealed class DashboardUser
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

internal sealed record DashboardSession(string Username, DateTimeOffset ExpiresAtUtc);
internal sealed record LoginRequest(string? Username, string? Password);

internal sealed class RequestMetricsCollector
{
    private readonly object _sync = new();
    private readonly Queue<RequestMetricEntry> _recentRequests = new();
    private readonly int _maxRecentEntries;
    private readonly string _storageDirectoryPath;
    private readonly int _retentionDays;
    private DateTimeOffset _lastRetentionSweepUtc = DateTimeOffset.MinValue;

    public RequestMetricsCollector(int maxRecentEntries, string storageDirectoryPath, int retentionDays)
    {
        _maxRecentEntries = Math.Max(50, maxRecentEntries);
        _storageDirectoryPath = storageDirectoryPath;
        _retentionDays = Math.Max(7, retentionDays);
        Directory.CreateDirectory(_storageDirectoryPath);
    }

    public void Record(HttpContext context, TimeSpan elapsed)
    {
        var elapsedMs = Math.Round(elapsed.TotalMilliseconds, 2);
        var requestPath = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        var statusCode = context.Response.StatusCode;
        var timestamp = DateTimeOffset.UtcNow;
        var entry = new RequestMetricEntry
        {
            TimestampUtc = timestamp,
            Method = context.Request.Method,
            Path = requestPath,
            StatusCode = statusCode,
            DurationMs = elapsedMs,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown"
        };

        lock (_sync)
        {
            _recentRequests.Enqueue(entry);
            while (_recentRequests.Count > _maxRecentEntries)
                _recentRequests.Dequeue();

            PersistEntry(entry);
            TryCleanupOldFiles(timestamp);
        }
    }

    public RequestMetricsSnapshot CreateSnapshot(RequestMetricsQuery query)
    {
        var byRoute = new Dictionary<string, RequestMetricBucket>(StringComparer.OrdinalIgnoreCase);
        var recentRequestsQueue = new Queue<RequestMetricEntry>(_maxRecentEntries);
        var processedEntryKeys = new HashSet<string>(StringComparer.Ordinal);
        long totalRequests = 0;
        long totalErrors = 0;
        double totalDurationMs = 0;
        double maxDurationMs = 0;

        void RegisterEntry(RequestMetricEntry entry)
        {
            if (!processedEntryKeys.Add(BuildEntryKey(entry)))
                return;

            totalRequests++;
            if (entry.StatusCode >= 400)
                totalErrors++;

            totalDurationMs += entry.DurationMs;
            maxDurationMs = Math.Max(maxDurationMs, entry.DurationMs);

            var routeKey = $"{entry.Method} {entry.Path}";
            if (!byRoute.TryGetValue(routeKey, out var bucket))
            {
                bucket = new RequestMetricBucket
                {
                    Method = entry.Method,
                    Path = entry.Path
                };
                byRoute[routeKey] = bucket;
            }

            bucket.Count++;
            bucket.TotalDurationMs += entry.DurationMs;
            bucket.MaxDurationMs = Math.Max(bucket.MaxDurationMs, entry.DurationMs);
            if (entry.StatusCode >= 400)
                bucket.ErrorCount++;
            bucket.LastStatusCode = entry.StatusCode;
            bucket.LastRequestUtc = entry.TimestampUtc;

            recentRequestsQueue.Enqueue(entry);
            while (recentRequestsQueue.Count > _maxRecentEntries)
                recentRequestsQueue.Dequeue();
        }

        foreach (var entry in ReadPersistedEntries(query))
            RegisterEntry(entry);

        lock (_sync)
        {
            foreach (var entry in _recentRequests)
            {
                if (MatchesQuery(entry, query))
                    RegisterEntry(entry);
            }
        }

        var byRoutePayload = byRoute.Values
            .OrderByDescending(route => route.Count)
            .Select(route => new
            {
                route.Method,
                route.Path,
                route.Count,
                route.ErrorCount,
                averageDurationMs = route.Count == 0 ? 0 : Math.Round(route.TotalDurationMs / route.Count, 2),
                maxDurationMs = route.MaxDurationMs,
                route.LastStatusCode,
                route.LastRequestUtc
            })
            .Cast<object>()
            .ToArray();

        var recentRequestsPayload = recentRequestsQueue
            .Reverse()
            .Select(entry => new
            {
                entry.TimestampUtc,
                entry.Method,
                entry.Path,
                entry.StatusCode,
                entry.DurationMs,
                entry.RemoteIp
            })
            .Cast<object>()
            .ToArray();

        return new RequestMetricsSnapshot(
            TotalRequests: totalRequests,
            TotalErrors: totalErrors,
            AverageDurationMs: totalRequests == 0 ? 0 : Math.Round(totalDurationMs / totalRequests, 2),
            MaxDurationMs: maxDurationMs,
            ByRoute: byRoutePayload,
            RecentRequests: recentRequestsPayload);
    }

    private IEnumerable<RequestMetricEntry> ReadPersistedEntries(RequestMetricsQuery query)
    {
        var fromDate = query.FromUtc.UtcDateTime.Date;
        var toDate = query.ToUtc.UtcDateTime.Date;
        var matchedEntries = new List<RequestMetricEntry>();

        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            var filePath = GetMetricsFilePath(date);
            if (!File.Exists(filePath))
                continue;

            try
            {
                foreach (var line in ReadLinesWithSharedAccess(filePath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    RequestMetricEntry? entry;
                    try
                    {
                        entry = JsonSerializer.Deserialize<RequestMetricEntry>(line);
                    }
                    catch
                    {
                        continue;
                    }

                    if (entry is null || !MatchesQuery(entry, query))
                        continue;

                    matchedEntries.Add(entry);
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
        }

        return matchedEntries;
    }

    private static IEnumerable<string> ReadLinesWithSharedAccess(string filePath)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        string? line;
        while ((line = reader.ReadLine()) is not null)
            yield return line;
    }

    private static bool MatchesQuery(RequestMetricEntry entry, RequestMetricsQuery query)
    {
        var timestampUtc = entry.TimestampUtc.ToUniversalTime();
        if (timestampUtc < query.FromUtc || timestampUtc > query.ToUtc)
            return false;

        if (!string.IsNullOrWhiteSpace(query.Method)
            && !string.Equals(entry.Method, query.Method, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.PathContains)
            && (entry.Path?.Contains(query.PathContains, StringComparison.OrdinalIgnoreCase) != true))
            return false;

        if (query.StatusCode.HasValue && entry.StatusCode != query.StatusCode.Value)
            return false;

        return true;
    }

    private static string BuildEntryKey(RequestMetricEntry entry)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.TimestampUtc:O}|{entry.Method}|{entry.Path}|{entry.StatusCode}|{entry.DurationMs:F2}|{entry.RemoteIp}");
    }

    private void PersistEntry(RequestMetricEntry entry)
    {
        try
        {
            var filePath = GetMetricsFilePath(entry.TimestampUtc.UtcDateTime.Date);
            using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(entry));
        }
        catch
        {
            // Best effort persistence. Metrics collection must not fail the request pipeline.
        }
    }

    private void TryCleanupOldFiles(DateTimeOffset currentTimestampUtc)
    {
        if ((currentTimestampUtc - _lastRetentionSweepUtc).TotalHours < 6)
            return;

        _lastRetentionSweepUtc = currentTimestampUtc;
        var cutoffDate = currentTimestampUtc.UtcDateTime.Date.AddDays(-_retentionDays);

        foreach (var filePath in Directory.EnumerateFiles(_storageDirectoryPath, "metrics-*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(filePath);
            if (!TryParseMetricsFileDate(fileName, out var fileDate))
                continue;

            if (fileDate >= cutoffDate)
                continue;

            try
            {
                File.Delete(filePath);
            }
            catch
            {
                // Ignore cleanup failures; they do not impact runtime behavior.
            }
        }
    }

    private string GetMetricsFilePath(DateTime date)
    {
        return Path.Combine(_storageDirectoryPath, $"metrics-{date:yyyyMMdd}.jsonl");
    }

    private static bool TryParseMetricsFileDate(string fileName, out DateTime date)
    {
        date = default;

        const string prefix = "metrics-";
        const string suffix = ".jsonl";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var dateTokenLength = fileName.Length - prefix.Length - suffix.Length;
        if (dateTokenLength != 8)
            return false;

        var dateToken = fileName.Substring(prefix.Length, 8);
        return DateTime.TryParseExact(
            dateToken,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }
}

internal sealed record RequestMetricsQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? Method,
    string? PathContains,
    int? StatusCode);

internal sealed class RequestMetricBucket
{
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long Count { get; set; }
    public long ErrorCount { get; set; }
    public double TotalDurationMs { get; set; }
    public double MaxDurationMs { get; set; }
    public int LastStatusCode { get; set; }
    public DateTimeOffset LastRequestUtc { get; set; }
}

internal sealed class RequestMetricEntry
{
    public DateTimeOffset TimestampUtc { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public double DurationMs { get; set; }
    public string RemoteIp { get; set; } = string.Empty;
}

internal sealed record RequestMetricsSnapshot(
    long TotalRequests,
    long TotalErrors,
    double AverageDurationMs,
    double MaxDurationMs,
    object[] ByRoute,
    object[] RecentRequests);

