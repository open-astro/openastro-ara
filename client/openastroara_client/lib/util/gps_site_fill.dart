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
  /// "the server's GPS dongle" or "this Mac's location".
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

typedef MacLocationResult = ({double lat, double lng, double? alt});
typedef MacLocationProvider = Future<MacLocationResult?> Function();
typedef InternetProbe = Future<bool> Function();

/// Test seams — replace to force a deterministic Mac-location / internet
/// outcome in widget tests (real geolocator/platform channels aren't available
/// there). Leave null in production.
@visibleForTesting
MacLocationProvider? debugMacLocationProvider;
@visibleForTesting
InternetProbe? debugInternetProbe;

/// Try to fill an observing site from GPS. **Preferred** source is a USB GPS
/// dongle on the server machine (§31.3 time-sync state); when that's absent
/// (no server, or no fix yet) it falls back to **this Mac's own location** —
/// but only when there's internet AND the Mac's fix is fresh. This one routine
/// is shared by the wizard (profile creation) and the Safety → Site panel
/// (editing), so every "Fill from GPS" behaves the same everywhere.
Future<GpsSiteFill> fillSiteFromGps(WidgetRef ref) async {
  // 1) Preferred: the server's USB GPS dongle fix.
  final api = ref.read(timeSyncApiProvider);
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
      // Ignore — fall through to the Mac path below.
    }
  }

  // 2) Fallback: this Mac's own location (internet + fresh fix required).
  final baseNote = api == null
      ? 'No server connected, '
      : 'No GPS dongle fix yet, ';

  try {
    // Deterministic test seam first (real platform channels aren't in tests).
    if (debugMacLocationProvider != null) {
      final r = await debugMacLocationProvider!();
      if (r == null) {
        return GpsSiteFill.failed(
            '$baseNote this Mac\'s location was unavailable. Check System '
            'Settings → Privacy & Security → Location Services → allow '
            'OpenAstro Ara, be online, then click Fill from GPS again.');
      }
      return GpsSiteFill.success(
        lat: r.lat,
        lng: r.lng,
        alt: r.alt,
        sourceLabel: 'this Mac\'s location (internet, fix up to date)',
      );
    }

    final internet =
        debugInternetProbe != null ? await debugInternetProbe!() : await _hasInternet();
    if (!internet) {
      return GpsSiteFill.failed(
        '$baseNote no internet was detected, so this Mac couldn\'t get a '
        'current location. Plug in a GPS dongle or connect to the internet.',
      );
    }

    var permission = await Geolocator.checkPermission();
    if (permission == LocationPermission.denied) {
      permission = await Geolocator.requestPermission();
    }
    if (permission == LocationPermission.denied ||
        permission == LocationPermission.deniedForever) {
      return GpsSiteFill.failed(
        '$baseNote this Mac\'s location permission is blocked. Open System '
        'Settings → Privacy & Security → Location Services → allow OpenAstro '
        'Ara, then click Fill from GPS again.',
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
        '$baseNote this Mac\'s location is stale (${age.inMinutes} min old), '
        'so it was not filled.',
      );
    }
    return GpsSiteFill.success(
      lat: pos.latitude,
      lng: pos.longitude,
      alt: pos.altitude,
      sourceLabel: 'this Mac\'s location (internet, fix up to date)',
    );
  } catch (_) {
    // Any platform failure (e.g. no geolocator registered) → a clear message
    // that tells the user exactly how to make the Mac location available.
    return GpsSiteFill.failed(
      '$baseNote this Mac\'s location was unavailable. Check System Settings → '
      'Privacy & Security → Location Services → allow OpenAstro Ara, be '
      'online, then click Fill from GPS again.',
    );
  }
}

/// Quick reachability probe — the Mac's location fix is only trusted when
/// there's internet (CoreLocation's Wi-Fi/GPS positioning needs the network).
Future<bool> _hasInternet() async {
  final client = HttpClient()..connectionTimeout = const Duration(seconds: 6);
  try {
    final req = await client
        .getUrl(Uri.parse('https://www.gstatic.com/generate_204'))
        .timeout(const Duration(seconds: 6));
    final res = await req.close().timeout(const Duration(seconds: 6));
    return res.statusCode == 204;
  } catch (_) {
    return false;
  } finally {
    client.close(force: true);
  }
}
