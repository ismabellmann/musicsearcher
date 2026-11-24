using Microsoft.EntityFrameworkCore;

namespace MusicSync.Infrastructure;

public sealed class RatingService
{
    private readonly MusicSyncDbContext _db;

    public RatingService(MusicSyncDbContext db) => _db = db;

    public Task<Rating?> GetAsync(string targetType, string targetKey, CancellationToken ct) =>
        _db.Ratings.AsNoTracking().SingleOrDefaultAsync(item => item.TargetType == targetType && item.TargetKey == targetKey, ct);

    public async Task<Rating> SetAsync(string targetType, string targetKey, int value, string? notes, CancellationToken ct)
    {
        Validate(targetType, targetKey, value, notes);
        var rating = await _db.Ratings.SingleOrDefaultAsync(item => item.TargetType == targetType && item.TargetKey == targetKey, ct);
        if (rating is null)
        {
            rating = new Rating { TargetType = targetType, TargetKey = targetKey, Value = value, Notes = notes?.Trim() };
            _db.Ratings.Add(rating);
        }
        else
        {
            rating.Value = value;
            rating.Notes = notes?.Trim();
            rating.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        return rating;
    }

    public async Task<bool> DeleteAsync(string targetType, string targetKey, CancellationToken ct)
    {
        var rating = await _db.Ratings.SingleOrDefaultAsync(item => item.TargetType == targetType && item.TargetKey == targetKey, ct);
        if (rating is null) return false;
        _db.Ratings.Remove(rating);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public static string TrackKey(Guid recordingMbid) => $"track:{recordingMbid:D}";
    public static string AlbumKey(Guid releaseGroupMbid) => $"album:{releaseGroupMbid:D}";

    private static void Validate(string type, string key, int value, string? notes)
    {
        if (type is not ("track" or "album")) throw new ArgumentException("El tipo debe ser track o album.", nameof(type));
        if (string.IsNullOrWhiteSpace(key) || key.Length > 300) throw new ArgumentException("La clave de valoración no es válida.", nameof(key));
        if (value is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(value), "La valoración debe estar entre 0 y 5.");
        if (notes?.Length > 2000) throw new ArgumentException("Las notas no pueden superar 2000 caracteres.", nameof(notes));
    }
}
