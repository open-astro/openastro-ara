import 'dart:math';

import '../../models/switch_device.dart';

/// How a [SwitchPort] should be presented. ASCOM's Switch interface is
/// deliberately untyped — every port is just `(value, min, max, canWrite)` —
/// so the UI has to infer intent from that shape. These rules are
/// vendor-agnostic on purpose: a Gemini power box, a ZWO dew heater and a
/// ToupTek thermal switch all land in the right bucket without a device
/// allow-list.
enum SwitchPortKind {
  /// Read-only numeric telemetry (voltage, current, temperature) — a gauge.
  reading,

  /// Read-only two-state port — a presence/status indicator (sensor attached).
  indicator,

  /// Writable two-state port — a toggle.
  toggle,

  /// Writable port over a small integer range — a segmented mode picker.
  mode,

  /// Writable port over a wide range — a slider (PWM duty, brightness).
  level,

  /// Writable, but the device reports bounds that admit no value to send
  /// (`min >= max`). Shown as a plain value row: calling it a Reading would
  /// tell the user it's read-only, and a control would be a lie.
  stuck,
}

/// The widest `max - min` span still presented as a segmented picker rather
/// than a slider. Modes are enumerations (0/1/2); anything wider reads as a
/// continuous level. Three steps is the practical ceiling before a segmented
/// control stops fitting a settings row.
const int kMaxModeSpan = 3;

/// Classifies [port] into the control that fits its shape.
SwitchPortKind classifySwitchPort(SwitchPort port) {
  if (!port.canWrite) {
    return port.isBoolean ? SwitchPortKind.indicator : SwitchPortKind.reading;
  }
  if (port.isBoolean) return SwitchPortKind.toggle;
  // A malformed device can report min == max; the ASCOM spec forbids it for a
  // non-boolean port, and a Slider would assert on it.
  if (port.min >= port.max) return SwitchPortKind.stuck;
  final span = port.max - port.min;
  final wholeSteps = span == span.roundToDouble();
  if (wholeSteps && span <= kMaxModeSpan) return SwitchPortKind.mode;
  return SwitchPortKind.level;
}

/// The unit suffix for a port, inferred from its name. Returns an empty string
/// when nothing is recognised — better a bare number than a wrong unit.
String switchPortUnit(SwitchPort port) {
  final n = port.name.toLowerCase();
  // A writable 0..100 port is a duty cycle in every powerbox we support, and
  // that wins over the name: a heater output called "Dew Power" is a percent,
  // not watts.
  if (port.canWrite && port.min == 0 && port.max == 100) return '%';
  // Whole words only — substring matching reads "Lamp" as amps.
  bool has(String word) => RegExp('\\b${word}s?\\b').hasMatch(n);
  if (has('voltage') || has('volt')) return 'V';
  if (has('current') || has('amp')) return 'A';
  if (has('power') || has('watt')) return 'W';
  if (has('humidity')) return '%';
  if (has('temperature') || n.contains('dew point')) return '°C';
  return '';
}

/// Formats [value] for display: at most two decimals, with trailing zeros
/// trimmed, so 12.6 reads "12.6" (not "12.60") and 1.0 reads "1" (not "1.00").
String formatSwitchValue(double value) {
  if (value == value.roundToDouble()) return value.toInt().toString();
  final fixed = value.toStringAsFixed(2);
  return fixed.contains('.')
      ? fixed.replaceFirst(RegExp(r'0+$'), '').replaceFirst(RegExp(r'\.$'), '')
      : fixed;
}

/// The leading word of a port name, used to group sibling ports ("USB A",
/// "USB B" → "USB"). Returns null when the name has no usable prefix.
String? switchPortGroupKey(SwitchPort port) {
  final trimmed = port.name.trim();
  if (trimmed.isEmpty) return null;
  final head = trimmed.split(RegExp(r'[\s\-_]+')).first;
  // "DC1"/"DC2" share the prefix "DC" only once the trailing index is dropped.
  final letters = RegExp(r'^[A-Za-z]+').firstMatch(head)?.group(0);
  if (letters == null || letters.length < 2) return null;
  return letters.toUpperCase();
}

