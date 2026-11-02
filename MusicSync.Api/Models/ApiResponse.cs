namespace MusicSync.Api.Models;

public sealed record ApiResponse<T>(int Status, string Msg, T? Data)
{
    public static ApiResponse<T> Success(int status, string msg, T data) =>
        new(status, msg, data);

    public static ApiResponse<T> Failure(int status, string msg) =>
        new(status, msg, default);
}
