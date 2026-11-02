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
builder.Services.AddSingleton<IMatchScorer, TrackScorer>();
builder.Services.AddSingleton<IAcquisitionProvider, DeterministicAcquisitionProvider>();
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

app.UseRouting();

app.MapGet("/", () => "MusicSync API is running");
app.MapControllers();

app.Run();
