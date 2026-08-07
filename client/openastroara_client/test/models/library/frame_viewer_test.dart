import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/library/frame_viewer.dart';

void main() {
  test('preview options serialize every non-destructive render control', () {
    const options = FramePreviewOptions(
      stretch: FrameStretch.asinh,
      channel: FrameChannel.green,
      applyDebayer: true,
      annotateStars: true,
      invert: true,
      saturation: 1.25,
      blackPoint: 0.03,
      midtonePoint: 0.42,
      whitePoint: 0.97,
      asinhBeta: 4.5,
      linearClipLow: 0.01,
      linearClipHigh: 0.99,
      maxDimensionPx: 3072,
    );

    expect(options.toJson(), {
      'stretch_palette': 'asinh',
      'black_point': 0.03,
      'midtone_point': 0.42,
      'white_point': 0.97,
      'max_dimension_px': 3072,
      'apply_debayer': true,
      'channel_mode': 'green',
      'invert': true,
      'saturation': 1.25,
      'asinh_beta': 4.5,
      'linear_clip_low': 0.01,
      'linear_clip_high': 0.99,
      'annotate_stars': true,
    });
  });

  test('preview response headers preserve exact applied values', () {
    final applied = FramePreviewApplied.fromHeaders({
      'X-OpenAstro-Preview-Width': '2048',
      'x-openastro-preview-height': '1395',
      'x-openastro-preview-cache': 'hit',
      'ETag': '"cache-key"',
      'x-openastro-stretch': 'auto_stf',
      'x-openastro-black-point': '0.012345',
      'x-openastro-mid-point': '0.371',
      'x-openastro-white-point': '0.998',
      'x-openastro-asinh-beta': '3',
      'x-openastro-clip-low': '0.005',
      'x-openastro-clip-high': '0.995',
      'x-openastro-debayer': 'super_pixel',
      'x-openastro-channel': 'rgb',
      'x-openastro-inverted': 'true',
      'x-openastro-saturation': '1.2',
      'x-openastro-annotated': 'true',
      'x-openastro-annotation-count': '187',
      'x-openastro-annotation-rejected': '12',
      'x-openastro-annotation-color': '#00ff00',
    });

    expect(applied.width, 2048);
    expect(applied.height, 1395);
    expect(applied.cacheStatus, 'hit');
    expect(applied.etag, '"cache-key"');
    expect(applied.algorithm, 'auto_stf');
    expect(applied.blackPoint, 0.012345);
    expect(applied.debayerMode, 'super_pixel');
    expect(applied.channelMode, 'rgb');
    expect(applied.inverted, isTrue);
    expect(applied.saturation, 1.2);
    expect(applied.annotated, isTrue);
    expect(applied.annotationCount, 187);
    expect(applied.rejectedAnnotationCount, 12);
  });

  test('wire parsing rejects non-finite and fractional integer values', () {
    final metadata = FrameMetadata.fromJson({
      'frame': {
        'captured_utc': '2026-08-01T02:00:00-04:00',
        'gain': 100.5,
        'width': double.infinity,
        'hfr': double.nan,
      },
      'source_exists': false,
    });
    final applied = FramePreviewApplied.fromHeaders({
      'X-OpenAstro-Black-Point': 'NaN',
      'X-OpenAstro-Saturation': 'Infinity',
    });

    expect(metadata.frame.gain, isNull);
    expect(metadata.frame.width, 0);
    expect(metadata.frame.hfr, isNull);
    expect(metadata.frame.capturedUtc, DateTime.utc(2026, 8, 1, 6));
    expect(applied.blackPoint, isNull);
    expect(applied.saturation, 1);
  });

  test(
    'metadata parses lifecycle, measurements, and quarantine additively',
    () {
      final metadata = FrameMetadata.fromJson({
        'frame': {
          'id': 'f1',
          'session_id': 's1',
          'target_name': 'M31',
          'frame_type': 'light',
          'filter_name': 'L',
          'exposure_seconds': 120.5,
          'gain': 100,
          'offset': 20,
          'temperature_c': -10.2,
          'captured_utc': '2026-08-01T02:03:04Z',
          'file_size_bytes': 50000000,
          'width': 6248,
          'height': 4176,
          'bit_depth': 16,
          'hfr': 1.43,
          'star_count': 812,
          'eccentricity': 0.41,
          'guiding_rms_arcsec': 0.55,
          'snr_estimate': 32.8,
          'rating': 5,
          'tags': ['keeper'],
          'focuser_position': 14820,
          'analysis_version': 'stars-v1',
          'quarantined_utc': '2026-08-01T03:00:00Z',
          'quarantine_reason': 'satellite trail',
          'future_field': true,
        },
        'storage': {
          'state': 'complete',
          'accepted_utc': '2026-08-01T02:01:00Z',
          'completed_utc': '2026-08-01T02:03:04Z',
          'byte_count': 50000000,
          'checksum_sha256': 'abc',
          'image_format': 'fits',
          'cfa_pattern': null,
          'updated_utc': '2026-08-01T02:03:04Z',
        },
        'source_exists': true,
        'source_checksum_sha256': 'abc',
        'image_format': 'fits',
        'analysis_state': 'ready',
        'preview_state': 'ready',
        'preview_checksum': 'def',
        'debayer_method': 'none',
        'preview_version': 'schema-2',
        'future_top_level': {'ignored': true},
      });

      expect(metadata.sourceExists, isTrue);
      expect(metadata.storage?.state, 'complete');
      expect(metadata.frame.targetName, 'M31');
      expect(metadata.frame.exposureSeconds, 120.5);
      expect(metadata.frame.hfr, 1.43);
      expect(metadata.frame.quarantineReason, 'satellite trail');
      expect(metadata.previewVersion, 'schema-2');
    },
  );

  test('metadata rejects a 2xx body missing its required frame object', () {
    expect(
      () => FrameMetadata.fromJson(const {'source_exists': true}),
      throwsA(isA<FormatException>()),
    );
  });

  test('job status reports bounded progress and terminal phases', () {
    final job = FrameJobStatus.fromJson({
      'job_id': 'j1',
      'job_type': 'frames.reanalyze:f1',
      'state': 'complete',
      'done': 2,
      'total': 1,
      'started_utc': '2026-08-01T00:00:00Z',
      'finished_utc': '2026-08-01T00:00:01Z',
    });
    expect(job.progress, 1);
    expect(job.isTerminal, isTrue);
  });
}
