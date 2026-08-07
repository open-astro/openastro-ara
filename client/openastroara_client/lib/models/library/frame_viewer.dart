import 'dart:typed_data';

/// Server-side stretch algorithms supported by the frame preview endpoint.
enum FrameStretch {
  autoStf('auto_stf', 'Auto STF'),
  linear('linear', 'Linear'),
  log('log', 'Log'),
  asinh('asinh', 'Asinh'),
  sqrt('sqrt', 'Square root'),
  equalized('equalized', 'Histogram'),
  manual('manual', 'Manual');

  final String wireName;
  final String label;
  const FrameStretch(this.wireName, this.label);
}

/// Display plane requested from the server. RGB is the normal OSC view;
/// luminance also works for monochrome frames.
enum FrameChannel {
  rgb('rgb', 'RGB'),
  luminance('luminance', 'Luminance'),
  red('red', 'Red'),
  green('green', 'Green'),
  blue('blue', 'Blue');

  final String wireName;
  final String label;
  const FrameChannel(this.wireName, this.label);
}

/// Immutable render request. The source frame is never modified; every field
/// only selects a cached preview variant.
class FramePreviewOptions {
  final FrameStretch stretch;
  final FrameChannel channel;
  final bool applyDebayer;
  final bool annotateStars;
  final bool invert;
  final double saturation;
  final double blackPoint;
  final double midtonePoint;
  final double whitePoint;
  final double asinhBeta;
  final double linearClipLow;
  final double linearClipHigh;
  final int maxDimensionPx;

  const FramePreviewOptions({
    this.stretch = FrameStretch.autoStf,
    this.channel = FrameChannel.rgb,
    this.applyDebayer = true,
    this.annotateStars = false,
    this.invert = false,
    this.saturation = 1,
    this.blackPoint = 0.02,
    this.midtonePoint = 0.5,
    this.whitePoint = 0.98,
    this.asinhBeta = 3,
    this.linearClipLow = 0.005,
    this.linearClipHigh = 0.995,
    this.maxDimensionPx = 2048,
  });

  FramePreviewOptions copyWith({
    FrameStretch? stretch,
    FrameChannel? channel,
    bool? applyDebayer,
    bool? annotateStars,
    bool? invert,
    double? saturation,
    double? blackPoint,
    double? midtonePoint,
    double? whitePoint,
    double? asinhBeta,
    double? linearClipLow,
    double? linearClipHigh,
    int? maxDimensionPx,
  }) => FramePreviewOptions(
    stretch: stretch ?? this.stretch,
    channel: channel ?? this.channel,
    applyDebayer: applyDebayer ?? this.applyDebayer,
    annotateStars: annotateStars ?? this.annotateStars,
    invert: invert ?? this.invert,
    saturation: saturation ?? this.saturation,
    blackPoint: blackPoint ?? this.blackPoint,
    midtonePoint: midtonePoint ?? this.midtonePoint,
    whitePoint: whitePoint ?? this.whitePoint,
    asinhBeta: asinhBeta ?? this.asinhBeta,
    linearClipLow: linearClipLow ?? this.linearClipLow,
    linearClipHigh: linearClipHigh ?? this.linearClipHigh,
    maxDimensionPx: maxDimensionPx ?? this.maxDimensionPx,
  );

  Map<String, dynamic> toJson() => <String, dynamic>{
    'stretch_palette': stretch.wireName,
    'black_point': blackPoint,
    'midtone_point': midtonePoint,
    'white_point': whitePoint,
    'max_dimension_px': maxDimensionPx,
    'apply_debayer': applyDebayer,
    'channel_mode': channel.wireName,
    'invert': invert,
    'saturation': saturation,
    'asinh_beta': asinhBeta,
    'linear_clip_low': linearClipLow,
    'linear_clip_high': linearClipHigh,
    'annotate_stars': annotateStars,
  };

