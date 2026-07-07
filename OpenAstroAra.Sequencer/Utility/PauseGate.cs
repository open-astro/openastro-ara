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
    ///  * <see cref="RequestPause"/> arms the gate — the run keeps executing until
    ///    the strategy reaches the next instruction boundary, then suspends.
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
    public sealed class PauseGate {
        private readonly object _lock = new object();
        // Non-null while a pause is requested/active; completing it releases the
        // waiters. RunContinuationsAsynchronously so Resume() (a request thread)
        // never inlines engine continuations.
        private TaskCompletionSource? _resume;
        private PauseKind _kind = PauseKind.User;

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
            lock (_lock) {
                _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (kind > _kind) {
                    _kind = kind;
                }
            }
        }

        /// <summary>Release all waiters and disarm. Harmless when not paused.</summary>
        public void Resume() {
            TaskCompletionSource? toComplete;
            lock (_lock) {
                toComplete = _resume;
                _resume = null;
                _kind = PauseKind.User;
            }
            toComplete?.TrySetResult();
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
        /// §58.12 — the engine paused itself after an urgent failure (e.g. a §58.9
        /// meridian-flip abort with the mount in safe rest) and needs a human
        /// decision before imaging may continue.
        /// </summary>
        AwaitingUser = 1
    }

    /// <summary>
    /// Implemented by root containers that can carry a <see cref="PauseGate"/>.
    /// The execution strategies resolve the gate from the tree's root (via
    /// <c>ItemUtility.GetRootContainer</c>), so attaching a gate to the root
    /// pauses every nesting level; a null gate (tests, standalone containers)
    /// means pause is simply unavailable.
    /// </summary>
    public interface IPauseGateHost {

        /// <summary>The run's pause gate, or null when pause is not wired.</summary>
        PauseGate? PauseGate { get; set; }
    }
}
