import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:path_provider/path_provider.dart';

/// What a target looks like: a DSS2 colour cutout (real photographic sky) of
/// the object at a field of view that shows it in context, fetched from the
/// CDS hips2fits cutout service and cached on disk under app support.
///
/// Offline-first rules apply: the network is only ever a cache FILL. A cached
/// preview is served without touching the network; an uncached one on a
/// dark-site Wi-Fi with no internet degrades to null (the widget shows a
/// "no preview cached" placeholder), never an error or a spinner that hangs.
class TargetPreviewService {
  TargetPreviewService({
    Future<Directory> Function()? supportDir,
    Dio? dio,
  })  : _supportDir = supportDir ?? getApplicationSupportDirectory,
        _dio = dio ??
            Dio(BaseOptions(
              connectTimeout: const Duration(seconds: 6),
              receiveTimeout: const Duration(seconds: 12),
              responseType: ResponseType.bytes,
            ));

  final Future<Directory> Function() _supportDir;
  final Dio _dio;

  static const _dirName = 'target_previews';
  static const cutoutBase =
      'https://alasky.u-strasbg.fr/hips-image-services/hips2fits';
  static const hips = 'CDS/P/DSS2/color';
  static const pixels = 320;

  /// Field of view (degrees) that frames an object of [sizeMajArcmin]: 2.5×
  /// the major axis so the surroundings read, floored at 0.4° so a small
  /// galaxy isn't a lone smudge, capped at 6° (hips2fits' DSS2 usefulness
  /// falls off past that, and the big Sharpless/LDN fields are all inside).
  static double fovDegFor(double? sizeMajArcmin) {
    final size = sizeMajArcmin ?? 0;
    return (size * 2.5 / 60).clamp(0.4, 6.0);
  }

  static Uri cutoutUri(
      {required double raDeg,
      required double decDeg,
      required double fovDeg}) =>
      Uri.parse(cutoutBase).replace(queryParameters: {
        'hips': hips,
        'ra': raDeg.toStringAsFixed(5),
        'dec': decDeg.toStringAsFixed(5),
        'fov': fovDeg.toStringAsFixed(3),
        'width': '$pixels',
        'height': '$pixels',
        'projection': 'TAN',
        'format': 'jpg',
      });

  /// Cache key: the catalog id with anything that isn't filename-safe
  /// replaced, plus the FOV so a changed framing rule doesn't serve stale
  /// crops.
  static String cacheName(String id, double fovDeg) {
    final safe = id.replaceAll(RegExp(r'[^A-Za-z0-9._-]+'), '_');
    return '$safe-${fovDeg.toStringAsFixed(2)}.jpg';
  }

  Future<File> _file(String id, double fovDeg) async {
    final dir = Directory('${(await _supportDir()).path}/$_dirName');
    return File('${dir.path}/${cacheName(id, fovDeg)}');
  }

  /// The cached preview bytes, or null when never fetched / unreadable.
  Future<Uint8List?> cached(String id, {double? sizeMajArcmin}) async {
    try {
      final f = await _file(id, fovDegFor(sizeMajArcmin));
      if (!await f.exists()) return null;
      final bytes = await f.readAsBytes();
      return bytes.isEmpty ? null : bytes;
    } catch (_) {
      return null;
    }
  }

  /// Cached bytes if present, else fetch + cache. Null when offline or the
  /// service fails — callers render a placeholder, never an error.
  Future<Uint8List?> load({
    required String id,
    required double raDeg,
    required double decDeg,
    double? sizeMajArcmin,
  }) async {
    final fov = fovDegFor(sizeMajArcmin);
    final hit = await cached(id, sizeMajArcmin: sizeMajArcmin);
    if (hit != null) return hit;
    try {
      final r = await _dio.getUri<List<int>>(
          cutoutUri(raDeg: raDeg, decDeg: decDeg, fovDeg: fov),
          options: Options(responseType: ResponseType.bytes));
      final data = r.data;
      if (r.statusCode != 200 || data == null || data.isEmpty) return null;
      final bytes = Uint8List.fromList(data);
      try {
        final f = await _file(id, fov);
        await f.parent.create(recursive: true);
        // Write-then-rename so a torn write can't leave a half image that
        // `cached` would happily serve.
        final tmp = File('${f.path}.part');
        await tmp.writeAsBytes(bytes, flush: true);
        await tmp.rename(f.path);
      } catch (_) {
        // Cache write failures are not the caller's problem.
      }
      return bytes;
    } catch (_) {
      return null;
    }
  }
}
