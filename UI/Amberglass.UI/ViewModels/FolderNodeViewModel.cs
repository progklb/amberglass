using System.Collections.ObjectModel;
using Amberglass.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Amberglass.UI.ViewModels;

public sealed partial class FolderNodeViewModel : ObservableObject
{
    private const string PlaceholderName = "…";

    private readonly IFolderBrowser? _browser;
    private bool _childrenLoaded;

    public FolderNodeViewModel(string fullPath, string name, IFolderBrowser browser)
    {
        FullPath = fullPath;
        Name = name;
        _browser = browser;

        // A placeholder child gives the node an expander arrow without walking the disk up front.
        Children = [CreatePlaceholder()];
    }

    private FolderNodeViewModel(string name)
    {
        FullPath = string.Empty;
        Name = name;
        Children = [];
        _childrenLoaded = true;
    }

    public string FullPath { get; }

    public string Name { get; }

    public ObservableCollection<FolderNodeViewModel> Children { get; }

    public bool IsPlaceholder => _browser is null;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = LoadChildrenAsync();
        }
    }

    public async Task LoadChildrenAsync()
    {
        if (_childrenLoaded || _browser is null)
        {
            return;
        }

        _childrenLoaded = true;

        var browser = _browser;
        var path = FullPath;
        var folders = await Task.Run(() => browser.GetSubfolders(path)).ConfigureAwait(true);

        Children.Clear();
        foreach (var folder in folders)
        {
            Children.Add(new FolderNodeViewModel(folder.FullPath, folder.Name, browser));
        }
    }

    private static FolderNodeViewModel CreatePlaceholder() => new(PlaceholderName);
}
