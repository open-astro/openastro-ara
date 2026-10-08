import 'condition_catalog.dart';
import 'instruction_catalog.dart';
import 'nina_dom.dart';
import 'node_display.dart';
import 'trigger_catalog.dart';

/// What a container actually DOES, in words a user reads at a glance: how many
/// times its children repeat, how many frames that is and roughly how long,
/// what else ends it, and what its triggers do along the way.
///
/// The editor used to show a bare "⟳ 1 ⚡ 2" on a container row — the NUMBER of
/// conditions and triggers — so a loop of 56 frames read as "loops once", and
/// the Take Exposure inspector never said how many frames it would take. Pure,
/// so the wording is unit-tested without a widget.
class LoopSummary {
  /// The Loop condition's iteration count, or null when there is no Loop.
  final int? iterations;

  /// Take Exposure steps directly inside the container (frames per pass).
  final int exposuresPerPass;

  /// Their exposure time when they all share one, else null.
  final double? exposureSeconds;

  /// Short phrases for the other conditions ("while above horizon").
  final List<String> conditionNotes;

  /// Short phrases for the triggers ("AF every 23", "dither every 1").
  final List<String> triggerNotes;

  const LoopSummary({
    required this.iterations,
    required this.exposuresPerPass,
    required this.exposureSeconds,
    required this.conditionNotes,
    required this.triggerNotes,
  });

  /// Frames this container takes when the Loop runs to completion.
  int? get totalFrames => iterations == null || exposuresPerPass == 0
      ? null
      : iterations! * exposuresPerPass;

  /// Rough wall time of those frames (exposure time only: no download,
  /// dither settle or autofocus).
  Duration? get estimatedDuration {
    final frames = totalFrames;
    final exp = exposureSeconds;
    if (frames == null || exp == null || exp <= 0) return null;
    return Duration(milliseconds: (frames * exp * 1000).round());
  }

  /// The row chip for the loop: "× 56 · 310 s · ≈ 4.8 h". Null when the
  /// container has no Loop or loops just once (nothing to say).
  String? get loopChip {
    final n = iterations;
    if (n == null || n <= 1) return null;
    final parts = <String>['× $n'];
    if (exposureSeconds != null && exposuresPerPass > 0) {
      parts.add(formatSeconds(exposureSeconds!));
    }
    final d = estimatedDuration;
    if (d != null) parts.add('≈ ${formatDuration(d)}');
    return parts.join(' · ');
  }

  /// The row chip for the triggers: "AF every 23 · dither every 1".
  String? get triggerChip =>
      triggerNotes.isEmpty ? null : triggerNotes.join(' · ');

  /// The row chip for the other conditions: "while above horizon".
  String? get conditionChip =>
      conditionNotes.isEmpty ? null : conditionNotes.join(' · ');
}

/// Summarise the container [node]. Non-containers summarise as empty.
LoopSummary summarizeContainer(Map<String, dynamic> node) {
  int? iterations;
  final conditionNotes = <String>[];
  for (final c in conditionsOf(node)) {
    final type = c[r'$type'];
    if (type == loopConditionType) {
      iterations = _int(c['Iterations']);
      continue;
    }
    conditionNotes.add(_conditionNote(c));
  }

  var exposures = 0;
  double? exposure;
  var mixed = false;
  for (final child in childrenOf(node)) {
    if (child[r'$type'] != takeExposureType) continue;
    exposures++;
    final t = _num(child['ExposureTime']);
    if (t == null) continue;
    if (exposure == null) {
      exposure = t;
    } else if (exposure != t) {
      mixed = true;
    }
  }

  return LoopSummary(
    iterations: iterations,
    exposuresPerPass: exposures,
    exposureSeconds: mixed ? null : exposure,
    conditionNotes: conditionNotes,
    triggerNotes: [for (final t in triggersOf(node)) _triggerNote(t)],
  );
}

