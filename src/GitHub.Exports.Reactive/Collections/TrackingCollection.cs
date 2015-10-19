#if !DISABLE_REACTIVEUI
using ReactiveUI;
#endif
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Windows.Threading;

namespace GitHub.Collections
{
    /// <summary>
    /// TrackingCollection is a specialization of ObserableCollection that gets items from
    /// an observable sequence and updates its contents in such a way that two updates to
    /// the same object (as defined by an Equals call) will result in one object on
    /// the list being updated (as opposed to having two different instances of the object
    /// added to the list).
    /// It is always sorted, either via the supplied comparer or using the default comparer
    /// for T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class TrackingCollection<T> : ObservableCollection<T>, IDisposable
        where T : class, ICopyable<T>, IComparable<T>
    {
        enum TheAction
        {
            None,
            Move,
            Add,
            Insert
        }

        CompositeDisposable disposables = new CompositeDisposable();
        IObservable<T> source;
        IObservable<T> sourceQueue;
        Func<T, T, int> comparer;
        Func<T, int, IList<T>, bool> filter;
        IScheduler scheduler;
        int itemCount = 0;
        ConcurrentQueue<T> queue;

        List<T> original = new List<T>();
#if DEBUG
        public IList<T> DebugInternalList => original;
#endif

        // lookup optimizations
        // for speeding up IndexOf in the unfiltered list
        readonly Dictionary<T, int> sortedIndexCache = new Dictionary<T, int>();

        // for speeding up IndexOf in the filtered list
        readonly Dictionary<T, int> filteredIndexCache = new Dictionary<T, int>();

        TimeSpan delay;
        TimeSpan requestedDelay;
        TimeSpan fuzziness;
        public TimeSpan ProcessingDelay
        {
            get { return requestedDelay; }
            set
            {
                requestedDelay = value;
                delay = value;
            }
        }


        public TrackingCollection()
        {
            queue = new ConcurrentQueue<T>();
            ProcessingDelay = TimeSpan.FromMilliseconds(10);
            fuzziness = TimeSpan.FromMilliseconds(1);
        }

        public TrackingCollection(Func<T, T, int> comparer = null, Func<T, int, IList<T>, bool> filter = null, IScheduler scheduler = null)
            : this()
        {
#if DISABLE_REACTIVEUI
            this.scheduler = GetScheduler(scheduler);
#else
            this.scheduler = scheduler ?? RxApp.MainThreadScheduler;
#endif
            this.comparer = comparer ?? new Func<T, T, int>((o, p) => Comparer<T>.Default.Compare(o, p));
            this.filter = filter;
            original = new List<T>();
        }

        public TrackingCollection(IObservable<T> source,
            Func<T, T, int> comparer = null,
            Func<T, int, IList<T>, bool> filter = null,
            IScheduler scheduler = null)
            : this(comparer, filter, scheduler)
        {
            this.source = source;
            Listen(source);
        }

        public IObservable<T> Listen(IObservable<T> obs)
        {
            if (disposed)
                throw new ObjectDisposedException("TrackingCollection");

            sourceQueue = obs
                .Do(data => queue.Enqueue(data));

            source = Observable
                .Generate(StartQueue(),
                    i => !disposed,
                    i => i + 1,
                    i => GetFromQueue(),
                    i => delay
                )
                .Where(data => data != null)
                .ObserveOn(scheduler)
                .Select(x => ProcessItem(x, original))
                .Select(SortedNone)
                .Select(SortedAdd)
                .Select(SortedInsert)
                .Select(SortedMove)
                .Select(CheckFilter)
                .Select(FilteredAdd)
                .Select(CalculateIndexes)
                .Select(FilteredNone)
                .Select(FilteredInsert)
                .Select(FilteredMove)
                .TimeInterval()
                .Select(UpdateProcessingDelay)
                .Select(data => data.Item)
                .Publish()
                .RefCount();

            return source;
        }

        /// <summary>
        /// Set a new comparer for the existing data. This will cause the
        /// collection to be resorted and refiltered.
        /// </summary>
        /// <param name="theComparer">The comparer method for sorting, or null if not sorting</param>
        public void SetComparer(Func<T, T, int> theComparer)
        {
            if (disposed)
                throw new ObjectDisposedException("TrackingCollection");
            SetAndRecalculateSort(theComparer);
            SetAndRecalculateFilter(filter);
        }

        /// <summary>
        /// Set a new filter. This will cause the collection to be filtered
        /// </summary>
        /// <param name="theFilter">The new filter, or null to not have any filtering</param>
        public void SetFilter(Func<T, int, IList<T>, bool> theFilter)
        {
            if (disposed)
                throw new ObjectDisposedException("TrackingCollection");
            SetAndRecalculateFilter(theFilter);
        }

        public IDisposable Subscribe()
        {
            if (source == null)
                throw new InvalidOperationException("No source observable has been set. Call Listen or pass an observable to the constructor");
            if (disposed)
                throw new ObjectDisposedException("TrackingCollection");
            disposables.Add(source.Subscribe());
            return this;
        }

        public IDisposable Subscribe(Action<T> onNext, Action onCompleted)
        {
            if (source == null)
                throw new InvalidOperationException("No source observable has been set. Call Listen or pass an observable to the constructor");
            if (disposed)
                throw new ObjectDisposedException("TrackingCollection");
            disposables.Add(source.Subscribe(onNext, onCompleted));
            return this;
        }

        public void AddItem(T item)
        {
            if (disposed)
                throw new ObjectDisposedException("TrackingCollection");
            queue.Enqueue(item);
        }

        public void RemoveItem(T item)
        {
            if (disposed)
                throw new ObjectDisposedException("TrackingCollection");
            var position = GetIndexUnfiltered(item);
            if (position < 0)
                return;
            // unfiltered list update
            original.Remove(item);
            sortedIndexCache.Remove(item);
            UpdateIndexCache(original.Count - 1, position, original, sortedIndexCache);

            // filtered list update
            var index = GetIndexFiltered(item);
            InternalRemoveItem(item);
            RecalculateFilter(original, index, position, original.Count);
        }

        void SetAndRecalculateSort(Func<T, T, int> compare)
        {
            comparer = compare;
            var list = filter != null ? original : Items as List<T>;
            RecalculateSort(list, 0, list.Count);
        }

        void RecalculateSort(List<T> list, int start, int end)
        {
            if (comparer == null)
                return;

            list.Sort(start, end, new LambdaComparer<T>(comparer));

            // if there's a filter, then it's going to trigger events and we don't need to manually trigger them
            if (filter == null)
            {
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }

        void SetAndRecalculateFilter(Func<T, int, IList<T>, bool> newFilter)
        {
            if (filter == null && newFilter == null)
                return; // nothing to do

            // no more filter, add all the hidden items back
            if (filter != null && newFilter == null)
            {
                for (int i = 0; i < original.Count; i++)
                {
                    if (GetIndexFiltered(original[i]) < 0)
                        InternalInsertItem(original[i], i);
                }
                original = null;
                filter = null;
                return;
            }

            // there was no filter before, so the Items collection has everything, grab it
            if (filter == null)
                original = new List<T>(Items);
            else
                ClearItems();

            filter = newFilter;
            RecalculateFilter(original, 0, 0, original.Count);
        }

        protected override void ClearItems()
        {
            filteredIndexCache.Clear();
            base.ClearItems();
        }

        ActionData CheckFilter(ActionData data)
        {
            data.IsIncluded = true;
            if (filter != null)
                data.IsIncluded = filter(data.Item, data.Position, this);
            return data;
        }

        int StartQueue()
        {
            disposables.Add(sourceQueue.Subscribe(_ => itemCount++));
            return 0;
        }

        T GetFromQueue()
        {
            try
            {
                T d = null;
                if (queue?.TryDequeue(out d) ?? false)
                    return d;
            }
            catch { }
            return null;
        }

        ActionData ProcessItem(T item, List<T> list)
        {
            ActionData ret;

            var idx = GetIndexUnfiltered(item);
            if (idx >= 0)
            {
                var old = list[idx];
                var comparison = comparer(item, old);

                // no sorting to be done, just replacing the element in-place
                if (comparison == 0)
                    ret = new ActionData(TheAction.None, item, null, idx, idx, list);
                else
                    // element has moved, save the original object, because we want to update its contents and move it
                    // but not overwrite the instance.
                    ret = new ActionData(TheAction.Move, item, old, comparison, idx, list);
            }
            // the element doesn't exist yet
            // figure out whether we're larger than the last element or smaller than the first or
            // if we have to place the new item somewhere in the middle
            else if (list.Count > 0)
            {
                if (comparer(list[0], item) >= 0)
                    ret = new ActionData(TheAction.Insert, item, null, 0, -1, list);

                else if (comparer(list[list.Count - 1], item) <= 0)
                    ret = new ActionData(TheAction.Add, item, null, list.Count, -1, list);

                // this happens if the original observable is not sorted, or it's sorting order doesn't
                // match the comparer that has been set
                else
                {
                    idx = BinarySearch(list, item, comparer);
                    if (idx < 0)
                        idx = ~idx;
                    if (idx == list.Count)
                        ret = new ActionData(TheAction.Add, item, null, list.Count, -1, list);
                    else
                        ret = new ActionData(TheAction.Insert, item, null, idx, -1, list);
                }
            }
            else
                ret = new ActionData(TheAction.Add, item, null, list.Count, -1, list);
            return ret;
        }

        ActionData SortedNone(ActionData data)
        {
            if (data.TheAction != TheAction.None)
                return data;
            data.List[data.OldPosition].CopyFrom(data.Item);
            return data;
        }

        ActionData SortedAdd(ActionData data)
        {
            if (data.TheAction != TheAction.Add)
                return data;
            data.List.Add(data.Item);
            return data;
        }

        ActionData SortedInsert(ActionData data)
        {
            if (data.TheAction != TheAction.Insert)
                return data;
            data.List.Insert(data.Position, data.Item);
            UpdateIndexCache(data.Position, data.List.Count, data.List, sortedIndexCache);
            return data;
        }
        ActionData SortedMove(ActionData data)
        {
            if (data.TheAction != TheAction.Move)
                return data;
            data.OldItem.CopyFrom(data.Item);
            var pos = FindNewPositionForItem(data.OldPosition, data.Position < 0, data.List, comparer, sortedIndexCache);
            // the old item is the one moving around
            return new ActionData(data.TheAction, data.OldItem, null, pos, data.OldPosition, data.List);
        }

        ActionData FilteredAdd(ActionData data)
        {
            if (data.TheAction != TheAction.Add)
                return data;

            if (data.IsIncluded)
                InternalAddItem(data.Item);
            return data;
        }

        ActionData CalculateIndexes(ActionData data)
        {
            data.Index = GetIndexFiltered(data.Item);
            data.IndexPivot = GetLiveListPivot(data.Position, data.List);
            return data;
        }

        ActionData FilteredNone(ActionData data)
        {
            if (data.TheAction != TheAction.None)
                return data;

            // nothing has changed as far as the live list is concerned
            if ((data.IsIncluded && data.Index >= 0) || !data.IsIncluded && data.Index < 0)
                return data;

            // wasn't on the live list, but it is now
            if (data.IsIncluded && data.Index < 0)
                InsertAndRecalculate(data.List, data.Item, data.IndexPivot, data.Position, false);

            // was on the live list, it's not anymore
            else if (!data.IsIncluded && data.Index >= 0)
                RemoveAndRecalculate(data.List, data.Item, data.Index, data.Position);

            return data;
        }

        ActionData FilteredInsert(ActionData data)
        {
            if (data.TheAction != TheAction.Insert)
                return data;

            if (data.IsIncluded)
                InsertAndRecalculate(data.List, data.Item, data.IndexPivot, data.Position, false);

            // need to recalculate the filter because inserting an object (even if it's not itself visible)
            // can change visibility of other items after it
            else
                RecalculateFilter(data.List, data.IndexPivot, data.Position, data.List.Count);
            return data;
        }

        /// <summary>
        /// Checks if the object being moved affects the filtered list in any way and update
        /// the list accordingly
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        ActionData FilteredMove(ActionData data)
        {
            if (data.TheAction != TheAction.Move)
                return data;

            var start = data.OldPosition < data.Position ? data.OldPosition : data.Position;
            var end = data.Position > data.OldPosition ? data.Position : data.OldPosition;

            // if there's no filter, the filtered list is equal to the unfiltered list, just move
            if (filter == null)
            {
                MoveAndRecalculate(data.List, data.Index, data.IndexPivot, start, end);
                return data;
            }

            var filteredListChanged = false;
            var startPosition = Int32.MaxValue;
            var endPosition = -1;
            // check if the filtered list is affected indirectly by the move (eg., if the filter involves position of items,
            // moving an item outside the bounds of the filter can affect the items being currently shown/hidden)
            if (Count > 0)
            {
                startPosition = GetIndexUnfiltered(this[0]);
                endPosition = GetIndexUnfiltered(this[Count - 1]);
                // true if the filtered list has been indirectly affected by this objects' move
                filteredListChanged = (!filter(this[0], startPosition, this) || !filter(this[Count - 1], endPosition, this));
            }

            // the move caused the object to not be visible in the live list anymore, so remove
            if (!data.IsIncluded && data.Index >= 0)
                RemoveAndRecalculate(data.List, data.Item, filteredListChanged ? 0 : data.Index, filteredListChanged ? startPosition : start);

            // the move caused the object to become visible in the live list, insert it
            // and recalculate all the other things on the live list from the start position
            else if (data.IsIncluded && data.Index < 0)
            {
                start = startPosition < start ? startPosition : start;
                end = endPosition > end ? endPosition : end;
                InsertAndRecalculate(data.List, data.Item, data.IndexPivot, start, filteredListChanged);
            }

            // move the object and recalculate the filter between the bounds of the move
            else if (data.IsIncluded)
                MoveAndRecalculate(data.List, data.Index, data.IndexPivot, start, end);

            // recalculate the filter for every item, there's no way of telling what changed
            else if (filteredListChanged)
                RecalculateFilter(data.List, 0, 0, data.List.Count);

            return data;
        }

        /// <summary>
        /// Compensate time between items by time taken in processing them
        /// so that the average time between an item being processed
        /// is +- the requested processing delay.
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        ActionData UpdateProcessingDelay(TimeInterval<ActionData> data)
        {
            if (requestedDelay == TimeSpan.Zero)
                return data.Value;
            var time = data.Interval;
            if (time > requestedDelay + fuzziness)
                delay -= time - requestedDelay;
            else if (time < requestedDelay + fuzziness)
                delay += requestedDelay - time;
            delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
            return data.Value;
        }

        /// <summary>
        /// Insert an object into the live list at liveListCurrentIndex and recalculate
        /// positions for all objects from the position
        /// </summary>
        /// <param name="list">The unfiltered, sorted list of items</param>
        /// <param name="item"></param>
        /// <param name="index"></param>
        /// <param name="position">Index of the unfiltered, sorted list to start reevaluating the filtered list</param>
        void InsertAndRecalculate(IList<T> list, T item, int index, int position, bool rescanAll)
        {
            InternalInsertItem(item, index);
            if (rescanAll)
                index = 0; // reevaluate filter from the start of the filtered list
            else
            {
                // if the item in position is different from the item we're inserting,
                // that means that this insertion might require some filter reevaluation of items
                // before the one we're inserting. We need to figure out if the item in position
                // is in the filtered list, and if it is, then that's where we need to start
                // reevaluating the filter. If it isn't, then there's no need to reevaluate from
                // there
                var needToBacktrack = false;
                if (!Equals(item, list[position]))
                {
                    var idx = GetIndexFiltered(list[position]);
                    if (idx >= 0)
                    {
                        needToBacktrack = true;
                        index = idx;
                    }
                }

                if (!needToBacktrack)
                {
                    index++;
                    position++;
                }
            }
            RecalculateFilter(list, index, position, list.Count);
        }

        /// <summary>
        /// Remove an object from the live list at index and recalculate positions
        /// for all objects after that
        /// </summary>
        /// <param name="list">The unfiltered, sorted list of items</param>
        /// <param name="item"></param>
        /// <param name="index">The index in the live list</param>
        /// <param name="position">The position in the sorted, unfiltered list</param>
        void RemoveAndRecalculate(IList<T> list, T item, int index, int position)
        {
            InternalRemoveItem(item);
            RecalculateFilter(list, index, position, list.Count);
        }

        /// <summary>
        /// Move an object in the live list and recalculate positions
        /// for all objects between the bounds of the affected indexes
        /// </summary>
        /// <param name="list">The unfiltered, sorted list of items</param>
        /// <param name="from">Index in the live list where the object is</param>
        /// <param name="to">Index in the live list where the object is going to be</param>
        /// <param name="start">Index in the unfiltered, sorted list to start reevaluating the filter</param>
        /// <param name="end">Index in the unfiltered, sorted list to end reevaluating the filter</param>
        /// <param name="obj"></param>
        void MoveAndRecalculate(IList<T> list, int from, int to, int start, int end)
        {
            if (start > end)
                throw new ArgumentOutOfRangeException(nameof(start), "Start cannot be bigger than end, evaluation of the filter goes forward.");

            InternalMoveItem(from, to);
            to++;
            start++;
            RecalculateFilter(list, to, start, end);
        }


        /// <summary>
        /// Go through the list of objects and adjust their "visibility" in the live list
        /// (by removing/inserting as needed). 
        /// </summary>
        /// <param name="index">Index in the live list corresponding to the start index of the object list</param>
        /// <param name="start">Start index of the object list</param>
        /// <param name="end">End index of the object list</param>
        void RecalculateFilter(IList<T> list, int index, int start, int end)
        {
            if (filter == null)
                return;
            for (int i = start; i < end; i++)
            {
                var obj = list[i];
                var idx = GetIndexFiltered(obj);
                var isIncluded = filter(obj, i, this);

                // element is still included and hasn't changed positions
                if (isIncluded && idx >= 0)
                    index++;
                // element is included and wasn't before
                else if (isIncluded && idx < 0)
                {
                    if (index == Count)
                        InternalAddItem(obj);
                    else
                        InternalInsertItem(obj, index);
                    index++;
                }
                // element is not included and was before
                else if (!isIncluded && idx >= 0)
                    InternalRemoveItem(obj);
            }
        }

        /// <summary>
        /// Get the index in the live list of an object at position.
        /// This will scan back to the beginning of the live list looking for
        /// the closest left neighbour and return the position after that.
        /// </summary>
        /// <param name="position">The index of an object in the unfiltered, sorted list that we want to map to the filtered live list</param>
        /// <param name="list">The unfiltered, sorted list of items</param>
        /// <returns></returns>
        int GetLiveListPivot(int position, IList<T> list)
        {
            var index = -1;
            if (position > 0)
            {
                for (int i = position - 1; i >= 0; i--)
                {
                    index = GetIndexFiltered(list[i]);
                    if (index >= 0)
                    {
                        // found an element to the left of what we want, so now we know the index where to start
                        // manipulating the list
                        index++;
                        break;
                    }
                }
            }

            // there was no element to the left of the one we want, start at the beginning of the live list
            if (index < 0)
                index = 0;
            return index;
        }

        void RaiseMoveEvent(T item, int from, int to)
        {
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Move, item, to, from));
        }

