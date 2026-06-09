#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace OpenAstroAra.Core.Utility {

    public abstract class BaseINPC : ObservableObject {

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1030:Use events where appropriate", Justification = "Standard INotifyPropertyChanged raise helper for the existing PropertyChanged event; it is not itself a candidate to become an event.")]
        protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) {
            OnPropertyChanged(new PropertyChangedEventArgs(propertyName));
        }

        protected void ChildChanged(object? sender, PropertyChangedEventArgs e) {
            RaisePropertyChanged("IsChanged");
        }

        protected void ItemsCollectionChanged(object? sender,
               System.Collections.Specialized.NotifyCollectionChangedEventArgs e) {
            if (e.OldItems != null) {
                foreach (INotifyPropertyChanged item in e.OldItems) {
                    item.PropertyChanged -= new
                                           PropertyChangedEventHandler(ItemPropertyChanged);
                }
            }
            if (e.NewItems != null) {
                foreach (INotifyPropertyChanged item in e.NewItems) {
                    item.PropertyChanged +=
                                       new PropertyChangedEventHandler(ItemPropertyChanged);
                }
            }
        }

        protected void ItemPropertyChanged(object? sender, PropertyChangedEventArgs e) {
            RaisePropertyChanged("IsChanged");
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1030:Use events where appropriate", Justification = "Raises the existing PropertyChanged event for all properties (null name); not a candidate to become an event.")]
        protected void RaiseAllPropertiesChanged() {
            OnPropertyChanged(new PropertyChangedEventArgs(null));
        }
    }

    /// <remarks>
    /// Migration base for legacy model/settings types that require the
    /// <see cref="System.SerializableAttribute"/> / XML/DataContract serialization
    /// shape, which is incompatible with the CommunityToolkit ObservableObject.
    /// New types should derive from <see cref="BaseINPC"/>/ObservableObject instead;
    /// this exists only so previously-serialized profiles/models keep deserializing.
    /// </remarks>
    [System.Serializable()]
    [DataContract]
    public abstract class SerializableINPC : INotifyPropertyChanged {

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1030:Use events where appropriate", Justification = "Standard INotifyPropertyChanged raise helper for the existing PropertyChanged event; it is not itself a candidate to become an event.")]
        protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) {
            var handler = PropertyChanged;
            handler?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        [field: System.NonSerialized]
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void ChildChanged(object? sender, PropertyChangedEventArgs e) {
            RaisePropertyChanged("IsChanged");
        }

        protected void ItemsCollectionChanged(object? sender,
               System.Collections.Specialized.NotifyCollectionChangedEventArgs e) {
            if (e.OldItems != null) {
                foreach (INotifyPropertyChanged item in e.OldItems) {
                    item.PropertyChanged -= new
                                           PropertyChangedEventHandler(ItemPropertyChanged);
                }
            }
            if (e.NewItems != null) {
                foreach (INotifyPropertyChanged item in e.NewItems) {
                    item.PropertyChanged +=
                                       new PropertyChangedEventHandler(ItemPropertyChanged);
                }
            }
        }

        protected void ItemPropertyChanged(object? sender, PropertyChangedEventArgs e) {
            RaisePropertyChanged("IsChanged");
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1030:Use events where appropriate", Justification = "Raises the existing PropertyChanged event for all properties (null name); not a candidate to become an event.")]
        protected void RaiseAllPropertiesChanged() {
            var handler = PropertyChanged;
            handler?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}