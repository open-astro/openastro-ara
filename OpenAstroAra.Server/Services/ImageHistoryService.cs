#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Sequencer.Interfaces;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §59.5 — the daemon-side <see cref="IImageHistory"/>: an in-memory, session-scoped record of
/// analysed frames and completed autofocus runs that the autofocus trigger family reads.
/// Deliberately NOT persisted — the triggers reason about the CURRENT observing session
/// (temperature drift since the last AF, HFR trend since the last AF); yesterday's points would
/// only mislead them, and the §28 frames catalog already owns the durable record.
///
/// Writers: <see cref="AutofocusSweepService"/> records each successful sweep; per-frame HFR
/// analysis feeds <see cref="RecordImage"/> as capture analysis lands (until then, only
/// triggers that read <see cref="AutofocusPoints"/> see data — the HFR-drift trigger stays
/// quiet, which is the safe direction).
/// </summary>
public sealed class ImageHistoryService : IImageHistory {

    // The HFR trigger looks at "frames since the last autofocus" — a whole night of one-minute
    // subs is a few hundred points, so this bound exists only to keep a pathological session
    // (e.g. rapid-fire short exposures for days) from growing without limit.
    internal const int MaxImagePoints = 2000;

    private readonly object _gate = new();
    private readonly List<ImageHistoryEntry> _images = [];
    private readonly List<AutofocusHistoryEntry> _autofocus = [];
    private long _lastId;

    /// <inheritdoc/>
    public IReadOnlyList<ImageHistoryEntry> ImagePoints {
        get {
            lock (_gate) {
                return _images.ToArray();
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<AutofocusHistoryEntry> AutofocusPoints {
        get {
            lock (_gate) {
                return _autofocus.ToArray();
            }
        }
    }

    /// <summary>
    /// Record one analysed frame. <paramref name="type"/> is the §28 frame type in its
    /// catalog casing ("light", "dark", …) OR NINA's upper-case image type — normalized to
    /// upper-case here because the trigger family compares against NINA's "LIGHT".
    /// </summary>
    public void RecordImage(string type, double hfr, string? filter) {
        ArgumentNullException.ThrowIfNull(type);
        lock (_gate) {
            _images.Add(new ImageHistoryEntry(++_lastId, type.ToUpperInvariant(), hfr, filter));
            if (_images.Count > MaxImagePoints) {
                // Trimming the front never breaks the "Id > lastAF.Id" filtering — ids stay
                // monotonic; old points simply age out.
                _images.RemoveRange(0, _images.Count - MaxImagePoints);
            }
        }
    }

    /// <summary>
    /// Record a completed autofocus run. <paramref name="temperature"/> is the focuser
    /// temperature at completion (NaN when the focuser reports none); <paramref name="filter"/>
    /// the filter the run focused on. The entry's Id is the image-ordinal watermark, so
    /// "since the last autofocus" filtering needs no clock comparison.
    /// </summary>
    public void RecordAutofocus(double temperature, string? filter) {
        lock (_gate) {
            _autofocus.Add(new AutofocusHistoryEntry(_lastId, DateTimeOffset.UtcNow, temperature, filter));
        }
    }
}