/// Whitespace-separated word count of a port's name — the cheap "is this named
/// like its siblings" test that separates a rail ("DC1") from a status bit
/// about a rail ("USB1 Status").
int _nameWordCount(SwitchPort port) =>
    port.name.trim().split(RegExp(r'\s+')).where((w) => w.isNotEmpty).length;

/// A named run of ports rendered together under one header.
class SwitchPortGroup {
  final String title;
  final List<SwitchPort> ports;
  const SwitchPortGroup(this.title, this.ports);
}

/// Splits [ports] of one [kind] into named groups, so "USB A..F" and "DC1..DC5"
/// become two labelled blocks instead of one eleven-row wall. A prefix has to
/// cover at least two ports to earn a header; everything else collects under
/// [fallbackTitle].
List<SwitchPortGroup> groupSwitchPorts(
  List<SwitchPort> ports, {
  required String fallbackTitle,
}) {
  final byPrefix = <String, List<SwitchPort>>{};
  final loose = <SwitchPort>[];
  for (final p in ports) {
    final key = switchPortGroupKey(p);
    if (key == null) {
      loose.add(p);
    } else {
      byPrefix.putIfAbsent(key, () => []).add(p);
    }
  }
  final groups = <SwitchPortGroup>[];
  final grouped = <int>{};
  // Preserve device order: a prefix is placed where its first port appeared.
  final seen = <String>{};
  for (final p in ports) {
    final key = switchPortGroupKey(p);
    if (key == null || !seen.add(key)) continue;
    final members = byPrefix[key]!;
    // A run of read-only booleans sharing a prefix ("USB2 Status", "USB3
    // Status") is a set of status bits, not a rail block — rendering it as
    // toggle rows would label each one "Always on". A prefix only earns a
    // control group when something in it is actually switchable, and a
    // read-only member only joins when it is NAMED like its switchable
    // siblings: "DC1" beside "DC2" is the always-on rail and belongs in the
    // group, while "USB1 Status" beside "USB1" carries an extra descriptor and
    // is a bit about the port, not the port itself.
    final writable = members.where((m) => m.canWrite).toList();
    if (members.length >= 2 && writable.isNotEmpty) {
      final railShapes = writable.map(_nameWordCount).toSet();
      final rails = members
          .where((m) => m.canWrite || railShapes.contains(_nameWordCount(m)))
          .toList();
      if (rails.length >= 2) {
        groups.add(SwitchPortGroup(key, rails));
        grouped.addAll(rails.map((m) => m.id));
      }
    }
  }
  final leftovers = [
    ...loose,
    for (final e in byPrefix.entries)
      for (final p in e.value)
        if (!grouped.contains(p.id)) p,
  ]..sort((a, b) => a.id.compareTo(b.id));
  if (leftovers.isNotEmpty) {
    groups.add(SwitchPortGroup(fallbackTitle, leftovers));
  }
  return groups;
}

/// Pairs an output port with its companion mode port: a device that exposes
/// "DEW6" and "DEW6 Mode" should render them as one control, not two rows.
/// Returns the mode port for [port], or null when it has none.
///
/// The candidate must itself classify as a [SwitchPortKind.mode] — a name match
/// alone would fold a read-only `<name> Mode` telemetry port into an
/// interactive picker (writing to a port the device refuses), or claim a wide
/// writable range that already renders as its own slider.
SwitchPort? findModePort(SwitchPort port, List<SwitchPort> all) {
  final target = '${port.name.toLowerCase()} mode';
  for (final p in all) {
    if (p.name.toLowerCase() == target &&
        classifySwitchPort(p) == SwitchPortKind.mode) {
      return p;
    }
  }
  return null;
}

