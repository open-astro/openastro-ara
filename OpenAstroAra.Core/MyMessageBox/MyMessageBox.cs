#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using OpenAstroAra.Core.Utility.Extensions;
using System;
using System.Windows;

namespace OpenAstroAra.Core.MyMessageBox {

    public class MyMessageBox : BaseINPC {
        private string _title;

        public string Title {
            get => _title;
            set {
                _title = value;
                RaisePropertyChanged();
            }
        }

        private string _text;

        public string Text {
            get => _text;
            set {
                _text = value;
                RaisePropertyChanged();
            }
        }

        private bool? _dialogResult;

        public bool? DialogResult {
            get => _dialogResult;
            set {
                _dialogResult = value;
                RaisePropertyChanged();
            }
        }

        private Visibility _cancelVisibility;

        public Visibility CancelVisibility {
            get => _cancelVisibility;
            set {
                _cancelVisibility = value;
                RaisePropertyChanged();
            }
        }

        private Visibility _oKVisibility;

        public Visibility OKVisibility {
            get => _oKVisibility;
            set {
                _oKVisibility = value;
                RaisePropertyChanged();
            }
        }

        private Visibility _yesVisibility;

        public Visibility YesVisibility {
            get => _yesVisibility;
            set {
                _yesVisibility = value;
                RaisePropertyChanged();
            }
        }

        private Visibility _noVisibility;

        public Visibility NoVisibility {
            get => _noVisibility;
            set {
                _noVisibility = value;
                RaisePropertyChanged();
            }
        }

        public static MessageBoxResult Show(string messageBoxText) {
            return Show(messageBoxText, "", MessageBoxButton.OK, MessageBoxResult.OK);
        }

        public static MessageBoxResult Show(string messageBoxText, string caption) {
            return Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxResult.OK);
        }

        // TODO(port): Phase 15 sweep — replace these static calls at all 4 call sites
        // (Sequencer/Sequencer.cs, SequenceHasChanged.cs, Container/SequenceRootContainer.cs,
        // MyMessageBoxVM.cs) with DI'd dialog/confirmation flow that proxies through WS to
        // WILMA (per §35 / §60.9 modal events). For now this is an API-preserving no-op
        // that returns the caller-supplied default so daemon code compiles without the
        // deleted WPF MyMessageBoxView (removed in Phase 0.5p).
        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxResult defaultresult) {
            return defaultresult;
        }
    }
}