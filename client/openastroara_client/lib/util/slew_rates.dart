/// Selectable manual-slew rates for the mount's direction pad — percentage
/// presets of the mount's reported max MoveAxis rate, capped at it.
///
/// Rules:
/// - The mount's own reported ladder is honored when it reports three or
///   more rates (deduped, ascending, plain deg/s labels — the driver's ladder
///   needs no re-interpretation).
/// - Two reported rates are one band `[min, max]` — the daemon publishes both
///   ends of every AxisRates band — so a percentage ladder of `max` is
///   generated and every preset under `min` is replaced by `min` itself as the
///   slowest chip ("min · 2°/s") (#1085). A preset that lands exactly on `min`
///   keeps its percentage label.
/// - With a single reported rate (typically the max — e.g. the AM5N's
///   6.016 °/s), the same percentage ladder is generated:
///   1 / 5 / 10 / 25 / 50 / 100% (logarithmic-ish steps — fine at the low end
///   for centering, coarse at the top; six options per HIG's short-choice
///   guidance). 100% is the max itself, so no separate MAX entry is needed.
/// - **Every option is <= the mount's max** — the UI never *asks* for a rate
///   above what the mount advertises. Zero/negative rates are dropped.
///
/// This picker is UX only: the daemon is the guard on hardware motion. It
/// snaps every MoveAxis rate into the axis's reported AxisRates bands
/// (#1064/#1072) — a rate over the top band is capped at its max, a rate
/// *below* the lowest band is RAISED to its min while it is within 4× of it
/// and REFUSED (409) when slower than that (#1085), and one in a gap between
/// discrete steps moves to the nearest step. So a percentage preset is a
/// request, not a promise: it may be driven up to 4× faster than its label.
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

  // A mount reporting its own ladder (3+ rates) keeps it verbatim with plain
  // deg/s labels — the driver's own choices need no re-interpretation.
  if (rates.length >= 3) {
    return [for (final r in rates) SlewRateOption(r, _fmtDeg(r))];
  }

  // Two rates are one band [min, max] (#1085); one is the max alone. Either
  // way: percentage presets of the max, labelled "25% · 1.5°/s".
  final maxRate = rates.last;
  final minRate = rates.length == 2 ? rates.first : 0.0;
  final options = <SlewRateOption>[];
  final seen = <double>{};
  var droppedUnderMin = false;
  for (final f in kSlewRatePresetFractions) {
    final r = maxRate * f;
    if (r <= 0 || r > maxRate || !seen.add(r)) continue;
    if (r < minRate) {
      // The daemon would raise (or, past 4×, refuse) this one — never offer it.
      droppedUnderMin = true;
      continue;
    }
    options.add(SlewRateOption(r, _pctLabel(f, r)));
  }
  // The band's own minimum stands in for the presets that fell under it, so
  // the slowest speed the mount actually offers is always a chip.
  if (droppedUnderMin && seen.add(minRate)) {
    options.add(SlewRateOption(minRate, 'min · ${_fmtDeg(minRate)}'));
  }
  options.sort((a, b) => a.rateDegPerSec.compareTo(b.rateDegPerSec));
  return options;
}

String _pctLabel(double fraction, double rate) =>
    '${(fraction * 100).round()}% · ${_fmtDeg(rate)}';

String _fmtDeg(double r) => r >= 1
    ? '${r.toStringAsFixed(r == r.roundToDouble() ? 0 : 1)}°/s'
    : '${r.toStringAsFixed(3)}°/s';
