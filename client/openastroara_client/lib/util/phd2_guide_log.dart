import '../state/guider/guide_step_state.dart';

/// One "Guiding Begins … Guiding Ends" session of a PHD2 guide log, in the
/// client's own [GuideStep] / [GuideMarker] terms.
class Phd2LogSession {
  final DateTime beganAt;
  final double? pixelScaleArcsec;
  final List<GuideStep> steps;
  final List<GuideMarker> markers;
  const Phd2LogSession({
    required this.beganAt,
    required this.pixelScaleArcsec,
    required this.steps,
    required this.markers,
  });
}

/// Parser for PHD2's `PHD2_GuideLog_*.txt` (the guider is PHD2, so the log
/// format is PHD2's). Only guiding sessions are read — calibration blocks are
/// skipped. Guide-step rows are the CSV block under the `Frame,Time,mount,…`
/// header; the markers come from the `INFO:` lines PHD2 writes between rows
/// (DITHER, SETTLING STATE CHANGE, star lost).
///
/// Sign conventions match what the daemon relays live: RA raw is negated
/// (the NINA-derived `IGuideStep.RADistanceRaw`), pulses are negative for
/// East / South.
class Phd2GuideLog {
  Phd2GuideLog._(this.sessions);

  final List<Phd2LogSession> sessions;

  /// Every step of every session, in order — what a replay plays.
  List<GuideStep> get allSteps => [for (final s in sessions) ...s.steps];
  List<GuideMarker> get allMarkers => [for (final s in sessions) ...s.markers];

  static final _begins = RegExp(r'^Guiding Begins at (\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2}):(\d{2})');
  static final _scale = RegExp(r'^Pixel scale = ([\d.]+) arc-sec/px');
  static final _dither = RegExp(r'DITHER by ([-\d.]+), ([-\d.]+)');

  static Phd2GuideLog parse(String text) {
    final sessions = <Phd2LogSession>[];
    DateTime? began;
    double? scale;
    var steps = <GuideStep>[];
    var markers = <GuideMarker>[];
    var lastT = 0.0;
    var inRows = false;

    void close() {
      final b0 = began; // local copy: a captured variable does not promote
      if (b0 != null && steps.isNotEmpty) {
        markers.add(GuideMarker(
            at: _at(b0, lastT + 0.5), kind: GuideMarkerKind.guidingStopped));
        sessions.add(Phd2LogSession(
            beganAt: b0, pixelScaleArcsec: scale, steps: steps, markers: markers));
      }
      began = null;
      steps = <GuideStep>[];
      markers = <GuideMarker>[];
      lastT = 0;
      inRows = false;
    }

    for (final raw in text.split('\n')) {
      final line = raw.trimRight();
      final b = _begins.firstMatch(line);
      if (b != null) {
        close();
        began = DateTime(int.parse(b[1]!), int.parse(b[2]!), int.parse(b[3]!),
            int.parse(b[4]!), int.parse(b[5]!), int.parse(b[6]!));
        markers.add(GuideMarker(at: began!, kind: GuideMarkerKind.guidingStarted));
        continue;
      }
      if (began == null) continue; // calibration or preamble
      if (line.startsWith('Guiding Ends')) {
        close();
        continue;
      }
      final sc = _scale.firstMatch(line);
      if (sc != null) {
        scale = double.tryParse(sc[1]!);
        continue;
      }
      if (line.startsWith('Frame,Time,')) {
        inRows = true;
        continue;
      }
      if (line.startsWith('INFO:')) {
        final at = _at(began!, lastT + 0.5);
        final d = _dither.firstMatch(line);
        if (d != null) {
          markers.add(GuideMarker(
              at: at,
              kind: GuideMarkerKind.dithered,
              dxPx: double.tryParse(d[1]!),
              dyPx: double.tryParse(d[2]!)));
        } else if (line.contains('Settling started')) {
          markers.add(GuideMarker(at: _at(began!, lastT + 0.6), kind: GuideMarkerKind.settling));
        } else if (line.contains('Settling complete')) {
          markers.add(GuideMarker(at: at, kind: GuideMarkerKind.settleDone, status: 0));
        } else if (line.contains('Settling failed')) {
          markers.add(GuideMarker(
              at: at, kind: GuideMarkerKind.settleDone, status: 1, error: 'settle failed'));
        } else if (line.toLowerCase().contains('star lost') &&
            (markers.isEmpty || markers.last.kind != GuideMarkerKind.starLost)) {
          markers.add(GuideMarker(at: at, kind: GuideMarkerKind.starLost));
        }
        continue;
      }
      if (!inRows) continue;
      final step = _row(line, began!, scale);
      if (step == null) continue;
      lastT = step.$2;
      // The first lost-star row of a run marks it, as a live star_lost
      // event would; most logs carry no "star lost" INFO line for it.
      if (step.$1.raPx == null &&
          (steps.isEmpty || steps.last.raPx != null) &&
          (markers.isEmpty || markers.last.kind != GuideMarkerKind.starLost)) {
        markers.add(GuideMarker(at: step.$1.at, kind: GuideMarkerKind.starLost));
      }
      steps.add(step.$1);
    }
    close();
    return Phd2GuideLog._(sessions);
  }

  static DateTime _at(DateTime began, double t) =>
      began.add(Duration(milliseconds: (t * 1000).round()));

  /// A guide-step row:
  /// `Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,
  /// DECGuideDistance,RADuration,RADirection,DECDuration,DECDirection,XStep,
  /// YStep,StarMass,SNR,ErrorCode`. A row whose error code is non-zero or
  /// whose distances are blank is a lost-star frame → a step with no offset.
  static (GuideStep, double)? _row(String line, DateTime began, double? scale) {
    final f = line.split(',');
    if (f.length < 18 || int.tryParse(f[0]) == null) return null;
    final t = double.tryParse(f[1]);
    if (t == null) return null;
    final at = _at(began, t);
    final raRaw = double.tryParse(f[5]);
    final decRaw = double.tryParse(f[6]);
    final err = int.tryParse(f[17].trim()) ?? 0;
    if (raRaw == null || decRaw == null || err != 0) {
      return (GuideStep(at: at), t);
    }
    final ra = -raRaw;
    final dec = decRaw;
    final raMs = (double.tryParse(f[9]) ?? 0) * (f[10].trim() == 'E' ? -1 : 1);
    final decMs = (double.tryParse(f[11]) ?? 0) * (f[12].trim() == 'S' ? -1 : 1);
    return (
      GuideStep(
        at: at,
        raPx: ra,
        decPx: dec,
        raArcsec: scale == null ? null : ra * scale,
        decArcsec: scale == null ? null : dec * scale,
        raPulseMs: raMs,
        decPulseMs: decMs,
        pixelScaleArcsec: scale,
        snr: double.tryParse(f[16]),
      ),
      t,
    );
  }
}
