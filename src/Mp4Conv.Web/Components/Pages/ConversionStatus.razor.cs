using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Mp4Conv.Web.Data;
using Mp4Conv.Web.Services;

namespace Mp4Conv.Web.Components.Pages;

public partial class ConversionStatus : ComponentBase, IDisposable
{
    #region Construction

    public ConversionStatus()
    {
    }

    #endregion

    #region Properties

    [Inject]
    public required IDbContextFactory<Mp4ConvDbContext> DbContextFactory { get; set; }

    [Inject]
    public required ConversionBackgroundService BackgroundService { get; set; }

    [Inject]
    public required ConversionProgressService ProgressService { get; set; }

    public List<FileConversionEntity>? ActiveEntries { get; private set; }

    public List<FileConversionEntity>? CompletedEntries { get; private set; }

    #endregion

    #region Methods

    protected override async Task OnInitializedAsync()
    {
        this.ProgressService.OnProgressUpdate += HandleProgressUpdate;
        this.ProgressService.OnStatusChange += HandleStatusChange;
        this.ProgressService.OnQueueChanged += HandleQueueChanged;

        await using Mp4ConvDbContext context = await this.DbContextFactory.CreateDbContextAsync();
        List<FileConversionEntity> all = await context.ConversionQueue.OrderBy(e => e.Id).ToListAsync();
        this.ActiveEntries = all
            .Where(e => e.Status != FileConversionStatus.Completed && e.Status != FileConversionStatus.Failed)
            .ToList();
        this.CompletedEntries = all
            .Where(e => e.Status == FileConversionStatus.Completed || e.Status == FileConversionStatus.Failed)
            .OrderByDescending(e => e.StatusChangedAt)
            .ToList();
    }

    public void Dispose()
    {
        this.ProgressService.OnProgressUpdate -= HandleProgressUpdate;
        this.ProgressService.OnStatusChange -= HandleStatusChange;
        this.ProgressService.OnQueueChanged -= HandleQueueChanged;
    }

    public string FormatDuration(DateTime? startedAt, DateTime? endedAt)
    {
        if (startedAt is null || endedAt is null)
            return "—";
        TimeSpan duration = endedAt.Value - startedAt.Value;
        if (duration.TotalSeconds < 1)
            return "< 1s";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{duration.Seconds}s";
    }

    private void HandleProgressUpdate(int id, ConversionProgress progress)
    {
        _ = this.InvokeAsync(StateHasChanged);
    }

    private void HandleQueueChanged()
    {
        _ = this.InvokeAsync(async () =>
        {
            await using Mp4ConvDbContext context = await this.DbContextFactory.CreateDbContextAsync();
            this.ActiveEntries = await context.ConversionQueue
                .Where(e => e.Status != FileConversionStatus.Completed && e.Status != FileConversionStatus.Failed)
                .OrderBy(e => e.Id)
                .ToListAsync();
            StateHasChanged();
        });
    }

    private void HandleStatusChange(int id, FileConversionStatus status, string? message)
    {
        FileConversionEntity? entry = this.ActiveEntries?.FirstOrDefault(e => e.Id == id);
        if (entry is not null)
        {
            entry.Status = status;
            entry.StatusChangedAt = DateTime.UtcNow;
            entry.StatusMessage = message;

            if (status == FileConversionStatus.Completed || status == FileConversionStatus.Failed)
            {
                this.ActiveEntries!.Remove(entry);
                this.CompletedEntries?.Insert(0, entry);
            }
        }

        _ = this.InvokeAsync(StateHasChanged);
    }

    public async Task RemoveAllCompleted()
    {
        await using Mp4ConvDbContext context = await this.DbContextFactory.CreateDbContextAsync();
        await context.ConversionQueue
            .Where(e => e.Status == FileConversionStatus.Completed || e.Status == FileConversionStatus.Failed)
            .ExecuteDeleteAsync();
        this.CompletedEntries?.Clear();
    }

    public async Task RetryEntry(FileConversionEntity entry)
    {
        entry.Status = FileConversionStatus.NotStarted;
        entry.StatusChangedAt = DateTime.UtcNow;
        entry.StatusMessage = null;

        await using Mp4ConvDbContext context = await this.DbContextFactory.CreateDbContextAsync();
        FileConversionEntity? entity = await context.ConversionQueue.FindAsync(entry.Id);
        if (entity is null)
            return;

        entity.Status = FileConversionStatus.NotStarted;
        entity.StatusChangedAt = entry.StatusChangedAt;
        entity.StatusMessage = null;
        await context.SaveChangesAsync();

        this.CompletedEntries?.Remove(entry);
        this.ActiveEntries?.Add(entry);
    }

