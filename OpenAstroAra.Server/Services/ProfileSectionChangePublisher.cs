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
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Endpoints;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1807 — marks the profile sections whose <c>profile.updated</c> the running code announces itself, so
/// <see cref="ProfileSectionChangePublisher"/> does not send a second one: the profile route filter announces
/// its own section (<see cref="ProfileEndpoints.PublishUpdatedAfterWrite"/>), and a whole-profile apply
/// (<see cref="ProfileStoreSnapshot.Apply"/>: select, the delete fallback, boot) announces every section, as it
/// published nothing before. A mark ends with its scope: a task started inside it and still writing afterwards
/// writes as the daemon.
/// </summary>
internal static class ProfileWriteScope {
    /// <summary>The mark for every section at once.</summary>
    public const string AllSections = "*";

    private sealed class Mark(string route, Mark? outer) {
        public string Route { get; } = route;
        public Mark? Outer { get; } = outer;
        public volatile bool Open = true;
    }

    private static readonly AsyncLocal<Mark?> Current = new();

    public static IDisposable Announce(string route) {
        var mark = new Mark(route, Current.Value);
        Current.Value = mark;
        return new Closer(mark);
    }

    /// <summary>True while an open scope in this async flow announces <paramref name="route"/> (or every section).</summary>
    public static bool IsAnnounced(string route) {
        for (var mark = Current.Value; mark is not null; mark = mark.Outer) {
            if (mark.Open && (mark.Route == AllSections || string.Equals(mark.Route, route, StringComparison.OrdinalIgnoreCase))) {
                return true;
            }
        }
        return false;
    }

    private sealed class Closer(Mark mark) : IDisposable {
        public void Dispose() {
            // Closed for every flow that captured it, not only this one.
            mark.Open = false;
            if (ReferenceEquals(Current.Value, mark)) {
                Current.Value = mark.Outer;
            }
        }
    }
}

/// <summary>
/// #1807 — one place for <c>profile.updated</c> on a write the profile routes did not make: after every store
/// write (<see cref="IProfileStore.Changed"/>), the section tags (<see cref="ProfileEndpoints.SectionETag"/>, the
/// same hash a client holds) are compared with the last seen, and each client-loadable section whose tag moved
/// is published by its route name, whoever wrote it (time sync, plate solve, a connect-time fill, the flip
/// executor, auto flats, the guider, the dome follower, the filter-wheel label mirror, storage configure).
/// A section the running code announces itself (<see cref="ProfileWriteScope"/>) is not published again; a
/// section with no route (calibration records, the identity record) has no client copy and publishes nothing.
/// It lives as long as the store it listens to (the daemon's lifetime), so it never unsubscribes.
/// Two writers at the same instant: whichever handler takes the gate first claims every section that moved,
/// judged by its own scope, so a daemon write landing inside a route write of the same section can go out
/// as the route's event (or twice). Accepted: one client drives the rig at a time (ADR 0005), and daemon
/// writes are rare (connect, solve, flip).
/// </summary>
internal sealed partial class ProfileSectionChangePublisher {
    private readonly IProfileStore _store;
    private readonly IWsBroadcaster _ws;
    private readonly IReadOnlyDictionary<ProfileSection, string> _routes;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<ProfileSection, string> _tags = new();

    /// <param name="routes">Each client-loadable section's route name, built once at startup so no writer's
    /// thread ever pays for reading the endpoint table.</param>
    public ProfileSectionChangePublisher(IProfileStore store, IWsBroadcaster ws,
            IReadOnlyDictionary<ProfileSection, string> routes, ILogger? logger = null) {
        _store = store;
        _ws = ws;
        _routes = routes;
        _logger = logger ?? NullLogger.Instance;
        foreach (var section in ProfileSections.All) {
            _tags[section] = ProfileEndpoints.SectionETag(section, store);
        }
        _store.Changed += OnChanged;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Raised synchronously inside the writer's Put after the write landed: a failed publish must not fail the write. Logged.")]
    private void OnChanged(object? sender, EventArgs e) {
        try {
            List<ProfileSection>? moved = null;
            // Cost: every store write hashes every section here, on the writer's thread. All daemon writers
            // are rare (connect, solve-learn, flip confirm, guiding assistant, dome, label mirror). A writer
            // on a per-frame or per-poll path would pay this each time: give it its own event instead.
            lock (_gate) {
                foreach (var section in ProfileSections.All) {
                    var tag = ProfileEndpoints.SectionETag(section, _store);
                    if (_tags.TryGetValue(section, out var before) && before == tag) {
                        continue;
                    }
                    _tags[section] = tag;
                    (moved ??= []).Add(section);
                }
            }
            if (moved is null || ProfileWriteScope.IsAnnounced(ProfileWriteScope.AllSections)) {
                return;
            }
            foreach (var route in moved.Select(s => _routes.GetValueOrDefault(s)).OfType<string>().Distinct()) {
                if (!ProfileWriteScope.IsAnnounced(route)) {
                    _ = ProfileEndpoints.PublishSectionUpdatedAsync(_ws, route);
                }
            }
        } catch (Exception ex) {
            LogPublishFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not publish profile.updated for a daemon write of the profile")]
    private partial void LogPublishFailed(Exception ex);
}
