import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import '../models/server.dart';

/// §36.8 framing fit of an object against the active optical train: does it
/// sit comfortably in the sensor's field of view? Wire values are all-lowercase
/// (§60.6); anything unrecognised maps to [unknown] so a new server enum can't
/// crash an older client.
enum TonightFraming {
  unknown,
  tooSmall,
  good,
  tooBig;

  static TonightFraming fromWire(Object? raw) => switch (raw) {
        'toosmall' => TonightFraming.tooSmall,
        'good' => TonightFraming.good,
        'toobig' => TonightFraming.tooBig,
        _ => TonightFraming.unknown,
      };
}

/// §36/§25.5 — one Tonight's Sky entry from `GET /api/v1/planning/tonight`:
/// a deep-sky object ranked by its equipment-aware [score] (0–100, server-ranked
/// descending). [altitudeDeg] is its current altitude above the site horizon;
/// [maxAltitudeDeg] is its highest possible (transit) altitude from this
/// latitude; [raDeg]/[decDeg] (J2000) let the atlas recentre on it. The §36.8
/// planner fields ([framing], the window/transit times, the hours, the size and
/// surface-brightness measurements, [scoreReasons]) are optional on the wire —
/// older servers omit them, so they are nullable / defaulted, never required.
class TonightSkyObject {
  final String id;
  final String name;
  final String type;

  /// Visual magnitude, or null when the server omits/mangles it — kept nullable
  /// so a bad value can't masquerade as a genuine `mag 0.0` (≈ Vega).
  final double? magnitude;
  final double raDeg;
  final double decDeg;
  final double altitudeDeg;
  final double maxAltitudeDeg;

  // §36.8 catalog measurements (nullable — not every object records a size).
  final double? sizeMajArcmin;
  final double? sizeMinArcmin;
  final double? posAngleDeg;
  final double? surfaceBrightness;

  // §36.8 tonight's longest dark window, its transit, and the hours. The times
  // are UTC instants (parse to local for display); the hours default to 0 when
  // a pre-§36.8 server omits them so the UI can simply hide a zero.
  final DateTime? windowStartUtc;
  final DateTime? windowEndUtc;
  final DateTime? transitUtc;
  final double integrationHours;
  final double remainingHours;

  /// Equipment-aware fit against the active optical train ([TonightFraming]).
  final TonightFraming framing;

  /// Transparent 0–100 "worth shooting tonight" blend — the ranking key.
  final double score;

  /// Short component tags ("fills the frame (+35)", "5 h dark window (+21)")
  /// that explain the score; null when the server omits them.
  final List<String>? scoreReasons;

  const TonightSkyObject({
    required this.id,
    required this.name,
    required this.type,
    required this.magnitude,
    required this.raDeg,
    required this.decDeg,
    required this.altitudeDeg,
    required this.maxAltitudeDeg,
    this.sizeMajArcmin,
    this.sizeMinArcmin,
    this.posAngleDeg,
    this.surfaceBrightness,
    this.windowStartUtc,
    this.windowEndUtc,
    this.transitUtc,
    this.integrationHours = 0,
    this.remainingHours = 0,
    this.framing = TonightFraming.unknown,
    this.score = 0,
    this.scoreReasons,
  });