  @override
  bool operator ==(Object other) =>
      other is FramePreviewOptions &&
      other.stretch == stretch &&
      other.channel == channel &&
      other.applyDebayer == applyDebayer &&
      other.annotateStars == annotateStars &&
      other.invert == invert &&
      other.saturation == saturation &&
      other.blackPoint == blackPoint &&
      other.midtonePoint == midtonePoint &&
      other.whitePoint == whitePoint &&
      other.asinhBeta == asinhBeta &&
      other.linearClipLow == linearClipLow &&
      other.linearClipHigh == linearClipHigh &&
      other.maxDimensionPx == maxDimensionPx;

  @override
  int get hashCode => Object.hash(
    stretch,
    channel,
    applyDebayer,
    annotateStars,
    invert,
    saturation,
    blackPoint,
    midtonePoint,
    whitePoint,
    asinhBeta,
    linearClipLow,
    linearClipHigh,
    maxDimensionPx,
  );
}

/// Exact values applied by the daemon, returned as response headers beside the
/// JPEG. This lets the controls describe the pixels actually on screen.
class FramePreviewApplied {
  final int width;
  final int height;
  final String cacheStatus;
  final String? etag;
  final String algorithm;
  final double? blackPoint;
  final double? midtonePoint;
  final double? whitePoint;
  final double? asinhBeta;
  final double? linearClipLow;
  final double? linearClipHigh;
  final String debayerMode;
  final String channelMode;
  final bool inverted;
  final double saturation;
  final bool annotated;
  final int annotationCount;
  final int rejectedAnnotationCount;
  final String? annotationColor;

  const FramePreviewApplied({
    this.width = 0,
    this.height = 0,
    this.cacheStatus = 'unknown',
    this.etag,
    this.algorithm = 'unknown',
    this.blackPoint,
    this.midtonePoint,
    this.whitePoint,
    this.asinhBeta,
    this.linearClipLow,
    this.linearClipHigh,
    this.debayerMode = 'unknown',
    this.channelMode = 'unknown',
    this.inverted = false,
    this.saturation = 1,
    this.annotated = false,
    this.annotationCount = 0,
    this.rejectedAnnotationCount = 0,
    this.annotationColor,
  });

  factory FramePreviewApplied.fromHeaders(Map<String, String?> headers) {
    final normalized = <String, String?>{
      for (final entry in headers.entries) entry.key.toLowerCase(): entry.value,
    };
    String? value(String name) => normalized[name.toLowerCase()];
    int readInt(String name) => int.tryParse(value(name) ?? '') ?? 0;
    double? readDouble(String name) {
      final parsed = double.tryParse(value(name) ?? '');
      return parsed?.isFinite == true ? parsed : null;
    }

    bool readBool(String name) => value(name)?.toLowerCase() == 'true';

    return FramePreviewApplied(
      width: readInt('x-openastro-preview-width'),
      height: readInt('x-openastro-preview-height'),
      cacheStatus: value('x-openastro-preview-cache') ?? 'unknown',
      etag: value('etag'),
      algorithm: value('x-openastro-stretch') ?? 'unknown',
      blackPoint: readDouble('x-openastro-black-point'),
      midtonePoint: readDouble('x-openastro-mid-point'),
      whitePoint: readDouble('x-openastro-white-point'),
      asinhBeta: readDouble('x-openastro-asinh-beta'),
      linearClipLow: readDouble('x-openastro-clip-low'),
      linearClipHigh: readDouble('x-openastro-clip-high'),
      debayerMode: value('x-openastro-debayer') ?? 'unknown',
      channelMode: value('x-openastro-channel') ?? 'unknown',
      inverted: readBool('x-openastro-inverted'),
      saturation: readDouble('x-openastro-saturation') ?? 1,
      annotated: readBool('x-openastro-annotated'),
      annotationCount: readInt('x-openastro-annotation-count'),
      rejectedAnnotationCount: readInt('x-openastro-annotation-rejected'),
      annotationColor: value('x-openastro-annotation-color'),
    );
  }
}

