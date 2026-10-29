using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using MusicSync.Core.Interfaces;
using MusicSync.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5000");

builder.Services.AddControllers();

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

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseRouting();

app.MapGet("/", () => "MusicSync API is running");
app.MapControllers();

app.Run();
