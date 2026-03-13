using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Mp4Conv.Web.Data;

namespace Mp4Conv.Web.Services;

public class ConversionBackgroundService : BackgroundService
{
    private readonly IDbContextFactory<Mp4ConvDbContext> _dbContextFactory;
    private readonly ConversionProgressService _progressService;
    private readonly ILogger<ConversionBackgroundService> _logger;
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _activeConversions = new();
    private readonly ConcurrentDictionary<int, Task> _activeTasks = new();

    public ConversionBackgroundService(
        IDbContextFactory<Mp4ConvDbContext> dbContextFactory,
        ConversionProgressService progressService,
        ILogger<ConversionBackgroundService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _progressService = progressService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing conversion queue.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        await using Mp4ConvDbContext context = await _dbContextFactory.CreateDbContextAsync(stoppingToken);
        ConfigSettingsEntity? config = await context.ConfigSettings.FirstOrDefaultAsync(stoppingToken);
        if (config is null)
            return;

        // Remove completed/failed entries older than 3 days
        DateTime cutoff = DateTime.UtcNow.AddDays(-3);
        await context.ConversionQueue
            .Where(e => (e.Status == FileConversionStatus.Completed || e.Status == FileConversionStatus.Failed)
                        && e.StatusChangedAt < cutoff)
            .ExecuteDeleteAsync(stoppingToken);

        int maxConcurrent = config.MaxNumberOfConcurrentConversions;
        bool useHardwareAcceleration = config.UseHardwareAcceleration;
        long processorAffinityMask = config.ProcessorAffinityMask;

        int activeCount = _activeConversions.Count;
        if (activeCount >= maxConcurrent)
            return;

        int slotsAvailable = maxConcurrent - activeCount;
        List<int> activeIds = [.. _activeConversions.Keys];

        List<FileConversionEntity> candidates = await context.ConversionQueue
            .Where(e => e.Status == FileConversionStatus.NotStarted && !activeIds.Contains(e.Id))
            .OrderBy(e => e.Id)
            .Take(slotsAvailable)
            .ToListAsync(stoppingToken);

        foreach (FileConversionEntity entry in candidates)
        {
            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            if (!_activeConversions.TryAdd(entry.Id, cts))
            {
                cts.Dispose();
                continue;
            }

            Task convTask = Task.Run(() => RunConversionAsync(entry, useHardwareAcceleration, processorAffinityMask, cts.Token), CancellationToken.None);
            _activeTasks.TryAdd(entry.Id, convTask);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await CancelAllConversionsAsync();

        if (_activeTasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(_activeTasks.Values).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timed out waiting for FFmpeg processes to stop during shutdown.");
            }
        }

        await base.StopAsync(cancellationToken);
    }

    public Task CancelConversionAsync(int id)
    {
        if (_activeConversions.TryGetValue(id, out CancellationTokenSource? cts))
            cts.Cancel();

        return Task.CompletedTask;
    }

    public Task CancelAllConversionsAsync()
    {
        foreach (KeyValuePair<int, CancellationTokenSource> kvp in _activeConversions)
            kvp.Value.Cancel();

        return Task.CompletedTask;
    }

