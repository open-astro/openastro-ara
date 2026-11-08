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
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Interfaces.Mediator;

namespace OpenAstroAra.Server.Services;

/// <summary>#1667 — moves the focuser by the two filters' offset difference after a filter change, when the profile asks for it.</summary>
public sealed partial class FilterFocusOffset {
    private readonly Func<IFocuserMediator?> _focuser;
    private readonly IProfileStore _profile;
    private readonly ILogger<FilterFocusOffset> _logger;

    public FilterFocusOffset(Func<IFocuserMediator?> focuser, IProfileStore profile, ILogger<FilterFocusOffset> logger) {
        _focuser = focuser;
        _profile = profile;
        _logger = logger;
    }

    public bool Enabled => _profile.GetFilterWheelPolicy().UseFilterWheelOffsets;

    /// <summary>The wheel has landed on <paramref name="to"/>: move the focuser, unless offsets are off, the
    /// previous filter is unknown or the two offsets match. A missing focuser or a failed move only warns.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A focuser fault of any kind must never fail the filter change: it is logged as a warning, and a genuine cancellation still propagates.")]
    public async Task MoveAsync(FilterInfo? from, FilterInfo? to, CancellationToken ct) {
        if (!Enabled || from is null || to is null) {
            return;
        }
        var delta = to.FocusOffset - from.FocusOffset;
        if (delta == 0) {
            return;
        }
        var focuser = _focuser();
        try {
            if (focuser is null || !focuser.GetInfo().Connected) {
                LogNoFocuser(from.Name, to.Name, delta);
                return;
            }
            await focuser.MoveFocuserRelative(delta, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            LogMoveFailed(ex, from.Name, to.Name, delta);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Filter offset not applied ({From} → {To}, {Delta} steps): no focuser is connected")]
    private partial void LogNoFocuser(string from, string to, int delta);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Filter offset not applied ({From} → {To}, {Delta} steps): the focuser move failed")]
    private partial void LogMoveFailed(Exception ex, string from, string to, int delta);
}
