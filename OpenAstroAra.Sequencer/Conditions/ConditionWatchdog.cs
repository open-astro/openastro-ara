#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Utility;
using OpenAstroAra.Sequencer.Interfaces;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Conditions {

    public class ConditionWatchdog : IConditionWatchdog, IDisposable {
        private CancellationTokenSource? watchdogCTS;
        private Task? watchdogTask;
        private readonly object lockObj = new object();

        public ConditionWatchdog(Func<Task> operation, TimeSpan delay) {
            WatchDogOperation = operation;
            Delay = delay;
        }

        public Func<Task> WatchDogOperation { get; }
        public TimeSpan Delay { get; set; }

        public Task? WatchdogTask {
            get {
                lock (lockObj) {
                    return watchdogTask;
                }
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Background watchdog boundary: WatchDogOperation is an arbitrary Func<Task> whose failures must be logged without killing the long-running watchdog loop. Cancellation is handled separately above; all other exceptions are logged and the loop continues. CA1031 sanctions general catches at such keep-alive background boundaries.")]
        public Task Start() {
            lock (lockObj) {
                if (watchdogTask == null) {
                    watchdogCTS = new CancellationTokenSource();
                    var token = watchdogCTS.Token;
                    watchdogTask = Task.Run(async () => {
                        while (true) {
                            try {
                                await WatchDogOperation();
                            } catch (OperationCanceledException) {
                                Logger.Debug("Condition watchdog was canceled");
                            } catch (Exception ex) {
                                Logger.Error(ex);
                            }
                            await Task.Delay(Delay, token);
                        }
                    });
                }
                return watchdogTask;
            }
        }

        public void Cancel() {
            lock (lockObj) {
                try {
                    watchdogCTS?.Cancel();
                } catch (ObjectDisposedException) { }

                watchdogCTS = null;
                watchdogTask = null;
            }
        }

        public void Dispose() {
            lock (lockObj) {
                try {
                    watchdogCTS?.Cancel();
                } catch (ObjectDisposedException) { }
                watchdogCTS?.Dispose();
                watchdogCTS = null;
                watchdogTask = null;
            }
            GC.SuppressFinalize(this);
        }
    }
}