    public async Task RemoveEntry(FileConversionEntity entry)
    {
        if (entry.Status == FileConversionStatus.InProgress)
            await this.BackgroundService.CancelConversionAsync(entry.Id);

        await using Mp4ConvDbContext context = await this.DbContextFactory.CreateDbContextAsync();
        await context.ConversionQueue.Where(e => e.Id == entry.Id).ExecuteDeleteAsync();
        this.ActiveEntries?.Remove(entry);
        this.CompletedEntries?.Remove(entry);
    }

    public async Task TogglePaused(FileConversionEntity entry)
    {
        if (entry.Status == FileConversionStatus.InProgress)
        {
            // Set DB to Paused BEFORE cancelling so the cancellation handler sees the correct status
            DateTime now = DateTime.UtcNow;
            await using Mp4ConvDbContext context = await this.DbContextFactory.CreateDbContextAsync();
            FileConversionEntity? entity = await context.ConversionQueue.FindAsync(entry.Id);
            if (entity is null)
                return;

            entity.Status = FileConversionStatus.Paused;
            entity.StatusChangedAt = now;
            await context.SaveChangesAsync();

            entry.Status = FileConversionStatus.Paused;
            entry.StatusChangedAt = now;

            await this.BackgroundService.CancelConversionAsync(entry.Id);
            return;
        }

        entry.Status = entry.Status == FileConversionStatus.Paused
            ? FileConversionStatus.NotStarted
            : FileConversionStatus.Paused;
        entry.StatusChangedAt = DateTime.UtcNow;

        await using (Mp4ConvDbContext context = await this.DbContextFactory.CreateDbContextAsync())
        {
            FileConversionEntity? entity = await context.ConversionQueue.FindAsync(entry.Id);
            if (entity is null)
                return;

            entity.Status = entry.Status;
            entity.StatusChangedAt = entry.StatusChangedAt;
            await context.SaveChangesAsync();
        }
    }

    public async Task RemoveAllEntries()
    {
        await this.BackgroundService.CancelAllConversionsAsync();

        await using Mp4ConvDbContext context = await this.DbContextFactory.CreateDbContextAsync();
        await context.ConversionQueue
            .Where(e => e.Status != FileConversionStatus.Completed && e.Status != FileConversionStatus.Failed)
            .ExecuteDeleteAsync();
        this.ActiveEntries?.Clear();
    }

    public async Task PauseAll()
    {
        if (this.ActiveEntries is null)
            return;

        DateTime now = DateTime.UtcNow;
        await using Mp4ConvDbContext context = await this.DbContextFactory.CreateDbContextAsync();

        // Pause NotStarted entries
        await context.ConversionQueue
            .Where(e => e.Status == FileConversionStatus.NotStarted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, FileConversionStatus.Paused)
                .SetProperty(e => e.StatusChangedAt, now));

        // Pause InProgress entries — set DB status BEFORE cancelling so the handler sees Paused
        await context.ConversionQueue
            .Where(e => e.Status == FileConversionStatus.InProgress)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, FileConversionStatus.Paused)
                .SetProperty(e => e.StatusChangedAt, now));

        foreach (FileConversionEntity entry in this.ActiveEntries.Where(e =>
            e.Status == FileConversionStatus.NotStarted || e.Status == FileConversionStatus.InProgress))
        {
            entry.Status = FileConversionStatus.Paused;
            entry.StatusChangedAt = now;
        }

        await this.BackgroundService.CancelAllConversionsAsync();
    }

    public async Task ResumeAll()
    {
        if (this.ActiveEntries is null)
            return;

        DateTime now = DateTime.UtcNow;
        await using Mp4ConvDbContext context = await this.DbContextFactory.CreateDbContextAsync();
        await context.ConversionQueue
            .Where(e => e.Status == FileConversionStatus.Paused)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, FileConversionStatus.NotStarted)
                .SetProperty(e => e.StatusChangedAt, now));

        foreach (FileConversionEntity entry in this.ActiveEntries.Where(e => e.Status == FileConversionStatus.Paused))
        {
            entry.Status = FileConversionStatus.NotStarted;
            entry.StatusChangedAt = now;
        }
    }

    #endregion
}
