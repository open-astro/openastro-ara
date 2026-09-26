/// §Run-redesign S5 / #1068 / #1080 — the run header's remaining-time blend.
///
/// The daemon publishes the sequencer's own estimate (`estimated_total_seconds`
/// / `estimated_remaining_seconds` in run state, from `RunEtaEstimator`); it
/// knows loop passes, the instruction that is running and what is left, so it
/// is preferred whenever it is present. The observed elapsed rate
/// (`elapsed / completed × remaining leaves`) is only a fallback for a daemon
/// that sent no estimate: leaves are not loop passes (a 30× exposure loop is
/// one leaf), and elapsed time includes pauses, so it misleads on exactly the
/// sequences the daemon figure was built for.
///
/// Remaining seconds: the daemon's remaining estimate when it sent one, else
/// the observed rate once ≥ 10% and ≥ 2 leaves are done, else 0 (nothing to
/// show). Never negative.
double estimateRemainingSeconds({
  double? serverRemainingSeconds,
  required int completed,
  required int total,
  required Duration elapsed,
}) {
  if (total <= 0 || completed >= total) return 0;
  if (serverRemainingSeconds != null && serverRemainingSeconds >= 0) {
    return serverRemainingSeconds;
  }
  final fractionDone = completed / total;
  if (completed >= 2 && fractionDone >= 0.10 && elapsed.inSeconds > 0) {
    final perLeaf = elapsed.inSeconds / completed;
    return perLeaf * (total - completed);
  }
  return 0;
}