class FramePreviewImage {
  final Uint8List bytes;
  final FramePreviewApplied applied;

  const FramePreviewImage({required this.bytes, required this.applied});
}

/// Full catalog row nested inside GET /frames/{id}/metadata.
class FrameMetadataItem {
  final String id;
  final String sessionId;
  final String targetName;
  final String frameType;
  final String? filterName;
  final double exposureSeconds;
  final int? gain;
  final int? offset;
  final double? temperatureC;
  final DateTime capturedUtc;
  final int fileSizeBytes;
  final int width;
  final int height;
  final int bitDepth;
  final double? hfr;
  final int? starCount;
  final double? eccentricity;
  final double? guidingRmsArcsec;
  final double? snrEstimate;
  final int rating;
  final List<String> tags;
  final int? focuserPosition;
  final String? analysisVersion;
  final DateTime? quarantinedUtc;
  final String? quarantineReason;

  const FrameMetadataItem({
    required this.id,
    required this.sessionId,
    required this.targetName,
    required this.frameType,
    required this.filterName,
    required this.exposureSeconds,
    required this.gain,
    required this.offset,
    required this.temperatureC,
    required this.capturedUtc,
    required this.fileSizeBytes,
    required this.width,
    required this.height,
    required this.bitDepth,
    required this.hfr,
    required this.starCount,
    required this.eccentricity,
    required this.guidingRmsArcsec,
    required this.snrEstimate,
    required this.rating,
    required this.tags,
    required this.focuserPosition,
    required this.analysisVersion,
    required this.quarantinedUtc,
    required this.quarantineReason,
  });

  factory FrameMetadataItem.fromJson(Map<String, dynamic> json) =>
      FrameMetadataItem(
        id: _string(json['id']),
        sessionId: _string(json['session_id']),
        targetName: _string(json['target_name'], fallback: '(unknown)'),
        frameType: _string(json['frame_type'], fallback: 'light'),
        filterName: _nullableString(json['filter_name']),
        exposureSeconds: _double(json['exposure_seconds']),
        gain: _intOrNull(json['gain']),
        offset: _intOrNull(json['offset']),
        temperatureC: _doubleOrNull(json['temperature_c']),
        capturedUtc: _date(json['captured_utc']),
        fileSizeBytes: _int(json['file_size_bytes']),
        width: _int(json['width']),
        height: _int(json['height']),
        bitDepth: _int(json['bit_depth']),
        hfr: _doubleOrNull(json['hfr']),
        starCount: _intOrNull(json['star_count']),
        eccentricity: _doubleOrNull(json['eccentricity']),
        guidingRmsArcsec: _doubleOrNull(json['guiding_rms_arcsec']),
        snrEstimate: _doubleOrNull(json['snr_estimate']),
        rating: _int(json['rating']),
        tags: ((json['tags'] as List?) ?? const <Object?>[])
            .whereType<String>()
            .toList(growable: false),
        focuserPosition: _intOrNull(json['focuser_position']),
        analysisVersion: _nullableString(json['analysis_version']),
        quarantinedUtc: _dateOrNull(json['quarantined_utc']),
        quarantineReason: _nullableString(json['quarantine_reason']),
      );

  FrameMetadataItem copyWith({
    int? rating,
    List<String>? tags,
    DateTime? Function()? quarantinedUtc,
    String? Function()? quarantineReason,
  }) => FrameMetadataItem(
    id: id,
    sessionId: sessionId,
    targetName: targetName,
    frameType: frameType,
    filterName: filterName,
    exposureSeconds: exposureSeconds,
    gain: gain,
    offset: offset,
    temperatureC: temperatureC,
    capturedUtc: capturedUtc,
    fileSizeBytes: fileSizeBytes,
    width: width,
    height: height,
    bitDepth: bitDepth,
    hfr: hfr,
    starCount: starCount,
    eccentricity: eccentricity,
    guidingRmsArcsec: guidingRmsArcsec,
    snrEstimate: snrEstimate,
    rating: rating ?? this.rating,
    tags: tags ?? this.tags,
    focuserPosition: focuserPosition,
    analysisVersion: analysisVersion,
    quarantinedUtc: quarantinedUtc != null
        ? quarantinedUtc()
        : this.quarantinedUtc,
    quarantineReason: quarantineReason != null
        ? quarantineReason()
        : this.quarantineReason,
  );
}

