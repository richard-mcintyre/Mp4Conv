using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Mp4Conv.Web.Data;
using Mp4Conv.Web.Services;

namespace Mp4Conv.Web.Components.Pages;

public partial class ConfigurationSettings : ComponentBase, IDisposable
{
    #region Construction

    public ConfigurationSettings()
    {
    }

    #endregion

    #region Properties

    [Inject]
    public required IDbContextFactory<Mp4ConvDbContext> DbContextFactory { get; set; }

    public ConfigSettingsEntity? Settings { get; private set; }

    public List<DropSourceFolderEntity> DropSourceFolders { get; private set; } = [];

    [Inject]
    public required UncConnectionService UncConnectionService { get; set; }

    public List<UncCredentialEntity> UncCredentials { get; private set; } = [];

    public int ProcessorCount => Environment.ProcessorCount;

    public bool IsCoreEnabled(int coreIndex)
    {
        if (Settings is null || Settings.ProcessorAffinityMask == 0)
            return false;
        return (Settings.ProcessorAffinityMask & (1L << coreIndex)) != 0;
    }

    public void ToggleCore(int coreIndex)
    {
        if (Settings is null)
            return;
        long bit = 1L << coreIndex;
        if ((Settings.ProcessorAffinityMask & bit) != 0)
            Settings.ProcessorAffinityMask &= ~bit;
        else
            Settings.ProcessorAffinityMask |= bit;
    }

    #endregion

    #region Fields

    private bool _showSaveToast;
    private CancellationTokenSource? _toastCts;
    private string _newFolderPath = string.Empty;
    private string _newUncPath = string.Empty;
    private string _newUncUsername = string.Empty;
    private string _newUncPassword = string.Empty;
    private string _uncError = string.Empty;

    #endregion

    #region Methods

    protected override async Task OnInitializedAsync()
    {
        await using Mp4ConvDbContext context = await DbContextFactory.CreateDbContextAsync();
        Settings = await context.ConfigSettings.FirstOrDefaultAsync();
        DropSourceFolders = await context.DropSourceFolders
            .OrderBy(f => f.FolderPath)
            .ToListAsync();
        UncCredentials = await context.UncCredentials
            .OrderBy(c => c.UncPath)
            .ToListAsync();
    }

    public async Task Save()
    {
        if (Settings is null)
            return;

        await using Mp4ConvDbContext context = await DbContextFactory.CreateDbContextAsync();
        ConfigSettingsEntity? entity = await context.ConfigSettings.FindAsync(Settings.Id);
        if (entity is null)
            return;

        entity.RootPath = Settings.RootPath;
        entity.MaxNumberOfConcurrentConversions = Settings.MaxNumberOfConcurrentConversions;
        entity.PauseConversions = Settings.PauseConversions;
        entity.UseHardwareAcceleration = Settings.UseHardwareAcceleration;
        entity.ProcessorAffinityMask = Settings.ProcessorAffinityMask;
        await context.SaveChangesAsync();

        await ShowToast();
    }

    public async Task AddDropFolder()
    {
        string path = _newFolderPath.Trim();

        if (string.IsNullOrEmpty(path))
            return;

        if (DropSourceFolders.Any(f => f.FolderPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return;

        await using Mp4ConvDbContext context = await DbContextFactory.CreateDbContextAsync();
        DropSourceFolderEntity entity = new() { FolderPath = path };
        context.DropSourceFolders.Add(entity);
        await context.SaveChangesAsync();

        DropSourceFolders.Add(entity);
        DropSourceFolders.Sort((a, b) => string.Compare(a.FolderPath, b.FolderPath, StringComparison.OrdinalIgnoreCase));
        _newFolderPath = string.Empty;
    }

    public async Task OnNewFolderKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await AddDropFolder();
    }

    public async Task RemoveDropFolder(DropSourceFolderEntity folder)
    {
        await using Mp4ConvDbContext context = await DbContextFactory.CreateDbContextAsync();
        await context.DropSourceFolders.Where(f => f.Id == folder.Id).ExecuteDeleteAsync();
        DropSourceFolders.Remove(folder);
    }

    public async Task AddUncCredential()
    {
        string uncPath = _newUncPath.Trim();
        string username = _newUncUsername.Trim();
        string password = _newUncPassword;

        if (string.IsNullOrEmpty(uncPath) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return;

        if (UncCredentials.Any(c => c.UncPath.Equals(uncPath, StringComparison.OrdinalIgnoreCase)))
        {
            _uncError = "A credential for this UNC path already exists. Remove it first to update.";
            return;
        }

        _uncError = string.Empty;

        try
        {
            UncConnectionService.Connect(uncPath, username, password);
        }
        catch (Exception ex)
        {
            _uncError = ex.Message;
            return;
        }

        string encryptedPassword = UncConnectionService.ProtectPassword(password);

        await using Mp4ConvDbContext context = await DbContextFactory.CreateDbContextAsync();
        UncCredentialEntity entity = new() { UncPath = uncPath, Username = username, EncryptedPassword = encryptedPassword };
        context.UncCredentials.Add(entity);
        await context.SaveChangesAsync();

        UncCredentials.Add(entity);
        UncCredentials.Sort((a, b) => string.Compare(a.UncPath, b.UncPath, StringComparison.OrdinalIgnoreCase));
        _newUncPath = string.Empty;
        _newUncUsername = string.Empty;
        _newUncPassword = string.Empty;
    }

    public async Task RemoveUncCredential(UncCredentialEntity cred)
    {
        UncConnectionService.Disconnect(cred.UncPath);

        await using Mp4ConvDbContext context = await DbContextFactory.CreateDbContextAsync();
        await context.UncCredentials.Where(c => c.Id == cred.Id).ExecuteDeleteAsync();
        UncCredentials.Remove(cred);
    }

    public async Task ReconnectAll()
    {
        _uncError = string.Empty;
        IReadOnlyList<string> errors = await UncConnectionService.ConnectAllAsync();
        if (errors.Count > 0)
            _uncError = string.Join(Environment.NewLine, errors);
    }

    private async Task ShowToast()
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        CancellationToken token = _toastCts.Token;

        _showSaveToast = true;
        StateHasChanged();

        try
        {
            await Task.Delay(3000, token);
            _showSaveToast = false;
            StateHasChanged();
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose() => _toastCts?.Cancel();

    #endregion
}
