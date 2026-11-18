using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using MusicSync.Api.Models;
using MusicSync.Core;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;
using MusicSync.Worker;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);

builder.WebHost.UseUrls(builder.Configuration["ASPNETCORE_URLS"] ?? "http://localhost:5000");

// Development keeps its SQLite database and data-protection keys in a repository-root
// config folder. Production deployments must pass persistent paths explicitly, usually
// Config__Dir=/config and Data__DatabasePath=/config/musicsync.db in the container.
var configDirectory = Path.GetFullPath(
    builder.Configuration["Config:Dir"]
        ?? (builder.Environment.IsDevelopment()
            ? Path.Combine(builder.Environment.ContentRootPath, "..", "config")
            : Path.Combine(builder.Environment.ContentRootPath, "data", "config")),
    builder.Environment.ContentRootPath);
Directory.CreateDirectory(configDirectory);

var databasePath = builder.Configuration["Data:DatabasePath"]
    ?? Path.Combine(
        builder.Environment.IsDevelopment()
            ? configDirectory
            : Path.Combine(builder.Environment.ContentRootPath, "data"),
        "musicsync.db");
databasePath = Path.GetFullPath(databasePath, builder.Environment.ContentRootPath);
var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
if (!string.IsNullOrWhiteSpace(databaseDirectory))
    Directory.CreateDirectory(databaseDirectory);

var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
{
    DataSource = databasePath
}.ToString();
builder.Services.AddDbContext<MusicSyncDbContext>(options => options.UseSqlite(connectionString));
var tracksDirectory = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "tracks"));
var libraryOptions = builder.Configuration.GetSection("Library").Get<LibraryOptions>() ?? new LibraryOptions();
if (string.IsNullOrWhiteSpace(libraryOptions.Root))
    libraryOptions.Root = tracksDirectory;
if (string.IsNullOrWhiteSpace(libraryOptions.StagingDirectory))
    libraryOptions.StagingDirectory = Path.Combine(builder.Environment.ContentRootPath, "data", "staging");

var audioOptions = builder.Configuration.GetSection("Audio").Get<AudioOptions>() ?? new AudioOptions();
var toolOptions = builder.Configuration.GetSection("Tools").Get<ToolOptions>() ?? new ToolOptions();
var acquisitionOptions = builder.Configuration.GetSection("Acquisition").Get<AcquisitionOptions>() ?? new AcquisitionOptions();
if (string.IsNullOrWhiteSpace(acquisitionOptions.LocalSourceDirectory))
    acquisitionOptions.LocalSourceDirectory = tracksDirectory;

builder.Services.AddSingleton(libraryOptions);
builder.Services.AddSingleton(audioOptions);
builder.Services.AddSingleton(toolOptions);
builder.Services.AddSingleton(new FfmpegAudioTranscoder(toolOptions, audioOptions));
builder.Services.AddSingleton<LibraryPathResolver>();
builder.Services.AddSingleton<JobEventHub>();
builder.Services.AddSingleton<IJobEventPublisher, JobEventPublisher>();
builder.Services.AddSingleton<JobCancellationRegistry>();
var musicBrainzOptions = builder.Configuration.GetSection("MusicBrainz").Get<MusicBrainzOptions>() ?? new MusicBrainzOptions();
var matchingOptions = builder.Configuration.GetSection("Matching").Get<MatchingOptions>() ?? new MatchingOptions();
var workerOptions = builder.Configuration.GetSection("Worker").Get<WorkerOptions>() ?? new WorkerOptions();
var playlistOptions = builder.Configuration.GetSection("Playlist").Get<PlaylistOptions>() ?? new PlaylistOptions();

var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
var coverArtOptions = builder.Configuration.GetSection("CoverArt").Get<CoverArtOptions>() ?? new CoverArtOptions();
var configurationErrors = MusicSyncOptionsValidator.Validate(
    musicBrainzOptions, matchingOptions, audioOptions, workerOptions, authOptions, coverArtOptions);
if (configurationErrors.Count > 0)
    throw new InvalidOperationException("Invalid configuration: " + string.Join(" ", configurationErrors));

