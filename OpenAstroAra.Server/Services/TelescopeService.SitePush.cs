#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Alpaca.Clients;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

// The profile is the site's one source of truth (user, 2026-11-03): the daemon writes it into the
// mount on connect and whenever the profile changes, so every Alpaca client reads Ara's site from
// the mount and nobody edits it there. (AlpacaBridge persists a client-set site itself while its
// device entry's learnSiteFromClient is on, the default.)
public sealed partial class TelescopeService {

    /// <summary>The mount's site as Alpaca exposes it (a seam over <see cref="AlpacaTelescope"/>).</summary>
    internal interface IMountSite {
        double Latitude { get; }
        double Longitude { get; }
        double Elevation { get; }
        bool Slewing { get; }
        void SetLatitude(double v);
        void SetLongitude(double v);
        void SetElevation(double v);
    }

    private sealed class AlpacaMountSite(AlpacaTelescope mount) : IMountSite {
        public double Latitude => mount.SiteLatitude;
        public double Longitude => mount.SiteLongitude;
        public double Elevation => mount.SiteElevation;
        public bool Slewing => mount.Slewing;
        public void SetLatitude(double v) => mount.SiteLatitude = v;
        public void SetLongitude(double v) => mount.SiteLongitude = v;
        public void SetElevation(double v) => mount.SiteElevation = v;
    }

    private OpenAstroAra.Server.Services.IProfileStore? _siteStore;
    // One push at a time, and the site last written in full, so a profile save that isn't the site
    // (autofocus, storage, a saved target…) makes no Alpaca round trip. The gate is awaited, not
    // locked: a push waiting out a slew holds no pool thread (review of #1486).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "Only WaitAsync/Release are used, never AvailableWaitHandle, so it holds no OS handle; disposing it under a push still in flight would throw from that push's Release.")]
    private readonly SemaphoreSlim _sitePushGate = new(1, 1);
    private volatile SiteValue? _sitePushed;

    private sealed record SiteValue(double Lat, double Lng, double Elev);

    // Some drivers refuse a site write mid-slew: wait for the slew to end, up to about three minutes.
    private const int SitePushSlewPolls = 180;
    private static readonly TimeSpan SitePushSlewPoll = TimeSpan.FromSeconds(1);

    /// <summary>The connected mount's site, null when none is connected; tests swap in a fake.</summary>
    internal Func<IMountSite?>? MountSiteSource { get; set; }

    /// <summary>Runs a site push off the caller's thread; tests run it inline.</summary>
    internal Func<Func<Task>, Task> RunSitePush { get; set; } = Task.Run;

    /// <summary>The wait between slew checks; tests make it instant.</summary>
    internal Func<TimeSpan, Task> SitePushWait { get; set; } = d => Task.Delay(d);

    /// <summary>The mount went away: the next one gets the site whatever this one held (review of
    /// #1486; every connect also forces a push).</summary>
    private void ForgetPushedSite() => _sitePushed = null;

    /// <summary>Write [lat, lng, elev] into the mount, skipping values it already holds and an elevation
    /// of 0 (unset: the default nobody changed, user 2026-11-03); each value is tried on its own, so one
    /// the driver refuses doesn't stop the rest. False when any failed.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort write boundary: a driver that refuses or can't report a site value (any ASCOM/transport exception) leaves that value to the mount and is reported by the false return.")]
    internal static bool PushSite(IMountSite mount, double lat, double lng, double elev) {
        var ok = true;
        void One(Func<double> read, Action<double> write, double value) {
            try {
                double current;
                try { current = read(); } catch (Exception) { current = double.NaN; }
                if (Math.Abs(current - value) < 1e-7) {
                    return;
                }
                write(value);
            } catch (Exception) {
                ok = false;
            }
        }
        One(() => mount.Latitude, mount.SetLatitude, lat);
        One(() => mount.Longitude, mount.SetLongitude, lng);
        if (elev != 0) {
            One(() => mount.Elevation, mount.SetElevation, elev);
        }
        return ok;
    }

    /// <summary>Whether the profile holds a site: the unset default (0°, 0°) is never pushed.</summary>
    internal static bool HasSite(double lat, double lng) => lat != 0 || lng != 0;

    /// <summary>Follow the profile: push its site into the connected mount on every change (the push on
    /// connect is in ConnectAsync).</summary>
    internal void FollowProfileSite(OpenAstroAra.Server.Services.IProfileStore store) {
        _siteStore = store;
        store.Changed += (_, _) => PushProfileSite();
    }

    /// <summary>Push the profile's site to the connected mount, off the caller's thread (each write
    /// is an HTTP round trip to the mount). A site already written in full is skipped unless
    /// <paramref name="force"/> (a fresh connect: the mount may hold anything).</summary>
    internal void PushProfileSite(bool force = false) {
        var store = _siteStore;
        if (store is null) {
            return;
        }
        var mount = MountSiteSource is { } source ? source() : ConnectedAlpacaSite();
        if (mount is null) {
            return;
        }
        _ = RunSitePush(async () => {
            await _sitePushGate.WaitAsync().ConfigureAwait(false);
            try {
                // Read under the gate, so of two close saves the later push writes the newer site.
                var site = store.GetSiteSettings();
                var wanted = new SiteValue(site.LatitudeDeg, site.LongitudeDeg, site.ElevationM);
                if (!HasSite(wanted.Lat, wanted.Lng) || (!force && _sitePushed == wanted)) {
                    return; // no site yet must not overwrite the mount's real one; an unchanged one is there
                }
                if (!await WaitOutSlewAsync(mount).ConfigureAwait(false)) {
                    return; // still slewing: the next save or connect tries again
                }
                if (PushSite(mount, wanted.Lat, wanted.Lng, wanted.Elev)) {
                    _sitePushed = wanted;
                } else {
                    _sitePushed = null;
                    LogSitePushIncomplete(wanted.Lat, wanted.Lng, wanted.Elev);
                }
            } finally {
                _sitePushGate.Release();
            }
        });
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A mount that can't report Slewing is treated as not slewing: the push's own per-value error handling covers a refused write.")]
    private async Task<bool> WaitOutSlewAsync(IMountSite mount) {
        for (var i = 0; i < SitePushSlewPolls; i++) {
            bool slewing;
            try { slewing = mount.Slewing; } catch (Exception) { slewing = false; }
            if (!slewing) {
                return true;
            }
            await SitePushWait(SitePushSlewPoll).ConfigureAwait(false);
        }
        LogSitePushSlewing();
        return false;
    }

    private AlpacaMountSite? ConnectedAlpacaSite() {
        lock (_gate) {
            return _state == OpenAstroAra.Server.Contracts.EquipmentConnectionState.Connected && _client is { } client
                ? new AlpacaMountSite(client)
                : null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The mount did not take all of the profile's site ({Lat}, {Lng}, {Elev} m); it keeps its own for those")]
    private partial void LogSitePushIncomplete(double lat, double lng, double elev);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "The mount was still slewing; the profile's site goes in on the next save or connect")]
    private partial void LogSitePushSlewing();
}
