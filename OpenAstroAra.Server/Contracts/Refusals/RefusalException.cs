#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Diagnostics.CodeAnalysis;

namespace OpenAstroAra.Server.Contracts.Refusals;

/// <summary>
/// A module declining a request for a declared <see cref="RefusalCause"/> (ADR 0022). <c>RefusalHandler</c>
/// answers it as problem+json with <c>reason</c> = the cause and <see cref="Parameters"/> as extensions; the
/// message is the problem's <c>detail</c>. It derives from <see cref="InvalidOperationException"/>, like
/// <c>CameraUnavailableException</c>, so a caller not yet moved to refusals that catches that type still catches it.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A refusal without a declared cause would have no reason to answer with, so the standard parameterless and message-only constructors are left out.")]
public sealed class RefusalException : InvalidOperationException {
    public RefusalCause Cause { get; }
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    public RefusalException(RefusalCause cause, string detail, IReadOnlyDictionary<string, object?>? parameters = null) : base(detail) {
        Cause = cause;
        Parameters = parameters ?? new Dictionary<string, object?>();
        foreach (var name in Parameters.Keys) {
            if (!cause.ParameterNames.Contains(name)) {
                throw new ArgumentException($"The refusal '{cause.Token}' does not declare a parameter '{name}'.", nameof(parameters));
            }
        }
    }
}