builder.Services.AddSingleton(musicBrainzOptions);
builder.Services.AddSingleton(new MusicBrainzRequestGate());
builder.Services.AddSingleton(coverArtOptions);
builder.Services.AddSingleton<ITagWriter, TagLibTagWriter>();
builder.Services.AddSingleton<ICoverArtProvider>(serviceProvider => new CoverArtArchiveClient(
    coverArtOptions,
    musicBrainzOptions.UserAgent,
    serviceProvider.GetRequiredService<ILogger<CoverArtArchiveClient>>()));
builder.Services.AddSingleton(matchingOptions);
builder.Services.AddSingleton(workerOptions);
builder.Services.AddSingleton(playlistOptions);
builder.Services.AddSingleton<IMatchScorer>(_ => new TrackScorer(
    matchingOptions.AutoAcceptThreshold,
    matchingOptions.ReviewThreshold,
    matchingOptions.DurationToleranceSeconds));
builder.Services.AddSingleton<IAcquisitionProvider, DeterministicAcquisitionProvider>();
builder.Services.AddSingleton<IAcquisitionProvider>(serviceProvider => new LocalFileAcquisitionProvider(
    acquisitionOptions.LocalSourceDirectory,
    serviceProvider.GetRequiredService<FfmpegAudioTranscoder>()));
var ytDlpProvider = new YtDlpAcquisitionProvider(
    toolOptions,
    Path.Combine(libraryOptions.StagingDirectory, "downloads"));
builder.Services.AddSingleton<IAcquisitionProvider>(ytDlpProvider);
builder.Services.AddSingleton<IVideoSearchProvider>(ytDlpProvider);
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<Worker>());

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = _ =>
            new BadRequestObjectResult(ApiResponse<object?>.Failure(
                StatusCodes.Status400BadRequest,
                "La solicitud no es válida."));
    });

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

builder.Services.AddHttpClient<IMetadataProvider, MusicBrainzClient>();
builder.Services.AddHttpClient<IPopularityProvider, ListenBrainzClient>();
builder.Services.AddHttpClient("Spotify");
builder.Services.AddTransient<IPlaylistSource>(serviceProvider =>
{
    var logger = serviceProvider.GetRequiredService<ILogger<SpotifyPlaylistSource>>();
    var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
    return new SpotifyPlaylistSource(httpClientFactory.CreateClient("Spotify"), logger);
});

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(configDirectory, "keys")));
builder.Services.AddSingleton<ProtectedSettingsService>();

builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<LibraryImportService>();
builder.Services.AddScoped<LibraryFileService>();
builder.Services.AddScoped<PlaylistImportService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "musicsync.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = authOptions.SecureCookies
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(authOptions.SessionHours);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var accountId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var stamp = context.Principal?.FindFirstValue(AccountService.SecurityStampClaim);
            if (!int.TryParse(accountId, out var id) || stamp is null)
            {
                context.RejectPrincipal();
                return;
            }

            var db = context.HttpContext.RequestServices.GetRequiredService<MusicSyncDbContext>();
            var current = await db.UserAccounts.AsNoTracking()
                .Where(account => account.Id == id)
                .Select(account => account.SecurityStamp)
                .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
            if (current is null || !string.Equals(current, stamp, StringComparison.Ordinal))
                context.RejectPrincipal();
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<MusicSyncDbContext>();
    await MusicSyncDatabaseMigrator.MigrateAsync(database);
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (MetadataUnavailableException ex)
    {
        context.RequestServices.GetRequiredService<ILogger<Program>>()
            .LogWarning(ex, "Metadata provider unavailable for {Path}", context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(ApiResponse<object?>.Failure(
            StatusCodes.Status503ServiceUnavailable,
            "El proveedor de metadatos no está disponible en este momento. Inténtelo de nuevo más tarde."));
    }
});

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next(context);
});

// The shell and login/setup UI must be reachable before the first account exists.
app.UseDefaultFiles();
app.UseStaticFiles();

// Until the first account exists, only these paths answer (section 11).
var setupExemptPaths = new[] { "/api/health", "/api/auth/status", "/api/auth/setup" };

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (!setupExemptPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
    {
        var accounts = context.RequestServices.GetRequiredService<AccountService>();
        if (!await accounts.IsSetupCompleteAsync(context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(ApiResponse<object?>.Failure(
                StatusCodes.Status403Forbidden,
                "Complete la configuración inicial para continuar."));
            return;
        }
    }

    await next(context);
});

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "MusicSync API is running");
app.MapControllers();

app.Run();
