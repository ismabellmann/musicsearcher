using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using MusicSync.Api.Models;
using MusicSync.Core;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;
using MusicSync.Worker;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5000");

var databasePath = builder.Configuration["Data:DatabasePath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "musicsync.db");
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
var musicBrainzOptions = builder.Configuration.GetSection("MusicBrainz").Get<MusicBrainzOptions>() ?? new MusicBrainzOptions();
var matchingOptions = builder.Configuration.GetSection("Matching").Get<MatchingOptions>() ?? new MatchingOptions();
var workerOptions = builder.Configuration.GetSection("Worker").Get<WorkerOptions>() ?? new WorkerOptions();

var configurationErrors = MusicSyncOptionsValidator.Validate(musicBrainzOptions, matchingOptions, audioOptions, workerOptions);
if (configurationErrors.Count > 0)
    throw new InvalidOperationException("Invalid configuration: " + string.Join(" ", configurationErrors));

builder.Services.AddSingleton(musicBrainzOptions);
builder.Services.AddSingleton(new MusicBrainzRequestGate());
builder.Services.AddSingleton(matchingOptions);
builder.Services.AddSingleton(workerOptions);
builder.Services.AddSingleton<IMatchScorer>(_ => new TrackScorer(
    matchingOptions.AutoAcceptThreshold,
    matchingOptions.ReviewThreshold,
    matchingOptions.DurationToleranceSeconds));
builder.Services.AddSingleton<IAcquisitionProvider, DeterministicAcquisitionProvider>();
builder.Services.AddSingleton<IAcquisitionProvider>(serviceProvider => new LocalFileAcquisitionProvider(
    acquisitionOptions.LocalSourceDirectory,
    serviceProvider.GetRequiredService<FfmpegAudioTranscoder>()));
builder.Services.AddSingleton<IAcquisitionProvider>(_ => new YtDlpAcquisitionProvider(
    toolOptions,
    Path.Combine(libraryOptions.StagingDirectory, "downloads")));
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

builder.Services.AddHttpClient<IMetadataProvider, MusicBrainzClient>();
builder.Services.AddHttpClient<IPopularityProvider, ListenBrainzClient>();
builder.Services.AddHttpClient("Spotify");
builder.Services.AddTransient<IPlaylistSource>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var logger = serviceProvider.GetRequiredService<ILogger<SpotifyPlaylistSource>>();
    var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
    var clientId = configuration["Spotify:ClientId"] ?? configuration["Spotify__ClientId"] ?? string.Empty;
    var clientSecret = configuration["Spotify:ClientSecret"] ?? configuration["Spotify__ClientSecret"] ?? string.Empty;

    return new SpotifyPlaylistSource(httpClientFactory.CreateClient("Spotify"), logger, clientId, clientSecret);
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
            "MusicBrainz no está disponible en este momento. Inténtelo de nuevo más tarde."));
    }
});

app.UseRouting();

app.MapGet("/", () => "MusicSync API is running");
app.MapControllers();

app.Run();
