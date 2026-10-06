/// Selectable manual-slew rates for the mount's direction pad, built from the
/// mount's reported MoveAxis rate BANDS (#1126).
///
/// The daemon publishes each pad band as `{min, max}` deg/s
/// (`move_axis_rate_bands_deg_per_sec`): the primary axis's AxisRates bands,
/// each already clipped to the secondary axis's floor and ceiling so one picked
/// rate is honoured on both axes. A discrete rate is a band with `min == max`;
/// "any speed up to max" is a band with `min` 0.
///
/// Rules:
/// - Every band discrete (`min == max`): the driver's own ladder is honored
///   verbatim (deduped, ascending, plain deg/s labels) — one chip for a
///   single-rate mount, two for two steps, and so on. Nothing between the
///   steps is offered, since the daemon would snap it to a step anyway.
/// - Exactly one band and it is continuous (`min < max`): a percentage ladder
///   of `max` — 1 / 5 / 10 / 25 / 50 / 100% (logarithmic-ish steps: fine at
///   the low end for centering, coarse at the top; six options per HIG's
///   short-choice guidance). Every preset under `min` is replaced by `min`
///   itself as the slowest chip ("min · 2°/s") (#1085); a preset that lands
///   exactly on `min` keeps its percentage label. 100% is the max itself.
/// - Several bands with at least one continuous: both endpoints of every band
///   verbatim (deduped, ascending, deg/s labels) — the driver's own choices
///   need no re-interpretation.
/// - **Every option lies inside a band** — the UI never *asks* for a rate the
///   mount does not advertise, and never one above its max. Zero/negative
///   rates are dropped.
///
/// An older daemon that sends only the endpoint list
/// (`move_axis_rates_deg_per_sec`) is read the way it always was
/// ([slewRateBandsFromLegacyRates]): one rate is "up to max", two are one band,
/// three or more are discrete steps.
///
/// This picker is UX only: the daemon is the guard on hardware motion. It
/// snaps every MoveAxis rate into the axis's reported AxisRates bands
/// (#1064/#1072) — a rate over the top band is capped at its max, a rate
/// *below* the lowest band is RAISED to its min while it is within 4× of it
/// and REFUSED (409) when slower than that (#1085), and one in a gap between
/// discrete steps moves to the nearest step. Building the presets from the
/// bands keeps every chip — the default included — inside a band, so a fresh
/// connect never starts on a rate the daemon refuses.
library;

/// Slew-speed presets as fractions of the mount's max rate.
const List<double> kSlewRatePresetFractions = [0.01, 0.05, 0.10, 0.25, 0.50, 1.0];

/// One MoveAxis rate band (deg/s): the mount honours any magnitude in
/// `[min, max]`; `min == max` is a single discrete rate.
class SlewRateBand {
  final double min;
  final double max;

  const SlewRateBand(this.min, this.max);

  bool get isDiscrete => min == max;

  @override
  bool operator ==(Object other) =>
      other is SlewRateBand && other.min == min && other.max == max;

  @override
  int get hashCode => Object.hash(min, max);

  @override
  String toString() => 'SlewRateBand($min, $max)';
}

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

  @override
  String toString() => 'SlewRateOption($rateDegPerSec, $label)';
}

/// The bands an older daemon's endpoint list stands for: one rate is "up to
/// max" (`0..max`), two are one band `[min, max]` (#1085), three or more are
/// discrete steps. Zero/negative rates are dropped, duplicates deduped.
List<SlewRateBand> slewRateBandsFromLegacyRates(List<double> mountRates) {
  final rates = mountRates.where((r) => r > 0).toSet().toList()..sort();
  if (rates.isEmpty) return const [];
  if (rates.length == 1) return [SlewRateBand(0.0, rates.single)];
  if (rates.length == 2) return [SlewRateBand(rates.first, rates.last)];
  return [for (final r in rates) SlewRateBand(r, r)];
}

