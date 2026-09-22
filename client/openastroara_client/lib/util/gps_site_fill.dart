import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:geolocator/geolocator.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/client_gps_state.dart';
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
  }) : success = true,
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

/// The platforms the client ships on, for user-facing copy. Detected from
/// `dart:io`; the copy helpers below are pure functions of it so tests can
/// pin any platform without a process-wide override.
enum ClientPlatform { macOS, windows, linux, android, iOS }

/// The platform this client is running on, as the copy helpers see it.
ClientPlatform get clientPlatform {
  if (Platform.isMacOS) return ClientPlatform.macOS;
  if (Platform.isWindows) return ClientPlatform.windows;
  if (Platform.isAndroid) return ClientPlatform.android;
  if (Platform.isIOS) return ClientPlatform.iOS;
  return ClientPlatform.linux;
}

/// What to call the machine running the client, in user-facing copy. The app
/// ships on macOS, Windows, Linux, Android and iOS, so "this Mac" is wrong
/// most of the time.
@visibleForTesting
String thisDeviceLabel(ClientPlatform p) => switch (p) {
  ClientPlatform.macOS => 'this Mac',
  ClientPlatform.windows => 'this PC',
  ClientPlatform.linux => 'this computer',
  ClientPlatform.android => 'this Android device',
  ClientPlatform.iOS => 'this iPhone or iPad',
};

String get _thisDevice => thisDeviceLabel(clientPlatform);

/// Where the user goes to grant location access, per platform. Linux has no
/// REGISTERED geolocator implementation — geolocator 14 ships a GeoClue backend
/// (`geolocator_linux`), but it is absent from the checked-in
/// linux/flutter/generated_plugin_registrant.cc, so a call there still lands on
/// a missing implementation. Until that plugin is registered and tested on a
/// Linux box, Linux gets the honest answer instead of a settings path that
/// doesn't exist there.
@visibleForTesting
String permissionHint(ClientPlatform p) => switch (p) {
  ClientPlatform.macOS =>
    'Open System Settings → Privacy & Security → Location Services and '
        'allow OpenAstro Ara, then click Fill from GPS again.',
  ClientPlatform.windows =>
    'Open Settings → Privacy & security → Location and allow desktop '
        'apps to access your location, then click Fill from GPS again.',
  ClientPlatform.linux =>
    'On Linux there is no system location service to fall back on — '
        'plug a USB GPS dongle into this computer (Settings → Site → GPS on '
        'this computer) or into the machine running Ara Server.',
  ClientPlatform.android =>
    'Open Settings → Apps → OpenAstro Ara → Permissions → Location and '
        'allow it while using the app, then tap Fill from GPS again.',
  ClientPlatform.iOS =>
    'Open Settings → Privacy & Security → Location Services → OpenAstro '
        'Ara and allow While Using the App, then tap Fill from GPS again.',
};

/// Why a fix may be missing or stale, per platform: desktops position by
/// network, phones and tablets carry a real GPS receiver.
@visibleForTesting
String noFixHint(ClientPlatform p) => switch (p) {
  ClientPlatform.android || ClientPlatform.iOS =>
    'Make sure Location is on and try again with a clear view of the '
        'sky, or plug a USB GPS dongle into the machine running Ara Server.',
  _ =>
    'Desktop location needs a network connection — connect to one, or '
        'plug a USB GPS dongle into this computer (Settings → Site → GPS on '
        'this computer) or into the machine running Ara Server.',
};

String get _noFixHint => noFixHint(clientPlatform);

String get _permissionHint => permissionHint(clientPlatform);

/// Try to fill an observing site from GPS, in this order: (1) a USB GPS
/// dongle on the server machine (§31.3 time-sync state); (2) a USB GPS dongle
/// on THIS computer when "GPS on this computer" is enabled (a fresh fix from
/// the background loop, or one read now); (3) **the client machine's own
/// location** (macOS/Windows/Android/iOS; Linux has no registered geolocator
/// backend), accepting only a fix less than ten minutes old. This one routine
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

  // 2) A USB GPS dongle on THIS computer (Settings → Site → GPS on this
  // computer). Reuse a fresh fix the background loop already has; otherwise
  // read one now. Ahead of the device-location fallback because a receiver
  // fix is better than a Wi-Fi geolocation guess.
  // Awaited, not `.value`: the provider builds lazily (it reads a prefs file), so
  // the first Fill from GPS of a session would otherwise see "loading" and skip.
  ClientGpsStatus? clientGps;
  try {
    clientGps = await ref
        .read(clientGpsProvider.future)
        .then<ClientGpsStatus?>((v) => v)
        .timeout(const Duration(seconds: 3), onTimeout: () => null);
  } catch (_) {
    clientGps = null; // prefs unreadable → treat as disabled
  }
  if (clientGps != null && clientGps.enabled) {
    final notifier = ref.read(clientGpsProvider.notifier);
    final fix = clientGps.freshFix(DateTime.now().toUtc()) ? clientGps.lastFix : await notifier.syncNow();
    if (fix != null && fix.hasPosition) {
      return GpsSiteFill.success(
        lat: fix.latitudeDeg!,
        lng: fix.longitudeDeg!,
        alt: fix.altitudeM,
        sourceLabel: 'the GPS dongle on $_thisDevice',
      );
    }
  }

  // 3) Fallback: this machine's own location (a fresh fix is required).
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
          '$_permissionHint',
        );
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
        'old), so it was not filled. $_noFixHint',
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
    // network-positioned (no network, no fix); a phone or tablet needs sky.
    return GpsSiteFill.failed(
      '$baseNote $_thisDevice couldn\'t fix a position in time. $_noFixHint',
    );
  } catch (_) {
    // Any other platform failure (e.g. no registered geolocator backend, as on
    // Linux) → a clear message that tells the user exactly how to make this
    // machine's location available.
    return GpsSiteFill.failed(
      '$baseNote $_thisDevice couldn\'t provide a location. $_permissionHint',
    );
  }
}