        void RaiseResetEvent()
        {
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        /// <summary>
        /// Adds an item to the filtered list
        /// </summary>
        void InternalAddItem(T item)
        {
            Add(item);
        }

        /// <summary>
        /// Inserts an item into the filtered list
        /// </summary>
        void InternalInsertItem(T item, int position)
        {
            Insert(position, item);
        }

        protected override void InsertItem(int index, T item)
        {
            filteredIndexCache.Add(item, index);
            UpdateIndexCache(index, Count, Items, filteredIndexCache);
            base.InsertItem(index, item);
        }

        /// <summary>
        /// Removes an item from the filtered list
        /// </summary>
        void InternalRemoveItem(T item)
        {
            int idx = -1;
            if (!filteredIndexCache.TryGetValue(item, out idx))
                return;
            RemoveItem(idx);
        }

        protected override void RemoveItem(int index)
        {
            filteredIndexCache.Remove(this[index]);
            UpdateIndexCache(Count - 1, index, Items, filteredIndexCache);
            base.RemoveItem(index);
        }

        /// <summary>
        /// Moves an item in the filtered list
        /// </summary>
        void InternalMoveItem(int positionFrom, int positionTo)
        {
            positionTo = positionFrom < positionTo ? positionTo - 1 : positionTo;
            Move(positionFrom, positionTo);
        }

        protected override void MoveItem(int oldIndex, int newIndex)
        {
            if (oldIndex != newIndex)
            {
                UpdateIndexCache(newIndex, oldIndex, Items, filteredIndexCache);
                filteredIndexCache[this[oldIndex]] = newIndex;
            }
            base.MoveItem(oldIndex, newIndex);
        }

        /// <summary>
        /// The filtered list always has a cache filled up with
        /// all the items that are visible.
        /// </summary>
        int GetIndexFiltered(T item)
        {
            int idx = -1;
            if (filteredIndexCache.TryGetValue(item, out idx))
                return idx;
            return -1;
        }

        /// <summary>
        /// The unfiltered has a lazy cache that gets filled
        /// up when something is looked up.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        int GetIndexUnfiltered(T item)
        {
            int ret = -1;
            if (!sortedIndexCache.TryGetValue(item, out ret))
            {
                ret = original.IndexOf(item);
                if (ret >= 0)
                    sortedIndexCache.Add(item, ret);
            }
            return ret;
        }

        /// <summary>
        /// When items get moved/inserted/deleted, update the indexes in the cache.
        /// If start < end, we're inserting an item and want to shift all the indexes
        /// between start and end to the right (+1)
        /// If start > end, we're removing an item and want to shift all
        /// indexes to the left (-1).
        /// </summary>
        static void UpdateIndexCache(int start, int end, IList<T> list, Dictionary<T, int> indexCache)
        {
            var change = end < start ? -1 : 1;
            for (int i = start; i != end; i += change)
                if (indexCache.ContainsKey(list[i]))
                    indexCache[list[i]] += change;
        }

        static int FindNewPositionForItem(int idx, bool lower, IList<T> list, Func<T, T, int> comparer, Dictionary<T, int> indexCache)
        {
            var i = idx;
            if (lower) // replacing element has lower sorting order, find the correct spot towards the beginning
                for (var pos = i - 1; i > 0 && comparer(list[i], list[pos]) < 0; i--, pos--)
                {
                    Swap(list, i, pos);
                    SwapIndex(list, i, 1, indexCache);
                }

            else // replacing element has higher sorting order, find the correct spot towards the end
                for (var pos = i + 1; i < list.Count - 1 && comparer(list[i], list[pos]) > 0; i++, pos++)
                {
                    Swap(list, i, pos);
                    SwapIndex(list, i, -1, indexCache);
                }
            indexCache[list[i]] = i;
            return i;
        }

        /// <summary>
        /// Swap two elements
        /// </summary>
        static void Swap(IList<T> list, int left, int right)
        {
            var l = list[left];
            list[left] = list[right];
            list[right] = l;
        }

        static void SwapIndex(IList<T> list, int pos, int change, Dictionary<T, int> cache)
        {
            if (cache.ContainsKey(list[pos]))
                cache[list[pos]] += change;
        }

        static int BinarySearch(List<T> list, T item, Func<T, T, int> comparer)
        {
            return list.BinarySearch(item, new LambdaComparer<T>(comparer));
        }

        static IScheduler GetScheduler(IScheduler scheduler)
        {
            Dispatcher d = null;
            if (scheduler == null)
                d = Dispatcher.FromThread(Thread.CurrentThread);
            return scheduler ?? (d != null ? new DispatcherScheduler(d) : null as IScheduler) ?? CurrentThreadScheduler.Instance;
        }

        bool disposed = false;
        void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!disposed)
                {
                    disposed = true;
                    queue = null;
                    disposables.Dispose();
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        class ActionData : Tuple<TheAction, T, T, int, int, List<T>>
        {
            public TheAction TheAction => Item1;
            public T Item => Item2;
            public T OldItem => Item3;
            public int Position => Item4;
            public int OldPosition => Item5;
            public List<T> List => Item6;

            public int Index { get; set; }
            public int IndexPivot { get; set; }
            public bool IsIncluded { get; set; }

            public ActionData(TheAction action, T item, T oldItem, int position, int oldPosition, List<T> list)
                : base(action, item, oldItem, position, oldPosition, list)
            {
            }
        }
    }
}