/// Builds the sorted, deduped rate list for the speed picker from an older
/// daemon's endpoint list (see [slewRateBandsFromLegacyRates]).
///
/// Legacy adapter with no production caller (kept for the tests that pin the
/// endpoint-list reading): the production entry point is
/// `MountCapabilities.padRateBands` fed to [buildSlewRateOptionsFromBands].
List<SlewRateOption> buildSlewRateOptions(List<double> mountRates) =>
    buildSlewRateOptionsFromBands(slewRateBandsFromLegacyRates(mountRates));

/// Builds the sorted, deduped rate list for the speed picker from the mount's
/// reported bands. Every option lies inside one of them.
List<SlewRateOption> buildSlewRateOptionsFromBands(List<SlewRateBand> mountBands) {
  final bands = _sanitise(mountBands);
  if (bands.isEmpty) return const [];

  // The driver's own ladder, or a mixed set of steps and spans: the endpoints
  // verbatim with plain deg/s labels.
  if (bands.every((b) => b.isDiscrete) || bands.length > 1) {
    final rates = <double>{
      for (final b in bands) ...[if (b.min > 0) b.min, b.max],
    }.toList()..sort();
    return [for (final r in rates) SlewRateOption(r, _fmtDeg(r))];
  }

  // One continuous band [min, max]: percentage presets of the max, labelled
  // "25% · 1.5°/s", from the band's minimum up.
  final band = bands.single;
  final maxRate = band.max;
  final minRate = band.min;
  final options = <SlewRateOption>[];
  final seen = <double>{};
  var droppedUnderMin = false;
  for (final f in kSlewRatePresetFractions) {
    final r = maxRate * f;
    if (r <= 0 || r > maxRate || !seen.add(r)) continue;
    if (r < minRate * (1 - 1e-9)) {
      // The daemon would raise (or, past 4×, refuse) this one — never offer it.
      // The relative epsilon keeps a preset that lands exactly on the minimum
      // (1 ulp under it from the multiply) as a percentage chip, not a
      // dropped preset plus a near-identical "min" chip.
      droppedUnderMin = true;
      continue;
    }
    options.add(SlewRateOption(r, _pctLabel(f, r)));
  }
  // The band's own minimum stands in for the presets that fell under it, so
  // the slowest speed the mount actually offers is always a chip.
  // A preset within rounding of the minimum already stands for it.
  final minAlreadyOffered =
      options.any((o) => (o.rateDegPerSec - minRate).abs() <= minRate * 1e-9);
  if (droppedUnderMin && !minAlreadyOffered) {
    options.add(SlewRateOption(minRate, 'min · ${_fmtDeg(minRate)}'));
  }
  options.sort((a, b) => a.rateDegPerSec.compareTo(b.rateDegPerSec));
  return options;
}

/// The chip selected on a fresh connect: the middle option — a usable nudge
/// without lurching at full speed. Every option lies inside a reported band
/// (see [buildSlewRateOptionsFromBands]), so on a discrete-rate mount this is
/// one of the mount's own steps and the daemon never refuses it. Null when the
/// mount offers no rates.
double? defaultSlewRate(List<SlewRateOption> options) =>
    options.isEmpty ? null : options[(options.length - 1) ~/ 2].rateDegPerSec;

/// Positive, ascending by min, min clamped into [0, max], duplicates dropped.
List<SlewRateBand> _sanitise(List<SlewRateBand> mountBands) {
  final seen = <SlewRateBand>{};
  final bands = <SlewRateBand>[];
  for (final b in mountBands) {
    if (!b.max.isFinite || b.max <= 0) continue;
    final min = b.min.isFinite ? b.min.clamp(0.0, b.max) : b.max;
    final clean = SlewRateBand(min, b.max);
    if (seen.add(clean)) bands.add(clean);
  }
  bands.sort((a, b) {
    final byMin = a.min.compareTo(b.min);
    return byMin != 0 ? byMin : a.max.compareTo(b.max);
  });
  return bands;
}

String _pctLabel(double fraction, double rate) =>
    '${(fraction * 100).round()}% · ${_fmtDeg(rate)}';

String _fmtDeg(double r) => r >= 1
    ? '${r.toStringAsFixed(r == r.roundToDouble() ? 0 : 1)}°/s'
    : '${r.toStringAsFixed(3)}°/s';
