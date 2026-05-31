#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// Phase 0.5p2 net10.0 conversion: the sequencer uses ICommand-typed
// properties + GalaSoft.MvvmLight.Command.RelayCommand throughout for the
// WPF right-click / drag-drop menus. Those properties never fire on the
// headless server (Flutter drives action via REST + WS), but the type
// signatures need to compile.
//
// Per playbook line 2140 we do NOT add a System.Windows.Compat shim —
// instead, ICommand + RelayCommand are defined in our own namespaces with
// the original WPF/MvvmLight call shape, so legacy source compiles as-is.

using System;

namespace System.Windows.Input {
    public interface ICommand {
        bool CanExecute(object? parameter);
        void Execute(object? parameter);
        event EventHandler? CanExecuteChanged;
    }
}

namespace System.Windows.Data {
    public interface ICollectionView : System.Collections.IEnumerable {
        bool MoveCurrentTo(object? item);
        object? CurrentItem { get; }
        System.Collections.IList GroupDescriptions { get; }
        System.Collections.IList SortDescriptions { get; }
        Predicate<object>? Filter { get; set; }
        void Refresh();
    }

    // Headless stub for WPF's CollectionViewSource — sequencer's
    // TemplateController / TargetController / SequencerFactory used it as a
    // sorted/grouped wrapper around an ObservableCollection. In headless we
    // just expose the underlying source; sorting/grouping is client-side.
    public class CollectionViewSource : ICollectionView {
        public System.Collections.IEnumerable? Source { get; set; }
        public System.Collections.IList GroupDescriptions { get; } = new System.Collections.Generic.List<object>();
        public System.Collections.IList SortDescriptions { get; } = new System.Collections.Generic.List<object>();
        public Predicate<object>? Filter { get; set; }
        public object? CurrentItem { get; private set; }
        public ICollectionView View => this;

        public bool MoveCurrentTo(object? item) { CurrentItem = item; return true; }
        public void Refresh() { /* no-op in headless */ }

        public System.Collections.IEnumerator GetEnumerator() =>
            Source?.GetEnumerator() ?? System.Linq.Enumerable.Empty<object>().GetEnumerator();

        public static ICollectionView GetDefaultView(object source) {
            var cvs = new CollectionViewSource { Source = source as System.Collections.IEnumerable };
            return cvs;
        }
    }

    public class PropertyGroupDescription {
        public string? PropertyName { get; set; }
        public PropertyGroupDescription() { }
        public PropertyGroupDescription(string propertyName) { PropertyName = propertyName; }
    }

    public class SortDescription {
        public string? PropertyName { get; set; }
        public System.ComponentModel.ListSortDirection Direction { get; set; }
        public SortDescription() { }
        public SortDescription(string propertyName, System.ComponentModel.ListSortDirection direction) {
            PropertyName = propertyName; Direction = direction;
        }
    }
}

namespace GalaSoft.MvvmLight.Command {
    public class RelayCommand : System.Windows.Input.ICommand {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;
        public RelayCommand(Action execute, Func<bool>? canExecute = null) {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }
        public bool CanExecute(object? parameter) => _canExecute is null || _canExecute();
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public class RelayCommand<T> : System.Windows.Input.ICommand {
        private readonly Action<T?> _execute;
        private readonly Func<T?, bool>? _canExecute;
        public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null) {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }
        public bool CanExecute(object? parameter) => _canExecute is null || _canExecute((T?)parameter);
        public void Execute(object? parameter) => _execute((T?)parameter);
        public event EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
