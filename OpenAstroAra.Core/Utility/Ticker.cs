#region "copyright"

/*
    Copyright � 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OxyPlot.Axes;
using System;
using System.Timers;

namespace OpenAstroAra.Core.Utility {

    public sealed class Ticker : BaseINPC, IDisposable {

        public Ticker(TimeSpan interval) {
            _timer = new Timer();
            _timer.Interval = interval.TotalMilliseconds;
            _timer.Elapsed += Timer_Elapsed;
            _timer.Start();
        }

        private readonly Timer _timer;

        // These are intentionally instance properties: the timer raises
        // PropertyChanged for them so bound consumers re-read the current time.
        // A static member could not participate in this instance change-notification,
        // so CA1822 ("mark static") does not apply.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "INotifyPropertyChanged change-notified property; must be an instance member to drive bindings via this Ticker's PropertyChanged.")]
        public DateTime Now => DateTime.Now;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "INotifyPropertyChanged change-notified property; must be an instance member to drive bindings via this Ticker's PropertyChanged.")]
        public double OxyNow => DateTimeAxis.ToDouble(DateTime.Now);

        private void Timer_Elapsed(object? sender, ElapsedEventArgs e) {
            RaisePropertyChanged(nameof(Now));
            RaisePropertyChanged(nameof(OxyNow));
        }

        public void Stop() {
            _timer.Stop();
        }

        public void Dispose() {
            _timer.Dispose();
        }
    }
}