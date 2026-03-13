using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Mp4Conv.Web.Data;
using Mp4Conv.Web.Models;

namespace Mp4Conv.Web.Components.Pages;

public partial class BrowsableFileList : ComponentBase
{
    #region Construction

    public BrowsableFileList()
    {
    }

    #endregion
    
    #region Fields

    private string _rootPath = string.Empty;
    private int? _lastSelectedIndex;
    private string _filterText = string.Empty;

    #endregion

    #region Properties

    [Inject]
    public required IDbContextFactory<Mp4ConvDbContext> DbContextFactory { get; set; }

    public string CurrentPath { get; private set; } = string.Empty;

    public bool PathExists { get; private set; }

    public List<string>? SubFolders { get; private set; }

    public List<FileModel>? Files { get; private set; }

    public HashSet<FileModel> SelectedFiles { get; private set; } = [];

    public string FilterText
    {
        get => _filterText;
        set
        {
            _filterText = value;
            _lastSelectedIndex = null;
        }
    }

    public IReadOnlyList<string> FilteredFolders =>
        string.IsNullOrWhiteSpace(_filterText) || SubFolders is null
            ? (IReadOnlyList<string>)(SubFolders ?? [])
            : SubFolders.Where(d => System.IO.Path.GetFileName(d)
                .Contains(_filterText, StringComparison.OrdinalIgnoreCase)).ToList();

    public IReadOnlyList<FileModel> FilteredFiles =>
        string.IsNullOrWhiteSpace(_filterText) || Files is null
            ? (IReadOnlyList<FileModel>)(Files ?? [])
            : Files.Where(f => System.IO.Path.GetFileName(f.FilePathAndName)
                .Contains(_filterText, StringComparison.OrdinalIgnoreCase)).ToList();

    public bool CanNavigateUp =>
        !string.IsNullOrEmpty(CurrentPath) &&
        !string.Equals(CurrentPath, _rootPath, StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Methods

    protected override async Task OnInitializedAsync()
    {
        await using Mp4ConvDbContext context = await DbContextFactory.CreateDbContextAsync();
        ConfigSettingsEntity? settings = await context.ConfigSettings.FirstOrDefaultAsync();
        _rootPath = settings?.RootPath ?? string.Empty;
        CurrentPath = _rootPath;
        LoadCurrentPath();
    }

    public void NavigateTo(string path)
    {
        CurrentPath = path;
        SelectedFiles = [];
        _filterText = string.Empty;
        _lastSelectedIndex = null;
        LoadCurrentPath();
    }

    public void NavigateUp()
    {
        if (!CanNavigateUp)
            return;

        string? parent = System.IO.Path.GetDirectoryName(CurrentPath);
        if (parent is not null)
            NavigateTo(parent);
    }

    public void OnFileClick(FileModel file, int index, MouseEventArgs e)
    {
        if (e.ShiftKey && _lastSelectedIndex.HasValue)
        {
            IReadOnlyList<FileModel> filtered = FilteredFiles;
            int start = Math.Min(_lastSelectedIndex.Value, index);
            int end = Math.Max(_lastSelectedIndex.Value, index);

            for (int i = start; i <= end; i++)
                SelectedFiles.Add(filtered[i]);
        }
        else
        {
            if (!SelectedFiles.Remove(file))
                SelectedFiles.Add(file);

            _lastSelectedIndex = index;
        }
    }

    private void LoadCurrentPath()
    {
        if (string.IsNullOrEmpty(CurrentPath) || !Directory.Exists(CurrentPath))
        {
            PathExists = false;
            SubFolders = null;
            Files = null;
            return;
        }

        PathExists = true;

        SubFolders = Directory.EnumerateDirectories(CurrentPath, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string[] extensions = [".mp4", ".mkv"];

        Files = Directory.EnumerateFiles(CurrentPath, "*", SearchOption.TopDirectoryOnly)
            .Where(o => extensions.Contains(System.IO.Path.GetExtension(o), StringComparer.OrdinalIgnoreCase))
            .Select(o => new FileModel(o))
            .ToList();
    }

    #endregion
}
