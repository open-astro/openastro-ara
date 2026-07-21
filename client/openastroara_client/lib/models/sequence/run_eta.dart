import 'nina_dom.dart';

/// §Run-redesign S5 — estimated total/remaining duration for a sequence body.
///
/// The daemon publishes no ETA, but the body carries the dominant costs:
/// every `TakeExposure.ExposureTime`, multiplied up through each ancestor
/// container's loop `Iterations`/`CompletedIterations`-style counts. Non-
/// exposure instructions (slew, autofocus, dither) are charged a flat nominal
/// cost — crude, but exposure time dominates a real session and the header
/// blends this with the observed elapsed rate anyway.
class RunEta {
  /// Total estimated seconds for the whole body.
  final double totalSeconds;

  const RunEta(this.totalSeconds);
}

const double _nominalInstructionSeconds = 15;

double _loopMultiplier(Map<String, dynamic> container) {
  final conditions = conditionsOf(container);
  for (final c in conditions) {
    final iterations = c['Iterations'];
    if (iterations is num && iterations > 0) return iterations.toDouble();
  }
  return 1;
}

double _walk(Map<String, dynamic> node) {
  if (!isContainer(node)) {
    final exposure = node['ExposureTime'];
    if (exposure is num && exposure > 0) return exposure.toDouble();
    return _nominalInstructionSeconds;
  }
  var sum = 0.0;
  for (final child in childrenOf(node)) {
    sum += _walk(child);
  }
  return sum * _loopMultiplier(node);
}

RunEta estimateRunEta(Map<String, dynamic> body) => RunEta(_walk(body));

/// Remaining seconds: prefer the observed elapsed rate over the static model
/// once enough of the run has happened to trust it (≥ 10% and ≥ 2 leaves),
/// else scale the static total by the un-completed fraction. Never negative.
double estimateRemainingSeconds({
  required double staticTotalSeconds,
  required int completed,
  required int total,
  required Duration elapsed,
}) {
  if (total <= 0 || completed >= total) return 0;
  final fractionDone = completed / total;
  if (completed >= 2 && fractionDone >= 0.10 && elapsed.inSeconds > 0) {
    final perLeaf = elapsed.inSeconds / completed;
    return perLeaf * (total - completed);
  }
  final remaining = staticTotalSeconds * (1 - fractionDone);
  return remaining < 0 ? 0 : remaining;
}
