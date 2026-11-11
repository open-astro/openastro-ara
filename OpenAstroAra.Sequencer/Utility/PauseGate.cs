#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Utility {

    /// <summary>
    /// §38 — the instruction-boundary pause gate for headless sequence execution.
    /// NINA's pause was WPF-coupled and stripped in the port; this replaces it with
    /// an engine-owned suspension point the execution strategies await between
    /// instructions (see <c>SequentialStrategy</c>/<c>ParallelStrategy</c>).
    ///
    /// Semantics:
    ///  * <see cref="RequestPause"/> arms the gate. A <see cref="PauseKind.User"/>
    ///    pause also cancels <see cref="InterruptToken"/>, so the instruction or
    ///    trigger in progress stops and runs again on Resume (a frame, flat set or
    ///    container finishes first); the strategy then suspends at that boundary.
    ///  * <see cref="PauseEntered"/> fires (on the engine thread) when a wait
    ///    actually blocks, so the owner can flip run state to Paused / notify only
    ///    for a suspension that really happened.
    ///  * <see cref="Resume"/> releases every waiter and disarms the gate.
    ///  * A cancelled run token aborts the wait with
    ///    <see cref="OperationCanceledException"/>, so abort/stop/shutdown win over
    ///    an active pause.
    /// Thread-safe: request/resume race arbitrary threads; waits happen on the
    /// engine worker.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "The interrupt source holds no timer or native handle (it is never created with a timeout), so it needs no disposal; a gate lives as long as its run and is shared by reference with containers that must not dispose it.")]
    public sealed class PauseGate {
        private readonly object _lock = new object();
        // Non-null while a pause is requested/active; completing it releases the
        // waiters. RunContinuationsAsynchronously so Resume() (a request thread)
        // never inlines engine continuations.
        private TaskCompletionSource? _resume;
        private PauseKind _kind = PauseKind.User;
        private CancellationTokenSource _interrupt = new();
        // Set when a user's pause has handed _interrupt to a Cancel that runs outside the lock: a
        // Resume in that gap must still swap it, or the token stays cancelled on an unarmed gate.
        private bool _interruptHandedOut;

        /// <summary>Cancelled by a user's pause (a person, or the daemon's safety pause), and new
        /// again on Resume: the plan's work in progress stops with it (user, 2026-11-11). An
        /// instruction's own wait for the user (a failed settle, a prompt) leaves it alone.</summary>
        public CancellationToken InterruptToken {
            get { lock (_lock) { return _interrupt.Token; } }
        }

        /// <summary>
        /// Raised on the engine thread each time a strategy actually suspends at
        /// the gate. With nested containers only the innermost strategy sits at an
        /// instruction boundary, so this fires once per pause in sequential trees;
        /// parallel branches may each fire — subscribers must be idempotent.
        /// </summary>
        public event EventHandler? PauseEntered;

        /// <summary>Whether a pause is currently requested (armed or actively suspending).</summary>
        public bool IsPauseRequested {
            get { lock (_lock) { return _resume != null; } }
        }

        /// <summary>
        /// Why the pending pause was requested. Only meaningful while
        /// <see cref="IsPauseRequested"/>; resets to <see cref="PauseKind.User"/>
        /// when <see cref="Resume"/> disarms the gate.
        /// </summary>
        public PauseKind PendingKind {
            get { lock (_lock) { return _kind; } }
        }

        /// <summary>
        /// Arm the gate. Idempotent — a second request joins the first. The kind
        /// only ever escalates (a user pause request cannot downgrade an armed
        /// <see cref="PauseKind.AwaitingUser"/> back to an ordinary pause).
        /// </summary>
        public void RequestPause(PauseKind kind = PauseKind.User) {
            CancellationTokenSource? interrupt = null;
            lock (_lock) {
                _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (kind > _kind) {
                    _kind = kind;
                }
                if (kind == PauseKind.User) {
                    interrupt = _interrupt;
                    _interruptHandedOut = true;
                }
            }
            // Outside the lock: cancellation runs the work's callbacks inline.
            interrupt?.Cancel();
        }

        /// <summary>Release all waiters and disarm. Harmless when not paused.</summary>
        public void Resume() {
            TaskCompletionSource? toComplete;
            lock (_lock) {
                toComplete = _resume;
                _resume = null;
                _kind = PauseKind.User;
                if (_interruptHandedOut || _interrupt.IsCancellationRequested) {
                    _interrupt = new CancellationTokenSource();
                    _interruptHandedOut = false;
                }
            }
            toComplete?.TrySetResult();
        }

        /// <summary>Completes once a pause is requested; for a long step that must not hold a pause back.</summary>
        public async Task WhenPauseRequestedAsync(CancellationToken token) {
            while (!IsPauseRequested) {
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Suspend while a pause is requested; return immediately otherwise. Loops
        /// so a re-pause racing the resume still holds the run at this boundary.
        /// Throws <see cref="OperationCanceledException"/> when
        /// <paramref name="token"/> cancels during the suspension (abort/stop).
        /// </summary>
        public async Task WaitWhilePausedAsync(CancellationToken token) {
            while (true) {
                token.ThrowIfCancellationRequested();
                Task resumeTask;
                lock (_lock) {
                    if (_resume is null) {
                        return;
                    }
                    resumeTask = _resume.Task;
                }
                PauseEntered?.Invoke(this, EventArgs.Empty);
                await resumeTask.WaitAsync(token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Why a pause was requested — the gate's owner maps this to the run state the
    /// suspension lands in. Ordered by severity: <see cref="RequestPause"/> only
    /// ever escalates the pending kind, never downgrades it.
    /// </summary>
    public enum PauseKind {

        /// <summary>§38 — an operator asked the run to pause; resume at leisure.</summary>
        User = 0,

        /// <summary>
        /// A step the plan asked for on purpose (<c>WaitForUser</c>: swap a filter, remove a cap). The run
        /// lands in the awaiting-user state, but no unattended-shutdown clock starts.
        /// </summary>
        Prompt = 1,

        /// <summary>
        /// §58.12 — the engine paused itself after an urgent failure (e.g. a §58.9
        /// meridian-flip abort with the mount in safe rest) and needs a human
        /// decision before imaging may continue.
        /// </summary>
        AwaitingUser = 2
    }

    /// <summary>
    /// Implemented by root containers that can carry a <see cref="PauseGate"/>.
    /// The execution strategies resolve the gate from the tree's root (via
    /// <c>ItemUtility.GetRootContainer</c>), so attaching a gate to the root
    /// pauses every nesting level; a null gate (tests, standalone containers)
    /// means pause is simply unavailable. A trigger's runner (a SequentialContainer that
    /// hangs off nothing) hosts a copy of the root's gate, set by SequenceTrigger.Run, so
    /// the instructions it fires can pause too.
    /// </summary>
    public interface IPauseGateHost {

        /// <summary>The run's pause gate, or null when pause is not wired.</summary>
        PauseGate? PauseGate { get; set; }
    }
}