    private async Task RunConversionAsync(FileConversionEntity entry, bool useHardwareAcceleration, long processorAffinityMask, CancellationToken cancellationToken)
    {
        string? outputPath = null;
        Process? process = null;

        try
        {
            // Mark InProgress in DB before starting
            await using (Mp4ConvDbContext context = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None))
            {
                FileConversionEntity? dbEntry = await context.ConversionQueue.FindAsync(entry.Id);
                if (dbEntry is null)
                    return;

                dbEntry.Status = FileConversionStatus.InProgress;
                dbEntry.StartedAt = DateTime.UtcNow;
                dbEntry.StatusChangedAt = DateTime.UtcNow;
                dbEntry.StatusMessage = null;
                await context.SaveChangesAsync();
            }

            _progressService.NotifyStatusChange(entry.Id, FileConversionStatus.InProgress, null);

            string inputPath = entry.FilePathAndName;
            string outputDir = Path.GetDirectoryName(inputPath) ?? string.Empty;
            string outputFileName = Path.GetFileNameWithoutExtension(inputPath) + ".mp4";
            outputPath = Path.Combine(outputDir, outputFileName);

            // If the input is already .mp4 the paths collide; use a distinct output name
            if (string.Equals(outputPath, inputPath, StringComparison.OrdinalIgnoreCase))
            {
                outputFileName = Path.GetFileNameWithoutExtension(inputPath) + "_converted.mp4";
                outputPath = Path.Combine(outputDir, outputFileName);
            }

            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete existing output file {File}.", outputPath); }
            }

            double totalSeconds = await GetDurationAsync(inputPath, cancellationToken);

            string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            string args = BuildFfmpegArgs(entry, inputPath, outputPath, useHardwareAcceleration, processorAffinityMask);
            _logger.LogInformation("Starting FFmpeg for entry {Id}: {Args}", entry.Id, args);