  /// Parse one wire object, or null when a required field is missing/wrong-typed
  /// (so a malformed row is skipped, not crashed on). The string identity (id /
  /// name / type), the position (ra/dec — a bad value would be a real-but-wrong
  /// (0,0) sky spot), and the altitudes (a bad value would render as a misleading
  /// "0°" on the horizon, and altitude is the ranking key) are all required;
  /// only `magnitude` is optional — left null rather than defaulting to the real
  /// value 0 (≈ Vega). The §36.8 planner fields are parsed defensively: a
  /// missing/wrong-typed value falls back to null (measurements/times) or a
  /// sensible default (hours 0, framing unknown, score 0), never throwing.
  static TonightSkyObject? fromJson(Map<String, dynamic> json) {
    final id = json['id'];
    final name = json['name'];
    final type = json['type'];
    if (id is! String || name is! String || type is! String) return null;
    final ra = json['ra_deg'];
    final dec = json['dec_deg'];
    if (ra is! num || dec is! num) return null;
    final alt = json['altitude_deg'];
    final maxAlt = json['max_altitude_deg'];
    if (alt is! num || maxAlt is! num) return null;
    final mag = json['magnitude'];
    return TonightSkyObject(
      id: id,
      name: name,
      type: type,
      magnitude: mag is num ? mag.toDouble() : null,
      raDeg: ra.toDouble(),
      decDeg: dec.toDouble(),
      altitudeDeg: alt.toDouble(),
      maxAltitudeDeg: maxAlt.toDouble(),
      sizeMajArcmin: _optDouble(json['size_maj_arcmin']),
      sizeMinArcmin: _optDouble(json['size_min_arcmin']),
      posAngleDeg: _optDouble(json['pos_angle_deg']),
      surfaceBrightness: _optDouble(json['surface_brightness']),
      windowStartUtc: _optUtc(json['window_start_utc']),
      windowEndUtc: _optUtc(json['window_end_utc']),
      transitUtc: _optUtc(json['transit_utc']),
      integrationHours: _optDouble(json['integration_hours']) ?? 0,
      remainingHours: _optDouble(json['remaining_hours']) ?? 0,
      framing: TonightFraming.fromWire(json['framing']),
      // Clamp at the data layer so the 0–100 invariant holds for every consumer
      // (comparators, tests, analytics), not just the display badge.
      score: (_optDouble(json['score']) ?? 0).clamp(0.0, 100.0).toDouble(),
      scoreReasons: _optStringList(json['score_reasons']),
    );
  }

  static double? _optDouble(Object? v) => v is num ? v.toDouble() : null;

  /// Parse an ISO-8601 instant, normalised to UTC; null on absent/garbage so a
  /// bad timestamp simply drops out of the UI rather than rendering nonsense.
  static DateTime? _optUtc(Object? v) {
    if (v is! String) return null;
    final parsed = DateTime.tryParse(v);
    return parsed?.toUtc();
  }

  /// Keep only the string entries — a non-list or a list with stray non-strings
  /// shouldn't blow up the "why this score" expansion.
  static List<String>? _optStringList(Object? v) {
    if (v is! List) return null;
    final out = <String>[for (final e in v) if (e is String) e];
    return out.isEmpty ? null : out;
  }
}

class TonightSkyApi {
  final Dio _dio;

  /// [dio] is injectable so tests can stub the transport with a fake adapter;
  /// production callers pass only the server and get a configured one-shot Dio.
  TonightSkyApi(AraServer server, {Dio? dio})
      : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ));

  /// The curated objects above the active profile's site horizon right now,
  /// highest first (server-ranked). Throws `DioException` on transport failure.
  /// The owning provider holds the lifecycle — call [close] when done with it.
  Future<List<TonightSkyObject>> fetch({int limit = 12}) async {
    final res = await _dio.get<dynamic>(
      '/api/v1/planning/tonight',
      queryParameters: <String, dynamic>{'limit': limit},
    );
    // A well-behaved server returns a JSON array; anything else (an error object,
    // an HTML body, null) is not iterable — treat it as "nothing to show" rather
    // than throwing a TypeError out of the provider. Dio already throws on 4xx/5xx,
    // so this only fires on a 200 with an unexpected shape — log it so that's
    // diagnosable rather than a silent empty list.
    final raw = res.data;
    if (raw is! List) {
      debugPrint('TonightSkyApi: unexpected 200 body (${raw.runtimeType}); '
          'expected a JSON array — showing no objects.');
      return const <TonightSkyObject>[];
    }
    final out = <TonightSkyObject>[];
    for (final e in raw) {
      if (e is Map<String, dynamic>) {
        final o = TonightSkyObject.fromJson(e);
        if (o != null) out.add(o);
      }
    }
    return out;
  }

  /// Release the underlying connection. `force: true` cancels any in-flight
  /// request, so the provider can call this from `onDispose` to drop a pending
  /// fetch when the panel closes rather than waiting out the receive timeout.
  void close() => _dio.close(force: true);
}
