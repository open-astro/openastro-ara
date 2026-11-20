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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #2066 (ARA-2), #2065 (ARA-7) — the mount's single motion owner. A routine that drives the motors itself (Polar Align)
/// holds it for its whole run; meanwhile every GoTo that goes through the mediator (the sequencer's, Center's, the
/// meridian flip's) fails here, and the REST GoTo, Center, Park and Home endpoints refuse with
/// <c>polar-align-moving-mount</c> (<c>EquipmentEndpoints.ThrowIfPolarAlignMovesTheMount</c>). Nothing that stops the mount
/// is refused: AbortSlew (Stop Mount, Emergency Stop), a pad stop, tracking off, and the safety, fault and shutdown parks
/// (they call <see cref="ParkAsync"/> and the mediator's park directly and end the routine through its takeover first).
/// </summary>
public sealed partial class TelescopeService {

    private readonly object _ownerGate = new();
    // The live hold (its identity: an older hold of the same owner gives nothing back) and its owner. Guarded by _ownerGate.
    private object? _holdToken;
    private string? _holdOwner;

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

    /// <summary>The mediator's GoTo while a routine holds the mount: refused before anything reaches the mount.</summary>
    private void ThrowIfMotionOwned(string op) {
        if (MotionOwner is { } owner) {
            LogMountOpRejectedOwned(op, owner);
            throw new OpenAstroAra.Core.Model.SequenceEntityFailedException(MotionOwnedMessage(owner, "GoTo"));
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
}