/// One line for a Take Exposure's inspector, from its parent container:
/// "Repeats 56× in Ha Imaging (≈ 4.8 h) · AF every 23 · dither every 1".
/// Null when the parent has no Loop (the exposure runs once per pass).
String? exposureRepeatLine(Map<String, dynamic> parent) {
  final s = summarizeContainer(parent);
  final n = s.iterations;
  if (n == null) return null;
  final name =
      parent['Name'] is String && (parent['Name'] as String).trim().isNotEmpty
      ? (parent['Name'] as String).trim()
      : nodeLabel(parent);
  final head = n == 1
      ? 'Runs once in $name'
      : 'Repeats $n× in $name'
            '${s.estimatedDuration == null ? '' : ' (≈ ${formatDuration(s.estimatedDuration!)})'}';
  return [head, ...s.conditionNotes, ...s.triggerNotes].join(' · ');
}

String _conditionNote(Map<String, dynamic> c) {
  final type = c[r'$type'];
  switch (shortTypeName(type)) {
    case 'AboveHorizonCondition':
      return 'while above horizon';
    case 'AltitudeCondition':
      return 'until altitude';
    case 'TimeSpanCondition':
      final h = _int(c['Hours']) ?? 0;
      final m = _int(c['Minutes']) ?? 0;
      return 'for ${h > 0 ? '${h}h ' : ''}${m}m';
    case 'TimeCondition':
      final h = _int(c['Hours']) ?? _int(c['Hour']);
      final m = _int(c['Minutes']) ?? 0;
      return h == null
          ? 'until a time'
          : 'until ${h.toString().padLeft(2, '0')}:${m.toString().padLeft(2, '0')}';
  }
  final def = type is String ? conditionForType(type) : null;
  return (def?.label ?? shortTypeName(type) ?? 'condition').toLowerCase();
}

String _triggerNote(Map<String, dynamic> t) {
  final type = t[r'$type'];
  if (type == autofocusAfterExposuresType) {
    return 'AF every ${_int(t['AfterExposures']) ?? 1}';
  }
  if (type == ditherAfterExposuresType) {
    return 'dither every ${_int(t['AfterExposures']) ?? 1}';
  }
  switch (shortTypeName(type)) {
    case 'AutofocusAfterTimeTrigger':
      return 'AF every ${_fmtAmount(t['Amount'])} min';
    case 'AutofocusAfterTemperatureChangeTrigger':
      return 'AF on ${_fmtAmount(t['Amount'])} °C change';
    case 'AutofocusAfterHFRIncreaseTrigger':
      return 'AF on +${_fmtAmount(t['Amount'])}% HFR';
    case 'AutofocusAfterFilterChange':
      return 'AF after filter change';
    case 'MeridianFlipTrigger':
      return 'meridian flip';
  }
  final def = type is String ? triggerForType(type) : null;
  return def?.label ?? shortTypeName(type) ?? 'trigger';
}

/// "310 s", "5 min", "5.2 min".
String formatSeconds(double s) {
  if (s < 120) return '${_trim(s)} s';
  final m = s / 60;
  return '${m == m.roundToDouble() ? m.round() : m.toStringAsFixed(1)} min';
}

/// "≈ 25 min" / "4.8 h" style, without the ≈.
String formatDuration(Duration d) {
  final minutes = d.inSeconds / 60;
  if (minutes < 90) return '${minutes.round()} min';
  final h = minutes / 60;
  return '${h.toStringAsFixed(h >= 10 ? 0 : 1)} h';
}

String _fmtAmount(Object? v) {
  final n = _num(v);
  return n == null ? '?' : _trim(n);
}

String _trim(double v) =>
    v == v.roundToDouble() ? v.round().toString() : v.toStringAsFixed(1);

int? _int(Object? v) =>
    v is num ? v.toInt() : (v is String ? int.tryParse(v) : null);
double? _num(Object? v) =>
    v is num ? v.toDouble() : (v is String ? double.tryParse(v) : null);
