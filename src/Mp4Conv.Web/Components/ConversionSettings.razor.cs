using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Mp4Conv.Web.Data;
using Mp4Conv.Web.Models;
using Mp4Conv.Web.Services;

namespace Mp4Conv.Web.Components;

public partial class ConversionSettings : ComponentBase, IDisposable
{
    #region Construction

    public ConversionSettings()
    {
    }

    #endregion

    #region Properties

    [Inject]
    public required AppSettings AppSettings { get; set; }

    [Inject]
    public required IDbContextFactory<Mp4ConvDbContext> DbContextFactory { get; set; }

    [Inject]
    public required ConversionProgressService ProgressService { get; set; }

    [Parameter]
    public IReadOnlyCollection<FileModel> SelectedFiles { get; set; } = [];

    [Parameter]
    public EventCallback<IReadOnlyCollection<FileModel>> OnFilesQueued { get; set; }

    public ConversionSettingsModel Settings { get; set; } = new ConversionSettingsModel();

    public IReadOnlyList<VideoCodec> AllVideoCodecs =>
        (VideoCodec[])[ConversionSettingsModel.VideoCopyCodec, .. this.AppSettings.VideoCodecs];

    public VideoCodec SelectedVideoCodec
    {
        get => this.Settings.VideoCodec ?? ConversionSettingsModel.VideoCopyCodec;
        set => this.Settings.VideoCodec = value;
    }

    public IReadOnlyList<AudioCodec> AllAudioCodecs =>
        (AudioCodec[])[ConversionSettingsModel.AudioCopyCodec, .. this.AppSettings.AudioCodecs];

    public AudioCodec SelectedAudioCodec
    {
        get => this.Settings.AudioCodec ?? ConversionSettingsModel.AudioCopyCodec;
        set => this.Settings.AudioCodec = value;
    }

    public bool UseVideoQuality
    {
        get => this.Settings.VideoQuality.HasValue;
        set => this.Settings.VideoQuality = value ? (this.Settings.VideoQuality ?? DefaultVideoQuality) : null;
    }

    public int VideoQualityValue
    {
        get => this.Settings.VideoQuality ?? DefaultVideoQuality;
        set
        {
            if (this.Settings.VideoQuality.HasValue)
                this.Settings.VideoQuality = Math.Clamp(value, 0, 51);
        }
    }

    #endregion

    #region Fields

    private const int DefaultVideoQuality = 28;
    private string _toastMessage = string.Empty;
    private bool _showToast;
    private CancellationTokenSource? _toastCts;

    #endregion

    #region Methods

    public void DefaultToLowQuality()
    {
        this.Settings.VideoCodec  = this.AllVideoCodecs.FirstOrDefault(c => c.Codec == "libx265") ?? new VideoCodec("H.265", "libx265");
        this.Settings.VideoQuality = 28;
        this.Settings.AudioCodec  = this.AllAudioCodecs.FirstOrDefault(c => c.Codec == "aac") ?? new AudioCodec("AAC", "aac");
        this.Settings.AudioBitrate = "128k";
    }

    public async Task QueueForConversion()
    {
        await using Mp4ConvDbContext context = await this.DbContextFactory.CreateDbContextAsync();

        ConfigSettingsEntity? configSettings = await context.ConfigSettings.FirstOrDefaultAsync();
        FileConversionStatus initialStatus = configSettings?.PauseConversions == true
            ? FileConversionStatus.Paused
            : FileConversionStatus.NotStarted;

        List<string> filePaths = this.SelectedFiles.Select(f => f.FilePathAndName).ToList();

        Dictionary<string, FileConversionEntity> existing = await context.ConversionQueue
            .Where(e => filePaths.Contains(e.FilePathAndName))
            .ToDictionaryAsync(e => e.FilePathAndName);

        foreach (FileModel file in this.SelectedFiles)
        {
            if (existing.TryGetValue(file.FilePathAndName, out FileConversionEntity? entity))
            {
                entity.DeleteMkvFile = this.Settings.DeleteMkvFiles;
                entity.OverwriteMp4File = this.Settings.OverwriteMp4Files;
                entity.VideoCodec = this.SelectedVideoCodec.Codec;
                entity.AudioCodec = this.SelectedAudioCodec.Codec;
                entity.SetVideoQuality = this.UseVideoQuality;
                entity.VideoQuality = this.VideoQualityValue;
                entity.Status = initialStatus;
                entity.StatusChangedAt = DateTime.UtcNow;
            }
            else
            {
                context.ConversionQueue.Add(new FileConversionEntity
                {
                    FilePathAndName = file.FilePathAndName,
                    DeleteMkvFile = this.Settings.DeleteMkvFiles,
                    OverwriteMp4File = this.Settings.OverwriteMp4Files,
                    VideoCodec = this.SelectedVideoCodec.Codec,
                    AudioCodec = this.SelectedAudioCodec.Codec,
                    SetVideoQuality = this.UseVideoQuality,
                    VideoQuality = this.VideoQualityValue,
                    Status = initialStatus,
                    StatusChangedAt = DateTime.UtcNow
                });
            }
        }

        await context.SaveChangesAsync();

        this.ProgressService.NotifyQueueChanged();

        IReadOnlyCollection<FileModel> queued = [.. this.SelectedFiles];
        await this.OnFilesQueued.InvokeAsync(queued);

        int count = queued.Count;
        await this.ShowToast($"{count} {(count == 1 ? "file" : "files")} queued for conversion.");
    }

    private async Task ShowToast(string message)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        CancellationToken token = _toastCts.Token;

        _toastMessage = message;
        _showToast = true;
        this.StateHasChanged();

        try
        {
            await Task.Delay(3000, token);
            _showToast = false;
            this.StateHasChanged();
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _toastCts?.Cancel();
        _toastCts?.Dispose();
    }

    #endregion
}