using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.AspNetCore.Components;

namespace Lakeflow.Console.Web;

/// <summary>Re-renders a page whenever its view model (or one of its collections) changes.</summary>
public abstract class ViewModelComponent<TViewModel> : ComponentBase, IDisposable where TViewModel : class, INotifyPropertyChanged
{
    private TViewModel? _viewModel;
    private readonly List<INotifyCollectionChanged> _collections = [];

    protected TViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            Detach();
            _viewModel = value;
            if (value is null) return;
            value.PropertyChanged += OnChanged;
            foreach (var property in typeof(TViewModel).GetProperties())
                if (property.GetIndexParameters().Length == 0 && property.GetValue(value) is INotifyCollectionChanged collection)
                {
                    collection.CollectionChanged += OnCollectionChanged;
                    _collections.Add(collection);
                }
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => InvokeAsync(StateHasChanged);

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvokeAsync(StateHasChanged);

    private void Detach()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnChanged;
        foreach (var collection in _collections) collection.CollectionChanged -= OnCollectionChanged;
        _collections.Clear();
    }

    public virtual void Dispose()
    {
        Detach();
        GC.SuppressFinalize(this);
    }
}
