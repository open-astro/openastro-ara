import '../models/library/frame.dart';

/// CSV export per §50.12. Two flavors:
///
/// - **Sessions summary** — one row per CaptureSession with date, target,
///   site, frame count, integration minutes, avg HFR, guiding RMS (RA/Dec).
/// - **Frame detail** — one row per CapturedFrame across all sessions with
///   the full metadata grid (12 columns) from the §40.5 Frame Viewer.
///
/// Phase 12g.3 returns these as `String`s; the caller copies them to the
/// clipboard. Phase 12g.4 wires a real file-save dialog (`file_picker` API
/// is platform-specific so it needs its own focused PR).

String exportSessionsCsv(List<CaptureSession> sessions) {
  final rows = <String>[
    'date,target,site,frame_count,integration_minutes,avg_hfr,'
        'guiding_rms_ra_arcsec,guiding_rms_dec_arcsec',
  ];
  for (final s in sessions) {
    final lights =
        s.frames.where((f) => f.frameType.toLowerCase() == 'light').toList();
    final avgHfr = lights.isEmpty
        ? ''
        : (lights.map((f) => f.hfr).reduce((a, b) => a + b) / lights.length)
            .toStringAsFixed(3);
    rows.add([
      _isoDate(s.date),
      _csv(s.targetName),
      _csv(s.siteName),
      '${s.frames.length}',
      '${s.totalIntegration.inMinutes}',
      avgHfr,
      s.guidingRmsRa?.toStringAsFixed(2) ?? '',
      s.guidingRmsDec?.toStringAsFixed(2) ?? '',
    ].join(','));
  }
  return rows.join('\r\n');
}

String exportFramesCsv(List<CaptureSession> sessions) {
  final rows = <String>[
    'session_id,target,captured_at,filter,frame_type,exposure_seconds,'
        'gain,offset,bin,hfr,star_count,median_adu,background_adu,'
        'sensor_temp_c,focus_steps,rating,filename',
  ];
  for (final s in sessions) {
    for (final f in s.frames) {
      rows.add([
        _csv(s.id),
        _csv(s.targetName),
        f.capturedAt.toIso8601String(),
        _csv(f.filter),
        _csv(f.frameType),
        '${f.exposure.inSeconds}',
        '${f.gain}',
        '${f.offset}',
        '${f.bin}',
        f.hfr.toStringAsFixed(3),
        '${f.starCount}',
        '${f.medianAdu}',
        '${f.backgroundAdu}',
        f.sensorTempC.toStringAsFixed(1),
        '${f.focusSteps}',
        '${f.rating}',
        _csv(f.filename),
      ].join(','));
    }
  }
  return rows.join('\r\n');
}

String _isoDate(DateTime d) =>
    '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

/// RFC-4180 quoting: wrap in quotes if the field contains comma, quote, LF,
/// or CR; escape inner quotes by doubling. Row separator is CRLF per RFC-4180
/// (strict consumers — Excel-on-Mac, ETL pipelines — require this).
String _csv(String s) {
  if (s.contains(',') ||
      s.contains('"') ||
      s.contains('\n') ||
      s.contains('\r')) {
    return '"${s.replaceAll('"', '""')}"';
  }
  return s;
}
