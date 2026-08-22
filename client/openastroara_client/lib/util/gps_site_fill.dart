import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:geolocator/geolocator.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/time_sync_state.dart';

/// The outcome of a "Fill from GPS" attempt: where it succeeded (or exactly why
/// it didn't), plus a human message the caller shows in its own status row.
class GpsSiteFill {
  final bool success;
  final double lat;
  final double lng;
  final double? alt;
  /// On success, a describe-the-source label, e.g.
  /// "the server's GPS dongle" or "this Mac's own location".
  final String sourceLabel;
  /// On failure, a ready-to-show explanation.
  final String message;

  const GpsSiteFill.success({
    required this.lat,
    required this.lng,
    required this.alt,
    required this.sourceLabel,
  })  : success = true,
        message = '';

  const GpsSiteFill.failed(this.message)
      : success = false,
        lat = 0,
        lng = 0,
        alt = null,
        sourceLabel = '';
}

typedef DeviceLocationResult = ({double lat, double lng, double? alt});
typedef DeviceLocationProvider = Future<DeviceLocationResult?> Function();

/// Test seam — replace to force a deterministic device-location outcome in
/// widget tests (real geolocator platform channels aren't available there).
/// Leave null in production.
@visibleForTesting
DeviceLocationProvider? debugMacLocationProvider;

/// What to call the machine running the client, in user-facing copy. The app
/// ships on macOS, Windows and Linux, so "this Mac" is wrong two thirds of
/// the time.
String get _thisDevice => Platform.isMacOS
    ? 'this Mac'
    : Platform.isWindows
        ? 'this PC'
        : 'this computer';

/// Where the user goes to grant location access, per platform. Linux has no
/// geolocator implementation at all, so it gets the honest answer instead of
/// a settings path that doesn't exist there.
String get _permissionHint => Platform.isMacOS
    ? 'Open System Settings → Privacy & Security → Location Services and '
        'allow OpenAstro Ara, then click Fill from GPS again.'
    : Platform.isWindows
        ? 'Open Settings → Privacy & security → Location and allow desktop '
            'apps to access your location, then click Fill from GPS again.'
        : 'On Linux there is no system location service to fall back on — '
            'plug a USB GPS dongle into the machine running Ara Server.';

/// Try to fill an observing site from GPS. **Preferred** source is a USB GPS
/// dongle on the server machine (§31.3 time-sync state); when that's absent
/// (no server, or no fix yet) it falls back to **the client machine's own
/// location** (macOS/Windows; Linux has no geolocator backend), accepting
/// only a fix less than ten minutes old. This one routine
/// is shared by the wizard (profile creation) and the Safety → Site panel
/// (editing), so every "Fill from GPS" behaves the same everywhere.
Future<GpsSiteFill> fillSiteFromGps(WidgetRef ref) async {
  // 1) Preferred: the server's USB GPS dongle fix.
  final api = ref.read(timeSyncApiProvider);
  var dongleReadFailed = false;
  if (api != null) {
    try {
      final state = await api.getState();
      final loc = state.location;
      if (loc != null) {
        return GpsSiteFill.success(
          lat: loc.lat,
          lng: loc.lng,
          alt: loc.alt,
          sourceLabel: "the server's GPS dongle (source: ${state.source})",
        );
      }
    } catch (_) {
      // The dongle can't be read (server unreachable, error) — distinct from
      // "no fix yet", so the user isn't told to wait under open sky.
      dongleReadFailed = true;
    }
  }

  // 2) Fallback: this machine's own location (a fresh fix is required).
  final baseNote = api == null
      ? 'No server connected, '
      : dongleReadFailed
          ? "Couldn't read the server's GPS state, "
          : 'No GPS dongle fix yet, ';

  try {
    // Deterministic test seam first (real platform channels aren't in tests).
    if (debugMacLocationProvider != null) {
      final r = await debugMacLocationProvider!();
      if (r == null) {
        return GpsSiteFill.failed(
            '$baseNote $_thisDevice couldn\'t provide a location. '
            '$_permissionHint');
      }
      return GpsSiteFill.success(
        lat: r.lat,
        lng: r.lng,
        alt: r.alt,
        sourceLabel: '$_thisDevice\'s own location',
      );
    }

    var permission = await Geolocator.checkPermission();
    if (permission == LocationPermission.denied) {
      permission = await Geolocator.requestPermission();
    }
    if (permission == LocationPermission.denied ||
        permission == LocationPermission.deniedForever) {
      return GpsSiteFill.failed(
        '$baseNote $_thisDevice\'s location permission is blocked. '
        '$_permissionHint',
      );
    }

    final pos = await Geolocator.getCurrentPosition(
      locationSettings: const LocationSettings(
        accuracy: LocationAccuracy.medium,
        timeLimit: Duration(seconds: 12),
      ),
    ).timeout(const Duration(seconds: 20));
    final age = DateTime.now().difference(pos.timestamp);
    if (age > const Duration(minutes: 10)) {
      return GpsSiteFill.failed(
        '$baseNote $_thisDevice\'s location is stale (${age.inMinutes} min '
        'old), so it was not filled. Desktop location needs a network to fix '
        'a position — connect to one, or plug in a GPS dongle.',
      );
    }
    // A desktop (Wi-Fi/GPS-less) fix often reports altitude 0.0 or an
    // uncalibrated value — only keep it when the fix carries a decent,
    // VALID accuracy (>= 0; Apple reports negative accuracy when altitude is
    // invalid). Mirrors the dongle path's "unknown altitude → don't
    // overwrite" guard. (A previously-entered real elevation stays put.)
    final altitude = pos.altitudeAccuracy >= 0 && pos.altitudeAccuracy < 100.0
        ? pos.altitude
        : null;
    return GpsSiteFill.success(
      lat: pos.latitude,
      lng: pos.longitude,
      alt: altitude,
      sourceLabel: '$_thisDevice\'s own location',
    );
  } on TimeoutException {
    // Permission was already granted by this point, so blaming permissions
    // here sends the user to the wrong settings pane. Desktop location is
    // network-positioned: no network, no fix.
    return GpsSiteFill.failed(
      '$baseNote $_thisDevice couldn\'t fix a position in time. Desktop '
      'location needs a network connection — connect to one, or plug a USB '
      'GPS dongle into the machine running Ara Server.',
    );
  } catch (_) {
    // Any other platform failure (e.g. no geolocator backend) → a clear message
    // that tells the user exactly how to make the Mac location available.
    return GpsSiteFill.failed(
      '$baseNote $_thisDevice couldn\'t provide a location. $_permissionHint',
    );
  }
}
