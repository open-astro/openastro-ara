#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.TestHarness.Polling;

/// <summary>
/// Waits for an asynchronously-observed condition to become true.
///
/// The bench drives the real daemon against simulated equipment, so almost
/// nothing it asserts is true the instant the triggering call returns: a state
/// transition lands on a refresh tick, and an event reaches subscribers after
/// the lock guarding that transition is released. Reading once and asserting is
/// a race the test loses whenever the runner is loaded — which is how it
/// presents, as an intermittent CI failure on an unrelated PR.
///
/// Deliberately framework-agnostic: this assembly carries no NUnit reference so
/// the same components can run under the NUnit suite and inside the
/// docker-compose bench. A timeout therefore throws <see cref="TimeoutException"/>
/// rather than calling <c>Assert.Fail</c>; NUnit reports that as a failure with
/// the message intact.
/// </summary>
public static class Poll {
    /// <summary>Default gap between probes. Short enough not to dominate a
    /// deadline, long enough not to spin against an HTTP-backed condition.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true or
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="description">
    /// What was being waited for, phrased to complete "timed out after 10s
    /// waiting for …". This ends up in the CI log and is the difference between
    /// a diagnosable failure and a bare timeout — state which condition did not
    /// hold, not merely that something did not happen.
    /// </param>
    /// <exception cref="TimeoutException">The condition never became true.</exception>
    public static async Task UntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        string description,
        TimeSpan? interval = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var gap = interval ?? DefaultInterval;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) {
            cancellationToken.ThrowIfCancellationRequested();
            if (await condition().ConfigureAwait(false)) {
                return;
            }
            await Task.Delay(gap, cancellationToken).ConfigureAwait(false);
        }

        // One last probe: a condition that became true inside the final gap
        // should not fail purely because the loop exited first.
        if (await condition().ConfigureAwait(false)) {
            return;
        }

        throw new TimeoutException(
            $"timed out after {timeout.TotalSeconds:0.#}s waiting for {description}");
    }

    /// <summary>Synchronous-predicate overload, for conditions that need no await
    /// (reading a list under a lock, checking a flag).</summary>
    public static Task UntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string description,
        TimeSpan? interval = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(condition);
        return UntilAsync(
            () => Task.FromResult(condition()), timeout, description, interval, cancellationToken);
    }
}
