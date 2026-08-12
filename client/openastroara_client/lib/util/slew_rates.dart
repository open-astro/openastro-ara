/// Selectable manual-slew rates for the mount's direction pad — percentage
/// presets of the mount's reported max MoveAxis rate, capped at it.
///
/// Rules:
/// - The mount's own reported rates are honored when it reports more than one
///   (deduped, ascending, plain deg/s labels — the driver's ladder needs no
///   re-interpretation).
/// - With a single reported rate (typically the max — e.g. the AM5N's
///   6.016 °/s), a percentage ladder is generated: 1 / 5 / 10 / 25 / 50 / 100%
///   (logarithmic-ish steps — fine at the low end for centering, coarse at the
///   top; six options per HIG's short-choice guidance). 100% is the max
///   itself, so no separate MAX entry is needed.
/// - **Every option is <= the mount's max** — the UI can never ask a mount to
///   slew faster than it advertises. Zero/negative rates are dropped.
library;

/// Slew-speed presets as fractions of the mount's max rate.
const List<double> kSlewRatePresetFractions = [0.01, 0.05, 0.10, 0.25, 0.50, 1.0];

/// One selectable direction-pad speed: the deg/sec value sent to the mount and
/// the chip label ("25% · 1.5°/s" for presets, "4°/s" for mount-reported
/// rates).
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
  // percentage presets of it, labelled "25% · 1.5°/s".
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
