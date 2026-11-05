/// One light frame Ara captured, as `GET /api/v1/stats/album-frames` serves it
/// (Sky Album, docs/specs/sky-album-v1.md). Snake_case wire; defensive parse.
library;

import '../../models/stats/stats_time.dart';

double? _dbl(dynamic v) => v is num ? v.toDouble() : null;
int _int(dynamic v) => v is num ? v.toInt() : 0;
String? _str(dynamic v) => v is String && v.isNotEmpty ? v : null;

class AlbumFrame {
  final String frameId;

  /// J2000 pointing; null when the frame was taken in another epoch or with no mount.
  final double? raDeg;
  final double? decDeg;

  /// Stored (binned) image size and arcsec per stored pixel.
  final int width;
  final int height;
  final double? pixelScaleArcsec;

  final DateTime? capturedUtc;
  final double exposureSeconds;
  final String? filterName;
  final String targetName;
  final double? compositeScore;
  final String? profileId;

  /// When the daemon last changed the row; the newest one is the next fetch's `since`.
  final DateTime? updatedUtc;

  const AlbumFrame({
    required this.frameId,
    this.raDeg,
    this.decDeg,
    this.width = 0,
    this.height = 0,
    this.pixelScaleArcsec,
    this.capturedUtc,
    this.exposureSeconds = 0,
    this.filterName,
    this.targetName = '',
    this.compositeScore,
    this.profileId,
    this.updatedUtc,
  });

  factory AlbumFrame.fromJson(Map<String, dynamic> json) => AlbumFrame(
        frameId: _str(json['frame_id']) ?? '',
        raDeg: _dbl(json['ra_deg']),
        decDeg: _dbl(json['dec_deg']),
        width: _int(json['width']),
        height: _int(json['height']),
        pixelScaleArcsec: _dbl(json['pixel_scale_arcsec']),
        capturedUtc: parseStatsUtc(json['captured_utc']),
        exposureSeconds: _dbl(json['exposure_seconds']) ?? 0,
        filterName: _str(json['filter_name']),
        targetName: _str(json['target_name']) ?? '',
        compositeScore: _dbl(json['composite_score']),
        profileId: _str(json['profile_id']),
        updatedUtc: parseStatsUtc(json['updated_utc']),
      );
}
