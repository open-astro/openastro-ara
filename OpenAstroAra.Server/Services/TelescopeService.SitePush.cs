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
        void SetLatitude(double v);
        void SetLongitude(double v);
        void SetElevation(double v);
    }

    private sealed class AlpacaMountSite(AlpacaTelescope mount) : IMountSite {
        public double Latitude => mount.SiteLatitude;
        public double Longitude => mount.SiteLongitude;
        public double Elevation => mount.SiteElevation;
        public void SetLatitude(double v) => mount.SiteLatitude = v;
        public void SetLongitude(double v) => mount.SiteLongitude = v;
        public void SetElevation(double v) => mount.SiteElevation = v;
    }

    private OpenAstroAra.Server.Services.IProfileStore? _siteStore;

    /// <summary>Write [lat, lng, elev] into the mount, skipping values it already holds; each value
    /// is tried on its own, so one the driver refuses doesn't stop the rest. False when any failed.</summary>
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
        One(() => mount.Elevation, mount.SetElevation, elev);
        return ok;
    }

    /// <summary>Whether the profile holds a site: the unset default (0°, 0°) is never pushed.</summary>
    internal static bool HasSite(double lat, double lng) => lat != 0 || lng != 0;

    /// <summary>Follow the profile: push its site now (if a mount is connected) and on every change.</summary>
    internal void FollowProfileSite(OpenAstroAra.Server.Services.IProfileStore store) {
        _siteStore = store;
        store.Changed += (_, _) => PushProfileSite();
    }

    /// <summary>Push the profile's site to the connected mount, off the caller's thread (each write
    /// is an HTTP round trip to the mount).</summary>
    private void PushProfileSite() {
        var store = _siteStore;
        if (store is null) {
            return;
        }
        AlpacaTelescope? client;
        lock (_gate) {
            client = _state == OpenAstroAra.Server.Contracts.EquipmentConnectionState.Connected ? _client : null;
        }
        if (client is null) {
            return;
        }
        var site = store.GetSiteSettings();
        if (!HasSite(site.LatitudeDeg, site.LongitudeDeg)) {
            return; // a profile with no site yet must not overwrite the mount's real one
        }
        _ = Task.Run(() => {
            if (!PushSite(new AlpacaMountSite(client), site.LatitudeDeg, site.LongitudeDeg, site.ElevationM)) {
                LogSitePushIncomplete(site.LatitudeDeg, site.LongitudeDeg, site.ElevationM);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The mount did not take all of the profile's site ({Lat}, {Lng}, {Elev} m); it keeps its own for those")]
    private partial void LogSitePushIncomplete(double lat, double lng, double elev);
}
