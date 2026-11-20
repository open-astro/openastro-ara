#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using System;
using System.Threading;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #2066 (ARA-2), #2065 (ARA-7) — the mount's single motion owner. A routine that drives the motors itself (Polar Align)
/// holds it for its whole run; meanwhile every GoTo that goes through the mediator (the sequencer's, Center's, the
/// meridian flip's) fails here, and the REST GoTo, Center, Park and Home endpoints refuse with
/// <c>polar-align-moving-mount</c> (<c>EquipmentEndpoints.ThrowIfPolarAlignMovesTheMount</c>). The owner check and the
/// send are one step: a GoTo is counted while it is being sent (<see cref="TryBeginMotionDispatch"/>), so one the REST
/// endpoint accepted just before the routine took the owner is dropped when it would go out, and the routine cannot take
/// the owner while one is on its way. Nothing that stops the mount is refused: AbortSlew (Stop Mount, Emergency Stop), a
/// pad stop, tracking off, and the safety and fault parks (they call <see cref="ParkAsync"/> and end the routine through
/// its takeover first).
/// </summary>
public sealed partial class TelescopeService {

    private readonly object _ownerGate = new();
    // The live hold (its identity: an older hold of the same owner gives nothing back) and its owner. Guarded by _ownerGate.
    private object? _holdToken;
    private string? _holdOwner;
    // GoTos being sent now (TryBeginMotionDispatch). Guarded by _ownerGate.
    private int _dispatching;

    /// <inheritdoc />
    public string? MotionOwner {
        get {
            lock (_ownerGate) {
                return _holdOwner;
            }
        }
    }

    /// <inheritdoc />
    public IDisposable AcquireMotion(string owner) {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        lock (_ownerGate) {
            if (_holdOwner is { } held && !string.Equals(held, owner, StringComparison.Ordinal)) {
                throw new InvalidOperationException($"{held} holds the mount's motion");
            }
            if (_dispatching > 0) {
                throw new InvalidOperationException("a GoTo is being sent to the mount");
            }
            var hold = new Hold(this, owner);
            _holdToken = hold;
            _holdOwner = owner;
            LogMotionOwnerTaken(owner);
            return hold;
        }
    }

    private void Release(Hold hold) {
        lock (_ownerGate) {
            if (!ReferenceEquals(_holdToken, hold)) {
                return; // replaced by a newer hold of the same owner, or given back already
            }
            _holdToken = null;
            _holdOwner = null;
        }
        LogMotionOwnerReleased(hold.Owner);
    }

    /// <summary>The sentence a GoTo, Center, Park or Home refused while <paramref name="owner"/> holds the mount carries
    /// (<paramref name="what"/> names the refused command).</summary>
    internal static string MotionOwnedMessage(string owner, string what) => owner == MotionOwners.PolarAlign
        ? $"Polar alignment is running and holds the mount, so the {what} was not sent. Stop polar alignment first (or tap Stop Mount, which ends it)."
        : $"{owner} holds the mount, so the {what} was not sent.";

    /// <summary>Begin sending a GoTo, unless a routine holds the mount (then null, and <paramref name="owner"/> names
    /// it). Checked and counted in one step with <see cref="AcquireMotion"/>: while the returned scope is open, no routine
    /// can take the owner, and once one has it, nothing more is sent.</summary>
    private Dispatch? TryBeginMotionDispatch(out string? owner) {
        lock (_ownerGate) {
            owner = _holdOwner;
            if (owner is not null) {
                return null;
            }
            _dispatching++;
            return new Dispatch(this);
        }
    }

    /// <summary><see cref="TryBeginMotionDispatch"/> for a mediator move: refused (a failed instruction, never an op
    /// fault) while a routine holds the mount. <paramref name="what"/> names the refused command.</summary>
    private Dispatch BeginMotionDispatchOrThrow(string op, string what) {
        var dispatch = TryBeginMotionDispatch(out var owner);
        if (dispatch is not null) {
            return dispatch;
        }
        LogMountOpRejectedOwned(op, owner!);
        throw new OpenAstroAra.Core.Model.SequenceEntityFailedException(MotionOwnedMessage(owner!, what));
    }

    private void EndDispatch() {
        lock (_ownerGate) {
            _dispatching--;
        }
    }

    private sealed class Dispatch(TelescopeService service) : IDisposable {
        private int _ended;

        public void Dispose() {
            if (Interlocked.Exchange(ref _ended, 1) == 0) {
                service.EndDispatch();
            }
        }
    }

    private sealed class Hold(TelescopeService service, string owner) : IDisposable {
        public string Owner => owner;

        public void Dispose() => service.Release(this);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "telescope: {Owner} holds the mount's motion — GoTo, Center, Park and Home are refused until it gives it back")]
    private partial void LogMotionOwnerTaken(string owner);

    [LoggerMessage(Level = LogLevel.Information, Message = "telescope: {Owner} gave the mount's motion back")]
    private partial void LogMotionOwnerReleased(string owner);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Op}: refused — {Owner} holds the mount's motion")]
    private partial void LogMountOpRejectedOwned(string op, string owner);

    [LoggerMessage(Level = LogLevel.Warning, Message = "slew to RA {RaHours} h, Dec {DecDeg}° dropped: {Owner} took the mount's motion after the GoTo was accepted")]
    private partial void LogSlewDroppedOwned(double raHours, double decDeg, string owner);
}