class FrameStorageMetadata {
  final String state;
  final DateTime? acceptedUtc;
  final DateTime? completedUtc;
  final int? byteCount;
  final String? checksumSha256;
  final String imageFormat;
  final String? cfaPattern;
  final String? failureCode;
  final String? failureMessage;
  final DateTime? updatedUtc;

  const FrameStorageMetadata({
    required this.state,
    required this.acceptedUtc,
    required this.completedUtc,
    required this.byteCount,
    required this.checksumSha256,
    required this.imageFormat,
    required this.cfaPattern,
    required this.failureCode,
    required this.failureMessage,
    required this.updatedUtc,
  });

  factory FrameStorageMetadata.fromJson(Map<String, dynamic> json) =>
      FrameStorageMetadata(
        state: _string(json['state'], fallback: 'unknown'),
        acceptedUtc: _dateOrNull(json['accepted_utc']),
        completedUtc: _dateOrNull(json['completed_utc']),
        byteCount: _intOrNull(json['byte_count']),
        checksumSha256: _nullableString(json['checksum_sha256']),
        imageFormat: _string(json['image_format'], fallback: 'unknown'),
        cfaPattern: _nullableString(json['cfa_pattern']),
        failureCode: _nullableString(json['failure_code']),
        failureMessage: _nullableString(json['failure_message']),
        updatedUtc: _dateOrNull(json['updated_utc']),
      );
}

class FrameMetadata {
  final FrameMetadataItem frame;
  final FrameStorageMetadata? storage;
  final bool sourceExists;
  final String? sourceChecksumSha256;
  final String? imageFormat;
  final String? cfaPattern;
  final String? analysisState;
  final String? analysisFailureCode;
  final String? analysisFailureMessage;
  final String? previewState;
  final String? previewFailureCode;
  final String? previewFailureMessage;
  final String? previewChecksum;
  final String? debayerMethod;
  final String? previewVersion;

  const FrameMetadata({
    required this.frame,
    required this.storage,
    required this.sourceExists,
    required this.sourceChecksumSha256,
    required this.imageFormat,
    required this.cfaPattern,
    required this.analysisState,
    required this.analysisFailureCode,
    required this.analysisFailureMessage,
    required this.previewState,
    required this.previewFailureCode,
    required this.previewFailureMessage,
    required this.previewChecksum,
    required this.debayerMethod,
    required this.previewVersion,
  });

  factory FrameMetadata.fromJson(Map<String, dynamic> json) {
    final frame = json['frame'];
    if (frame is! Map<String, dynamic>) {
      throw const FormatException('frame metadata is missing its frame object');
    }
    final storage = json['storage'];
    return FrameMetadata(
      frame: FrameMetadataItem.fromJson(frame),
      storage: storage is Map<String, dynamic>
          ? FrameStorageMetadata.fromJson(storage)
          : null,
      sourceExists: json['source_exists'] == true,
      sourceChecksumSha256: _nullableString(json['source_checksum_sha256']),
      imageFormat: _nullableString(json['image_format']),
      cfaPattern: _nullableString(json['cfa_pattern']),
      analysisState: _nullableString(json['analysis_state']),
      analysisFailureCode: _nullableString(json['analysis_failure_code']),
      analysisFailureMessage: _nullableString(json['analysis_failure_message']),
      previewState: _nullableString(json['preview_state']),
      previewFailureCode: _nullableString(json['preview_failure_code']),
      previewFailureMessage: _nullableString(json['preview_failure_message']),
      previewChecksum: _nullableString(json['preview_checksum']),
      debayerMethod: _nullableString(json['debayer_method']),
      previewVersion: _nullableString(json['preview_version']),
    );
  }

