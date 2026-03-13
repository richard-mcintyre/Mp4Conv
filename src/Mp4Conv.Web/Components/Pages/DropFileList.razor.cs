using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using Mp4Conv.Web.Data;
using Mp4Conv.Web.Models;

namespace Mp4Conv.Web.Components.Pages;

public partial class DropFileList : ComponentBase, IAsyncDisposable
{
    #region Construction

    public DropFileList()
    {
    }

    #endregion

    #region Fields

    private ElementReference _dropZoneRef;
    private DotNetObjectReference<DropFileList>? _dotNetRef;
    private int? _lastSelectedIndex;
    private string _dropError = string.Empty;

    #endregion

    #region Properties

    [Inject]
    public required IJSRuntime JS { get; set; }

    [Inject]
    public required IDbContextFactory<Mp4ConvDbContext> DbContextFactory { get; set; }

    public bool IsDraggingOver { get; private set; }

    public List<string> SourceFolders { get; private set; } = [];

    public List<FileModel> DroppedFiles { get; private set; } = [];

    public HashSet<FileModel> SelectedFiles { get; private set; } = [];

    #endregion

    #region Methods

    protected override async Task OnInitializedAsync()
    {
        await using Mp4ConvDbContext context = await DbContextFactory.CreateDbContextAsync();
        SourceFolders = await context.DropSourceFolders
            .OrderBy(f => f.FolderPath)
            .Select(f => f.FolderPath)
            .ToListAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("dropFileList.initialize", _dropZoneRef, _dotNetRef);
        }
    }

    [JSInvokable]
    public void OnDragEnter()
    {
        IsDraggingOver = true;
        StateHasChanged();
    }

    [JSInvokable]
    public void OnDragLeave()
    {
        IsDraggingOver = false;
        StateHasChanged();
    }

    [JSInvokable]
    public void OnFilesDropped(string[] uris, string[] fileNames)
    {
        IsDraggingOver = false;
        _dropError = string.Empty;

        string[] extensions = [".mp4", ".mkv"];

        if (uris.Length > 0)
        {
            // Firefox: full paths available via text/uri-list
            foreach (string uri in uris)
            {
                try
                {
                    string path = new Uri(uri).LocalPath;
                    if (extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                        AddFile(path);
                }
                catch { }
            }
        }
        else if (fileNames.Length > 0)
        {
            // Chrome: search configured source folders by file name
            if (SourceFolders.Count == 0)
            {
                _dropError = "No drop source folders configured. Add them in Settings.";
            }
            else
            {
                foreach (string fileName in fileNames)
                {
                    if (!extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
                        continue;

                    foreach (string folder in SourceFolders.Where(Directory.Exists))
                    {
                        foreach (string fullPath in Directory.EnumerateFiles(folder, fileName, SearchOption.AllDirectories))
                            AddFile(fullPath);
                    }
                }
            }
        }

        StateHasChanged();
    }

    private void AddFile(string path)
    {
        FileModel model = new(path);
        if (!DroppedFiles.Contains(model))
        {
            DroppedFiles.Add(model);
            SelectedFiles.Add(model);
        }
    }

    public void OnFileClick(FileModel file, int index, MouseEventArgs e)
    {
        if (e.ShiftKey && _lastSelectedIndex.HasValue)
        {
            int start = Math.Min(_lastSelectedIndex.Value, index);
            int end = Math.Max(_lastSelectedIndex.Value, index);

            for (int i = start; i <= end; i++)
                SelectedFiles.Add(DroppedFiles[i]);
        }
        else
        {
            if (!SelectedFiles.Remove(file))
                SelectedFiles.Add(file);

            _lastSelectedIndex = index;
        }
    }

    public void ClearFiles()
    {
        DroppedFiles = [];
        SelectedFiles = [];
        _lastSelectedIndex = null;
    }

    public void OnFilesQueued(IReadOnlyCollection<FileModel> files)
    {
        foreach (FileModel file in files)
        {
            DroppedFiles.Remove(file);
            SelectedFiles.Remove(file);
        }

        _lastSelectedIndex = null;
    }

    public async ValueTask DisposeAsync()
    {
        _dotNetRef?.Dispose();
        await ValueTask.CompletedTask;
    }

    #endregion
}
