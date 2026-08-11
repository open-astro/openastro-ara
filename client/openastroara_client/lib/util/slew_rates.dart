/// Selectable manual-slew rates for the mount's direction pad — ZWO ASIAir
/// style. A mount usually reports a single max MoveAxis rate (e.g. the AM5N's
/// 6.016 °/s); the ASIAir app offers fractions of that max so you can nudge
/// finely instead of always lurching at full speed.
///
/// Rules:
/// - The mount's own reported rates are honored when it reports more than one
///   (deduped, ascending, capped at the max).
/// - With a single rate (or none), ZWO-style presets are generated as
///   percentages of the max: 1 / 5 / 10 / 25 / 50 / 100.
/// - **Every option is <= the mount's max** — the UI can never ask a mount to
///   slew faster than it advertises. Zero/negative rates are dropped.
library;

/// ZWO-ASIAir-style preset fractions of the mount's max manual-slew rate.
const List<double> kSlewRatePresetFractions = [0.01, 0.05, 0.10, 0.25, 0.50, 1.0];

/// One selectable direction-pad speed: the deg/sec value sent to the mount and
/// the chip label ("50% · 3.0°/s" for presets, "1.5°/s" for mount-reported
/// rates whose percentage isn't a clean fraction of the max).
class SlewRateOption {
  final double rateDegPerSec;
  final String label;

  const SlewRateOption(this.rateDegPerSec, this.label);

  @override
  bool operator ==(Object other) =>
      other is SlewRateOption && other.rateDegPerSec == rateDegPerSec;

  @override
  int get hashCode => rateDegPerSec.hashCode;
}

/// Builds the sorted, deduped, max-capped rate list for the speed picker.
List<SlewRateOption> buildSlewRateOptions(List<double> mountRates) {
  final rates = mountRates.where((r) => r > 0).toSet().toList()..sort();
  if (rates.isEmpty) return const [];

  // A mount reporting its own ladder (2+ rates) keeps it verbatim with plain
  // deg/s labels — the driver's own choices need no re-interpretation.
  if (rates.length >= 2) {
    return [for (final r in rates) SlewRateOption(r, _fmtDeg(r))];
  }

  // Single reported rate (typically the max — e.g. the AM5N's 6.016 °/s):
  // ZWO-ASIAir-style percentage presets of it, labelled "50% · 3.0°/s".
  final maxRate = rates.last;
  final options = <SlewRateOption>[];
  final seen = <double>{};
  for (final f in kSlewRatePresetFractions) {
    final r = maxRate * f;
    if (r <= 0 || r > maxRate || !seen.add(r)) continue;
    options.add(SlewRateOption(r, _pctLabel(f, r)));
  }
  options.sort((a, b) => a.rateDegPerSec.compareTo(b.rateDegPerSec));
  return options;
}

String _pctLabel(double fraction, double rate) =>
    '${(fraction * 100).round()}% · ${_fmtDeg(rate)}';

String _fmtDeg(double r) => r >= 1
    ? '${r.toStringAsFixed(r == r.roundToDouble() ? 0 : 1)}°/s'
    : '${r.toStringAsFixed(3)}°/s';
