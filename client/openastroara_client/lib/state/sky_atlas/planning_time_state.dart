import 'package:flutter_riverpod/flutter_riverpod.dart';

/// §36 — the planetarium's planning time. `null` means "live now" (the view
/// tracks the real clock); a non-null value pins the sky to a chosen instant so
/// you can see what's up later tonight rather than the current (often daytime)
/// sky. Stored in UTC. `StellariumView` drives the engine's observer clock from
/// this; the time bar sets it.
class PlanningTimeNotifier extends Notifier<DateTime?> {
  @override
  DateTime? build() => null;

  /// Back to the live clock.
  void setNow() => state = null;

  /// Pin to a specific instant.
  void setTime(DateTime t) => state = t.toUtc();

  /// Nudge the pinned time (pins relative to "now" if currently live).
  void shift(Duration d) {
    final base = state ?? DateTime.now().toUtc();
    state = base.add(d);
  }

  /// Jump to ~22:00 local tonight — a sensible "deep enough into the night to
  /// plan" default. Uses the device's local time (the observing desktop is at or
  /// near the site); a per-site override can refine this later.
  void tonight() {
    final now = DateTime.now();
    state = DateTime(now.year, now.month, now.day, 22).toUtc();
  }
}

final planningTimeProvider =
    NotifierProvider<PlanningTimeNotifier, DateTime?>(PlanningTimeNotifier.new);
