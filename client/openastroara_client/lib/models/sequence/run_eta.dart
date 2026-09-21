/// §Run-redesign S5 / #1068 — the run header's remaining-time blend.
///
/// The daemon publishes the sequencer's own estimate (`estimated_total_seconds`
/// / `estimated_remaining_seconds` in run state, from `RunEtaEstimator`); the
/// client no longer walks the body. What stays here is presentation: once
/// enough of the run has happened to trust the observed elapsed rate, prefer
/// it over the daemon's figure.
///
/// Remaining seconds: the observed elapsed rate once ≥ 10% and ≥ 2 leaves are
/// done, else the daemon's remaining estimate when it sent one, else 0 (no
/// estimate to show). Never negative.
double estimateRemainingSeconds({
  double? serverRemainingSeconds,
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
  if (serverRemainingSeconds != null && serverRemainingSeconds > 0) {
    return serverRemainingSeconds;
  }
  return 0;
}
