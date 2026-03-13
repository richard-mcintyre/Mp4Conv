using System.Collections.Concurrent;
using Mp4Conv.Web.Data;

namespace Mp4Conv.Web.Services;

public record ConversionProgress(int PercentComplete, string CurrentTime, string TotalDuration, string Speed);

public class ConversionProgressService
{
    private readonly ConcurrentDictionary<int, ConversionProgress> _progress = new();

    public event Action<int, ConversionProgress>? OnProgressUpdate;

    public event Action<int, FileConversionStatus, string?>? OnStatusChange;

    public void UpdateProgress(int id, ConversionProgress progress)
    {
        _progress[id] = progress;
        OnProgressUpdate?.Invoke(id, progress);
    }

    public void NotifyStatusChange(int id, FileConversionStatus status, string? message)
    {
        OnStatusChange?.Invoke(id, status, message);
    }

    public ConversionProgress? GetProgress(int id)
    {
        _progress.TryGetValue(id, out ConversionProgress? progress);
        return progress;
    }

    public void RemoveProgress(int id)
    {
        _progress.TryRemove(id, out _);
    }
}
