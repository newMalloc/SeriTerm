using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SeriTerm.Core.Documents;

/// <summary>
/// 支持批量增删的 <see cref="ObservableCollection{T}"/>。
///
/// 为什么需要它：日志区每秒可能新增成百上千行，逐行发 CollectionChanged 会让
/// WPF 每行都跑一次布局；这里把一批追加合并成**一次 Reset**，只重绘可视区域。
/// 淘汰旧行采用"整段重建"而不是逐行 <c>RemoveAt(0)</c>——后者在 20 万行规模下是 O(n²)，
/// 会把界面卡死好几秒。
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private int _deferLevel;

    /// <summary>批量操作期间挂起通知，作用域结束时只发一次 Reset。</summary>
    public IDisposable Defer() => new DeferScope(this);

    /// <summary>追加一批元素，结束时发一次 Reset。</summary>
    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        using (Defer())
        {
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }
    }

    /// <summary>从头部淘汰 <paramref name="count"/> 个元素（整段重建，O(n)）。</summary>
    public void TrimFront(int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (count >= Items.Count)
        {
            Clear();
            return;
        }

        using (Defer())
        {
            var kept = new T[Items.Count - count];
            for (var i = 0; i < kept.Length; i++)
            {
                kept[i] = Items[i + count];
            }

            Items.Clear();
            foreach (var item in kept)
            {
                Items.Add(item);
            }
        }
    }

    /// <summary>让已绑定的界面重新读取所有元素（切换 HEX/编码后调用）。</summary>
    public void Refresh() => RaiseReset();

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_deferLevel > 0)
        {
            return;
        }

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_deferLevel > 0)
        {
            return;
        }

        base.OnPropertyChanged(e);
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private sealed class DeferScope : IDisposable
    {
        private readonly BulkObservableCollection<T> _owner;
        private bool _disposed;

        public DeferScope(BulkObservableCollection<T> owner)
        {
            _owner = owner;
            _owner._deferLevel++;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (--_owner._deferLevel == 0)
            {
                _owner.RaiseReset();
            }
        }
    }
}