            List<string> stderrLines = [];

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    lock (stderrLines)
                        stderrLines.Add(e.Data);
                }
            };

            process.Start();

            if (OperatingSystem.IsWindows() && processorAffinityMask > 0)
            {
                // Use a Job Object with JOB_OBJECT_LIMIT_AFFINITY — a kernel-enforced hard ceiling
                // that prevents the process from expanding its own affinity mask (which libx265 does
                // internally at encoder init time, undoing a plain SetProcessAffinityMask call).
                nint hJob = NativeMethods.CreateJobObject(nint.Zero, null);
                if (hJob != nint.Zero)
                {
                    try
                    {
                        NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION limits = default;
                        limits.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_AFFINITY;
                        limits.Affinity = new UIntPtr((ulong)processorAffinityMask);
                        uint structSize = (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION>();
                        NativeMethods.SetInformationJobObject(hJob, 2, ref limits, structSize);
                        NativeMethods.AssignProcessToJobObject(hJob, process.Handle);
                    }
                    finally
                    {
                        NativeMethods.CloseHandle(hJob);
                    }
                }
                else
                {
                    NativeMethods.SetProcessAffinityMask(process.Handle, (UIntPtr)(ulong)processorAffinityMask);
                }
            }

            process.BeginErrorReadLine();

            await ReadProgressAsync(process, entry.Id, totalSeconds, cancellationToken);

            await process.WaitForExitAsync(CancellationToken.None);

            if (process.ExitCode == 0)
            {
                await using Mp4ConvDbContext context = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
                FileConversionEntity? dbEntry = await context.ConversionQueue.FindAsync(entry.Id);
                if (dbEntry is not null)
                {
                    dbEntry.Status = FileConversionStatus.Completed;
                    dbEntry.StatusChangedAt = DateTime.UtcNow;
                    dbEntry.StatusMessage = null;
                    await context.SaveChangesAsync();
                }

                _progressService.NotifyStatusChange(entry.Id, FileConversionStatus.Completed, null);

                if (entry.DeleteMkvFile && File.Exists(inputPath))
                {
                    try { File.Delete(inputPath); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Could not delete source file {File}.", inputPath); }
                }
            }
            else
            {
                string errorMessage;
                lock (stderrLines)
                    errorMessage = string.Join("\n", stderrLines.TakeLast(5));

                await using Mp4ConvDbContext context = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
                FileConversionEntity? dbEntry = await context.ConversionQueue.FindAsync(entry.Id);
                if (dbEntry is not null)
                {
                    dbEntry.Status = FileConversionStatus.Failed;
                    dbEntry.StatusChangedAt = DateTime.UtcNow;
                    dbEntry.StatusMessage = errorMessage;
                    await context.SaveChangesAsync();
                }

                _progressService.NotifyStatusChange(entry.Id, FileConversionStatus.Failed, errorMessage);
            }
        }
        catch (OperationCanceledException)
        {
            if (process is not null && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* best effort */ }
            }

            if (process is not null)
            {
                try { await process.WaitForExitAsync(CancellationToken.None); }
                catch { /* best effort */ }
            }

            // Check DB to determine final status (UI may have set to Paused before cancelling)
            try
            {
                await using Mp4ConvDbContext context = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
                FileConversionEntity? dbEntry = await context.ConversionQueue.FindAsync(entry.Id);

                if (dbEntry is not null && dbEntry.Status == FileConversionStatus.InProgress)
                {
                    // Cancelled by app shutdown — requeue so it picks up again on next start
                    dbEntry.Status = FileConversionStatus.NotStarted;
                    dbEntry.StatusChangedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync();
                    _progressService.NotifyStatusChange(entry.Id, FileConversionStatus.NotStarted, null);
                }
                else if (dbEntry is not null)
                {
                    // Status already set by UI (e.g. Paused) — push event so UI refreshes
                    _progressService.NotifyStatusChange(entry.Id, dbEntry.Status, null);
                }
                // dbEntry is null → user removed the item; nothing to do
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cancellation cleanup for entry {Id}.", entry.Id);
            }

            // Remove any partial output file
            if (outputPath is not null && File.Exists(outputPath))
            {
                try { File.Delete(outputPath); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete partial output file {File}.", outputPath); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error converting {File}.", entry.FilePathAndName);
            try
            {
                await using Mp4ConvDbContext context = await _dbContextFactory.CreateDbContextAsync(CancellationToken.None);
                FileConversionEntity? dbEntry = await context.ConversionQueue.FindAsync(entry.Id);
                if (dbEntry is not null)
                {
                    dbEntry.Status = FileConversionStatus.Failed;
                    dbEntry.StatusChangedAt = DateTime.UtcNow;
                    dbEntry.StatusMessage = ex.Message;
                    await context.SaveChangesAsync();
                }

                _progressService.NotifyStatusChange(entry.Id, FileConversionStatus.Failed, ex.Message);
            }
            catch { /* best effort */ }
        }
        finally
        {
            process?.Dispose();
            if (_activeConversions.TryRemove(entry.Id, out CancellationTokenSource? cts))
                cts.Dispose();
            _activeTasks.TryRemove(entry.Id, out _);
            _progressService.RemoveProgress(entry.Id);
        }
    }

    private async Task ReadProgressAsync(Process process, int entryId, double totalSeconds, CancellationToken cancellationToken)
    {
        DateTime lastUpdate = DateTime.MinValue;
        Dictionary<string, string> progressData = new(StringComparer.Ordinal);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            int eq = line.IndexOf('=');
            if (eq < 1)
                continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            progressData[key] = value;

            if (key == "progress")
            {
                if (DateTime.UtcNow - lastUpdate > TimeSpan.FromMilliseconds(500))
                {
                    lastUpdate = DateTime.UtcNow;
                    ConversionProgress? progress = BuildProgress(progressData, totalSeconds);
                    if (progress is not null)
                        _progressService.UpdateProgress(entryId, progress);
                }

                if (value == "end")
                    break;

                progressData.Clear();
            }
        }
    }

    private static ConversionProgress? BuildProgress(Dictionary<string, string> data, double totalSeconds)
    {
        if (!data.TryGetValue("out_time_ms", out string? outTimeMsStr))
            return null;

        if (!long.TryParse(outTimeMsStr, out long outTimeUs))
            return null;

        double currentSeconds = outTimeUs / 1_000_000.0;
        int percent = totalSeconds > 0 ? (int)Math.Min(100, currentSeconds / totalSeconds * 100) : 0;

        data.TryGetValue("speed", out string? speed);
        return new ConversionProgress(percent, FormatTime(currentSeconds), FormatTime(totalSeconds), speed ?? "N/A");
    }

    private static string FormatTime(double totalSeconds)
    {
        TimeSpan ts = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private async Task<double> GetDurationAsync(string filePath, CancellationToken cancellationToken)
    {
        string ffprobePath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v quiet -show_entries format=duration -of csv=p=0 \"{filePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            }
        };

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return double.TryParse(output.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double seconds)
            ? seconds
            : 0;
    }

    private static string BuildFfmpegArgs(FileConversionEntity entry, string inputPath, string outputPath, bool useHardwareAcceleration, long processorAffinityMask)
    {
        StringBuilder sb = new();
        string videoCodec = entry.VideoCodec;
        bool isNvenc = false;

        if (useHardwareAcceleration && videoCodec != "copy")
        {
            string? nvencCodec = GetNvencCodec(videoCodec);
            if (nvencCodec is not null)
            {
                videoCodec = nvencCodec;
                isNvenc = true;
            }
        }

        sb.Append($"-i \"{inputPath}\" ");
        sb.Append($"-c:v {videoCodec} ");

        if (entry.SetVideoQuality && videoCodec != "copy")
        {
            string qualityFlag = isNvenc ? "-cq" : "-crf";
            int quality = Math.Clamp(entry.VideoQuality, 0, 51);
            sb.Append($"{qualityFlag} {quality} ");
        }

        if (isNvenc)
            sb.Append("-vf format=yuv420p ");

        // For software codecs, constrain thread counts directly in the codec parameters.
        // Process-level affinity alone is insufficient because libx265 manages its own internal
        // thread pool via the Windows thread pool API and ignores the process affinity mask.
        if (!isNvenc && videoCodec != "copy" && processorAffinityMask > 0)
        {
            int threadCount = BitOperations.PopCount((ulong)processorAffinityMask);
            if (threadCount > 0)
            {
                // ffmpeg global thread flag — covers libx264, audio, demuxer threads etc.
                sb.Append($"-threads {threadCount} ");

                // libx265 has its own internal thread pool (libuv-based) that ignores -threads
                // and the process affinity mask entirely. The only reliable way to limit it is
                // via the x265 pools parameter, which sets the encoder thread count directly.
                if (videoCodec == "libx265")
                    sb.Append($"-x265-params pools={threadCount} ");
            }
        }

        sb.Append($"-c:a {entry.AudioCodec} ");
        sb.Append(entry.OverwriteMp4File ? "-y " : "-n ");
        sb.Append("-progress pipe:1 -nostats -loglevel warning ");
        sb.Append($"\"{outputPath}\"");

        return sb.ToString();
    }

    private static string? GetNvencCodec(string softwareCodec) => softwareCodec switch
    {
        "libx264" => "h264_nvenc",
        "libx265" => "hevc_nvenc",
        "libsvtav1" or "libaom-av1" => "av1_nvenc",
        _ => null,
    };

    private static class NativeMethods
    {
        // JOB_OBJECT_LIMIT_AFFINITY — prevents the process from expanding its affinity beyond the job mask
        public const uint JOB_OBJECT_LIMIT_AFFINITY = 0x00000010;

        // JOBOBJECT_BASIC_LIMIT_INFORMATION (x64 explicit layout, total 64 bytes)
        // LimitFlags is at offset 16, Affinity (ULONG_PTR) is at offset 48.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 64)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            [System.Runtime.InteropServices.FieldOffset(16)] public uint LimitFlags;
            [System.Runtime.InteropServices.FieldOffset(48)] public UIntPtr Affinity;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint CreateJobObject(nint lpJobAttributes, [MarshalAs(UnmanagedType.LPStr)] string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(nint hJob, int JobObjectInformationClass,
            ref JOBOBJECT_BASIC_LIMIT_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessAffinityMask(nint hProcess, UIntPtr dwProcessAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(nint hObject);
    }
}