/// Labels for a 0..2 mode port. ASCOM carries no enum names, so this is a
/// convention shared by the power boxes we support (Gemini, WandererBox):
/// 0 = automatic regulation, 1 = manual duty, 2 = plain on/off.
const List<String> kDewModeLabels = ['Auto', 'Manual', 'Switch'];

/// Whether [mode] follows the 0 Auto / 1 Manual / 2 Switch dew convention, as
/// opposed to some other small enumeration (a 0..3 fan speed, say). The single
/// gate for everything that only makes sense for a dew channel: the named
/// labels, the dew help entry, and the Auto read-only rule.
bool isDewModePort(SwitchPort mode) =>
    mode.min == 0 &&
    (mode.max - mode.min).round() == 2 &&
    mode.name.toLowerCase().contains('mode');

/// Whether a dew channel is regulating itself, so its output is the
/// controller's choice rather than the user's to set.
bool isAutoRegulating(SwitchPort? mode) =>
    mode != null && isDewModePort(mode) && mode.value.round() == 0;

/// Display labels for [mode], falling back to raw numbers when the range
/// doesn't match a convention we know.
List<String> switchModeLabels(SwitchPort mode) {
  if (isDewModePort(mode)) return kDewModeLabels;
  return [
    for (var i = mode.min.round(); i <= mode.max.round(); i++) i.toString(),
  ];
}

/// Rounds [value] to a whole step when [port]'s range is whole-numbered, so a
/// continuous drag never writes a fraction to a port that wants an integer.
/// Applies regardless of how the slider was rendered — the division cap is a
/// rendering limit, not a licence to send 41234.87.
double quantiseSwitchWrite(SwitchPort port, double value) {
  final stepped = snapToSwitchStep(port, value);
  if (stepped != null) return stepped;
  final span = port.max - port.min;
  if (span != span.roundToDouble()) return value;
  // Round on the port's OWN grid: a 0.5..3.5 port has a whole span but its
  // steps land on 0.5/1.5/2.5, so snapping to absolute integers would write a
  // value the device never offers — the very thing this exists to prevent.
  return (port.min + (value - port.min).roundToDouble())
      .clamp(port.min, port.max);
}

/// Slider stops at most: a wide range (a 0..65535 device) must not build tens
/// of thousands of divisions.
const _maxSwitchStops = 1000;

/// The device's step when it reports one coarse enough to be slider stops,
/// else null (#1680).
double? usableSwitchStep(SwitchPort port) =>
    port.step > 0 && (port.max - port.min) / port.step <= _maxSwitchStops
    ? port.step
    : null;

/// [value] on the device's own step grid (from [SwitchPort.min]), or null when
/// the device reports no usable step.
double? snapToSwitchStep(SwitchPort port, double value) {
  final step = usableSwitchStep(port);
  if (step == null) return null;
  final k = ((value - port.min) / step).round();
  // To the step's decimals: 0.1 * 85 is 8.500000000000002.
  final places = max(_decimals(step), _decimals(port.min));
  return double.parse((port.min + k * step).toStringAsFixed(places))
      .clamp(port.min, port.max);
}

int _decimals(double x) {
  for (var d = 0; d < 6; d++) {
    final scaled = x * pow(10, d);
    if ((scaled - scaled.roundToDouble()).abs() < 1e-6) return d;
  }
  return 6;
}

/// Slider stops for a port: one per device step when known, else one per whole
/// unit on a whole-numbered range; null stays continuous.
int? switchSliderDivisions(SwitchPort port) {
  final span = port.max - port.min;
  final step = usableSwitchStep(port) ?? 1;
  final stops = span / step;
  final whole = stops.roundToDouble();
  if ((stops - whole).abs() > 1e-6) return null;
  return whole >= 1 && whole <= _maxSwitchStops ? whole.toInt() : null;
}
