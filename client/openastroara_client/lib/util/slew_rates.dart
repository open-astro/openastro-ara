/// Selectable manual-slew rates for the mount's direction pad — ASIAir style:
/// a ladder of **x-multipliers of sidereal rate** (1x = tracking speed), capped
/// at the mount's reported max MoveAxis rate, with `MAX` always exactly the max.
///
/// Rules:
/// - The mount's own reported rates are honored when it reports more than one
///   (deduped, ascending, plain deg/s labels — the driver's ladder needs no
///   re-interpretation).
/// - With a single reported rate (typically the max — e.g. the AM5N's
///   6.016 °/s), the ASIAir ladder is generated: 1x, 2x, 8x, 16x, 32x, 64x,
///   128x, 256x, 512x (of sidereal), then `MAX`.
/// - **Every option is <= the mount's max** — the UI can never ask a mount to
///   slew faster than it advertises. Zero/negative rates are dropped.
library;

/// Sidereal rate in °/s — the base for slew-speed multipliers (1x = tracking
/// speed). 15.041067 °/hour, the standard value ASCOM/drivers use.
const double kSiderealRateDegPerSec = 15.041067 / 3600;

/// ASIAir-style slew-speed multipliers of [kSiderealRateDegPerSec].
const List<int> kSlewRateMultipliers = [1, 2, 8, 16, 32, 64, 128, 256, 512];

/// One selectable direction-pad speed: the deg/sec value sent to the mount,
/// the wheel label ("16x" for presets, "4°/s" for mount-reported rates), and
/// an optional secondary line (the deg/s value under an x label).
class SlewRateOption {
  final double rateDegPerSec;
  final String label;
  final String? detail;

  const SlewRateOption(this.rateDegPerSec, this.label, [this.detail]);

  @override
  bool operator ==(Object other) =>
      other is SlewRateOption && other.rateDegPerSec == rateDegPerSec;

  @override
  int get hashCode => rateDegPerSec.hashCode;
}

/// Builds the sorted, deduped, max-capped rate list for the speed wheel.
List<SlewRateOption> buildSlewRateOptions(List<double> mountRates) {
  final rates = mountRates.where((r) => r > 0).toSet().toList()..sort();
  if (rates.isEmpty) return const [];

  // A mount reporting its own ladder (2+ rates) keeps it verbatim with plain
  // deg/s labels — the driver's own choices need no re-interpretation.
  if (rates.length >= 2) {
    return [for (final r in rates) SlewRateOption(r, _fmtDeg(r))];
  }

  // Single reported rate (typically the max — e.g. the AM5N's 6.016 °/s):
  // ASIAir-style x-of-sidereal ladder, then MAX = exactly the max.
  final maxRate = rates.last;
  final options = <SlewRateOption>[];
  final seen = <double>{};
  for (final m in kSlewRateMultipliers) {
    final r = m * kSiderealRateDegPerSec;
    if (r > maxRate) break; // ladder is ascending — everything after is too big
    if (!seen.add(r)) continue;
    options.add(SlewRateOption(r, '${m}x', _fmtDeg(r)));
  }
  if (seen.add(maxRate)) {
    options.add(SlewRateOption(maxRate, 'MAX', _fmtDeg(maxRate)));
  }
  return options;
}

String _fmtDeg(double r) => r >= 1
    ? '${r.toStringAsFixed(r == r.roundToDouble() ? 0 : 1)}°/s'
    : '${r.toStringAsFixed(3)}°/s';
