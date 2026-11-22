#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>What <see cref="FakeGuideCapture"/> does with one capture.</summary>
    internal enum FakeCaptureOutcome {
        /// <summary>The frame is saved; the completion carries its file name.</summary>
        Saved,
        /// <summary>The capture is accepted, then its completion reports a failure.</summary>
        Failed,
        /// <summary>The guider refuses the capture (<see cref="GuiderRpcException"/>).</summary>
        Refused,
        /// <summary>The capture is accepted and no completion ever comes.</summary>
        NeverCompletes,
    }

    /// <summary>
    /// ADR 0016 rule 5 — the Guider as the Polar Align Routine and guide focus see it, in process: no socket
    /// and no timing. A real <see cref="GuideCameraCustody"/> decides who holds the camera over a recorded
    /// lease, so the custody rules run unchanged. A completion is raised inside
    /// <see cref="CaptureFrameAsync"/>, after the capture is recorded, on the caller's thread.
    /// </summary>
    internal sealed class FakeGuideCapture : IGuideCapture, IGuideCaptureLink {
        private readonly object _gate = new();
        private readonly List<Action<SingleFrameCompleteEventArgs>> _handlers = new();
        private readonly List<(int ExposureMs, int? Binning)> _captures = new();
        private readonly List<string> _leaseCalls = new();
        private readonly List<(int Count, TaskCompletionSource Done)> _captureWaiters = new();
        private readonly List<(int Count, TaskCompletionSource Done)> _leaseWaiters = new();
        private readonly TaskCompletionSource _leaseReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Port _port;
        private string? _host = "192.0.2.10";
        private int _idleWaits;
        private int _loopStopCalls;
        private int _frameSizeReads;

        public FakeGuideCapture() {
            _port = new Port(this);
        }

        public GuideCameraCustody Custody { get; } = new();

        public bool Connected { get; set; } = true;

        /// <summary>The PHD2 app state the custody module reads ("Guiding" and the like make the Guider busy).</summary>
        public string? AppState { get; set; } = "Stopped";

        /// <summary>Why the Guider does not hold the active profile's settings; null when it does.</summary>
        public string? OutOfStep { get; set; }

        /// <summary>The host the Guider is connected to; null once <see cref="Connected"/> is false, as the
        /// PHD2 client's own host is after a disconnect.</summary>
        public string? Host {
            get => Connected ? _host : null;
            set => _host = value;
        }

        public int RpcPort { get; set; } = 4400;

        public (int Width, int Height)? FrameSize { get; set; } = (1280, 960);

        /// <summary>Runs inside each frame-size read: something that lands while a run waits on that RPC (#2066).</summary>
        public Action? OnFrameSizeRead { get; set; }

        /// <summary>When set, waiting for the capture to be idle fails with this message.</summary>
        public string? CaptureBusy { get; set; }

        public bool LoopStops { get; set; } = true;

        /// <summary>The outcome of the n-th capture (1-based).</summary>
        public Func<int, FakeCaptureOutcome> Script { get; set; } = _ => FakeCaptureOutcome.Saved;

        public string RefusalMessage { get; set; } = "the camera is busy";

        public const string FailedCaptureError = "the camera returned no frame";

        public int Subscribers {
            get {
                lock (_gate) {
                    return _handlers.Count;
                }
            }
        }

        public IReadOnlyList<(int ExposureMs, int? Binning)> Captures {
            get {
                lock (_gate) {
                    return _captures.ToArray();
                }
            }
        }

        /// <summary>"on:&lt;timeout&gt;" or "off", in order.</summary>
        public IReadOnlyList<string> LeaseCalls {
            get {
                lock (_gate) {
                    return _leaseCalls.ToArray();
                }
            }
        }

        public int IdleWaits => Volatile.Read(ref _idleWaits);

        public int LoopStopCalls => Volatile.Read(ref _loopStopCalls);

        /// <summary>How many times the camera frame size was asked for.</summary>
        public int FrameSizeReads => Volatile.Read(ref _frameSizeReads);

        /// <summary>Awaited at the start of every lease call, with whether it acquires or releases: a test holds a
        /// call here (on a <see cref="TaskCompletionSource"/>) to keep a Start or a release in flight. The call is
        /// recorded once the gate opens, so the recorded order is the order the guider would see.</summary>
        public Func<bool, Task>? LeaseGate { get; set; }

        /// <summary>When set, every lease acquire after the first (a renewal) is recorded, signalled and then
        /// refused with a <see cref="GuiderRpcException"/>, as a daemon answering a renewal with no result does.</summary>
        public bool RenewalsFail { get; set; }

        /// <summary>Completes once <paramref name="count"/> captures have been recorded (and their completions raised).</summary>
        public Task WhenCapturedAsync(int count) {
            lock (_gate) {
                if (_captures.Count >= count) {
                    return Task.CompletedTask;
                }
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _captureWaiters.Add((count, done));
                return done.Task;
            }
        }

        /// <summary>Completes once <paramref name="count"/> lease calls (acquire, renew or release) have been recorded.</summary>
        public Task WhenLeaseCallsAsync(int count) {
            lock (_gate) {
                if (_leaseCalls.Count >= count) {
                    return Task.CompletedTask;
                }
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _leaseWaiters.Add((count, done));
                return done.Task;
            }
        }

        /// <summary>Completes on the first lease release.</summary>
        public Task WhenLeaseReleasedAsync() => _leaseReleased.Task;

        /// <summary>Delivers a completion to every handler subscribed now (a late or stray one).</summary>
        public void RaiseFrameComplete(SingleFrameCompleteEventArgs e) {
            Action<SingleFrameCompleteEventArgs>[] handlers;
            lock (_gate) {
                handlers = _handlers.ToArray();
            }
            foreach (var handler in handlers) {
                handler(e);
            }
        }

        IGuideCaptureLink IGuideCapture.ConnectedLink() =>
            Connected ? this : throw new InvalidOperationException("guider is not connected");

        string? IGuideCapture.OutOfStepReason => OutOfStep;

        void IGuideCapture.ThrowIfOutOfStep(string operation) {
            if (OutOfStep is { } reason) {
                throw new GuiderOutOfStepException(reason);
            }
        }

        Task<GuideCameraCustody.Claim> IGuideCapture.TakeCameraAsync(CameraUse use, Func<CameraUse, Task> stop, CancellationToken ct) =>
            Custody.TakeAsync(use, _port, stop, ct);

        Task<GuideCameraCustody.Claim> IGuideCapture.TakeCameraIfFreeAsync(CameraUse use, Func<CameraUse, Task> stop, CancellationToken ct) =>
            Custody.TakeIfFreeAsync(use, _port, stop, ct);

        /// <summary>Guide pressed while a setup activity holds the camera: what GuiderService does before it guides.</summary>
        public Task TakeForGuidingAsync() => Custody.TakeForGuidingAsync(_port, CancellationToken.None);

        public Task WaitForCaptureIdleAsync(CancellationToken ct) {
            Interlocked.Increment(ref _idleWaits);
            return CaptureBusy is { } message ? Task.FromException(new InvalidOperationException(message)) : Task.CompletedTask;
        }

        public Task<bool> StopLoopForCaptureAsync(TimeSpan timeout, CancellationToken ct) {
            Interlocked.Increment(ref _loopStopCalls);
            return Task.FromResult(LoopStops);
        }

        public Task<(int Width, int Height)?> GetCameraFrameSizeAsync(CancellationToken ct) {
            Interlocked.Increment(ref _frameSizeReads);
            OnFrameSizeRead?.Invoke();
            return Task.FromResult(FrameSize);
        }

        public Task CaptureFrameAsync(int exposureMs, int? binning, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if (!Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            int n;
            lock (_gate) {
                _captures.Add((exposureMs, binning));
                n = _captures.Count;
            }
            try {
                switch (Script(n)) {
                    case FakeCaptureOutcome.Refused:
                        throw new GuiderRpcException("capture_single_frame", -1, RefusalMessage);
                    case FakeCaptureOutcome.Saved:
                        RaiseFrameComplete(new SingleFrameCompleteEventArgs(true, null, $"/fake/guider/frame_{n}.fits", $"frame_{n}.fits"));
                        break;
                    case FakeCaptureOutcome.Failed:
                        RaiseFrameComplete(new SingleFrameCompleteEventArgs(false, FailedCaptureError, null));
                        break;
                    case FakeCaptureOutcome.NeverCompletes:
                        break;
                }
            } finally {
                SignalCaptureWaiters(n);
            }
            return Task.CompletedTask;
        }

        public IDisposable OnFrameComplete(Action<SingleFrameCompleteEventArgs> handler) {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_gate) {
                _handlers.Add(handler);
            }
            return new Subscription(this, handler);
        }

        private void SignalCaptureWaiters(int count) {
            List<TaskCompletionSource> due;
            lock (_gate) {
                due = _captureWaiters.Where(w => w.Count <= count).Select(w => w.Done).ToList();
                _captureWaiters.RemoveAll(w => w.Count <= count);
            }
            foreach (var done in due) {
                done.TrySetResult();
            }
        }

        private sealed class Subscription(FakeGuideCapture owner, Action<SingleFrameCompleteEventArgs> handler) : IDisposable {
            private int _disposed;

            public void Dispose() {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) {
                    lock (owner._gate) {
                        owner._handlers.Remove(handler);
                    }
                }
            }
        }

        // The custody module's view of this Guider: the scripted app state and a recorded lease.
        private sealed class Port(FakeGuideCapture owner) : IGuideCameraPort {
            public string? AppState => owner.AppState;

            public Task<bool?> LeaseActiveAsync(CancellationToken ct) {
                lock (owner._gate) {
                    return Task.FromResult<bool?>(owner._leaseCalls.Count > 0 && owner._leaseCalls[^1] != "off");
                }
            }

            public async Task SetLeaseAsync(bool active, int? timeoutS, CancellationToken ct) {
                if (owner.LeaseGate is { } gate) {
                    await gate(active).ConfigureAwait(false);
                }
                List<TaskCompletionSource> due;
                bool refused;
                lock (owner._gate) {
                    owner._leaseCalls.Add(active ? $"on:{timeoutS}" : "off");
                    var n = owner._leaseCalls.Count;
                    refused = active && n > 1 && owner.RenewalsFail;
                    due = owner._leaseWaiters.Where(w => w.Count <= n).Select(w => w.Done).ToList();
                    owner._leaseWaiters.RemoveAll(w => w.Count <= n);
                }
                foreach (var d in due) {
                    d.TrySetResult();
                }
                if (!active) {
                    owner._leaseReleased.TrySetResult();
                }
                if (refused) {
                    // After the waiters are signalled: a test counting renewals sees the refused one too.
                    throw new GuiderRpcException("set_pa_session", -1, "the renewal was refused");
                }
            }
        }
    }
}