  FrameMetadata copyWith({FrameMetadataItem? frame}) => FrameMetadata(
    frame: frame ?? this.frame,
    storage: storage,
    sourceExists: sourceExists,
    sourceChecksumSha256: sourceChecksumSha256,
    imageFormat: imageFormat,
    cfaPattern: cfaPattern,
    analysisState: analysisState,
    analysisFailureCode: analysisFailureCode,
    analysisFailureMessage: analysisFailureMessage,
    previewState: previewState,
    previewFailureCode: previewFailureCode,
    previewFailureMessage: previewFailureMessage,
    previewChecksum: previewChecksum,
    debayerMethod: debayerMethod,
    previewVersion: previewVersion,
  );
}

class FrameOperationAccepted {
  final String operationId;
  final String operationType;
  final DateTime acceptedUtc;
  final String? idempotencyKey;

  const FrameOperationAccepted({
    required this.operationId,
    required this.operationType,
    required this.acceptedUtc,
    required this.idempotencyKey,
  });

  factory FrameOperationAccepted.fromJson(Map<String, dynamic> json) {
    final id = _string(json['operation_id']);
    if (id.isEmpty) {
      throw const FormatException('operation response has no operation_id');
    }
    return FrameOperationAccepted(
      operationId: id,
      operationType: _string(json['operation_type'], fallback: 'unknown'),
      acceptedUtc: _date(json['accepted_utc']),
      idempotencyKey: _nullableString(json['idempotency_key']),
    );
  }
}

class FrameJobStatus {
  final String jobId;
  final String jobType;
  final String state;
  final int done;
  final int total;
  final DateTime startedUtc;
  final DateTime? finishedUtc;
  final String? errorMessage;

  const FrameJobStatus({
    required this.jobId,
    required this.jobType,
    required this.state,
    required this.done,
    required this.total,
    required this.startedUtc,
    required this.finishedUtc,
    required this.errorMessage,
  });

  factory FrameJobStatus.fromJson(Map<String, dynamic> json) => FrameJobStatus(
    jobId: _string(json['job_id']),
    jobType: _string(json['job_type'], fallback: 'unknown'),
    state: _string(json['state'], fallback: 'unknown'),
    done: _int(json['done']),
    total: _int(json['total']),
    startedUtc: _date(json['started_utc']),
    finishedUtc: _dateOrNull(json['finished_utc']),
    errorMessage: _nullableString(json['error_message']),
  );

  bool get isTerminal =>
      state == 'complete' || state == 'failed' || state == 'cancelled';

  double? get progress => total > 0 ? (done / total).clamp(0, 1) : null;
}

String _string(Object? value, {String fallback = ''}) =>
    value is String && value.isNotEmpty ? value : fallback;
String? _nullableString(Object? value) =>
    value is String && value.isNotEmpty ? value : null;
int _int(Object? value) => _intOrNull(value) ?? 0;
int? _intOrNull(Object? value) {
  if (value is int) return value;
  if (value is! num) return null;
  final number = value.toDouble();
  if (!number.isFinite || number != number.truncateToDouble()) return null;
  return number.toInt();
}

double _double(Object? value) => _doubleOrNull(value) ?? 0;
double? _doubleOrNull(Object? value) {
  if (value is! num) return null;
  final number = value.toDouble();
  return number.isFinite ? number : null;
}

DateTime _date(Object? value) =>
    _dateOrNull(value) ?? DateTime.fromMillisecondsSinceEpoch(0, isUtc: true);
DateTime? _dateOrNull(Object? value) {
  final parsed = DateTime.tryParse(value is String ? value : '');
  return parsed?.toUtc();
}
