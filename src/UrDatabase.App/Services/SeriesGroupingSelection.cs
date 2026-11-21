using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace UrDatabase.Services;

public sealed class SeriesGroupingSelection : INotifyPropertyChanged, IDisposable
{
    private readonly IReadOnlyList<SeriesGroupingFile> _files;
    private bool _selecting;

    public SeriesGroupingSelection(IReadOnlyList<SeriesGroupingFile> files)
    {
        _files = files;
        Results = files;
        foreach (var file in files) file.PropertyChanged += FileChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<SeriesGroupingFile> Results { get; private set; }
    public bool HasResults => Results.Count > 0;
    public int SelectedCount => _files.Count(file => file.IsSelected);
    public bool? AllSelected
    {
        get
        {
            var selected = Results.Count(file => file.IsSelected);
            return selected == 0 ? false : selected == Results.Count ? true : null;
        }
    }
    public string Summary => HasResults
        ? $"{Results.Count(file => file.IsSelected)} of {Results.Count} shown selected - {SelectedCount} selected in total"
        : $"No files match this filter - {SelectedCount} selected in total";

    public void Filter(string? text)
    {
        var query = (text ?? "").Trim();
        Results = _files.Where(file =>
            file.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || file.FilePath.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        Changed(nameof(Results));
        Changed(nameof(HasResults));
        SelectionChanged();
    }

    public void SelectAllResults(bool selected)
    {
        _selecting = true;
        try
        {
            foreach (var file in Results) file.IsSelected = selected;
        }
        finally
        {
            _selecting = false;
            SelectionChanged();
        }
    }

    private void FileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_selecting && e.PropertyName == nameof(SeriesGroupingFile.IsSelected))
            SelectionChanged();
    }

    private void SelectionChanged()
    {
        Changed(nameof(AllSelected));
        Changed(nameof(SelectedCount));
        Changed(nameof(Summary));
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        foreach (var file in _files) file.PropertyChanged -= FileChanged;
    }
}
