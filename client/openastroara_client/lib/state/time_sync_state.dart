import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/server.dart';
import '../services/time_sync_api.dart';
import '../l10n/l10n.dart';
import 'app_locale_state.dart';
import 'saved_server_state.dart';
import 'simple_messages_state.dart';

/// Builds a [TimeSyncClient] for a server. Overridable in tests.
final timeSyncApiFactoryProvider = Provider<TimeSyncClient Function(AraServer)>(
  (ref) => TimeSyncApi.new,
);

/// [TimeSyncClient] bound to the active server, or null when none is saved.
final timeSyncApiProvider = Provider<TimeSyncClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(timeSyncApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

/// The daemon's current §31 sync state for the settings status UI, or null
/// when no server is active. Refresh with `ref.invalidate`.
final timeSyncStatusProvider = FutureProvider.autoDispose<TimeSyncState?>((
  ref,
) async {
  final api = ref.watch(timeSyncApiProvider);
  if (api == null) return null;
  return api.getState();
});

/// The rig clock is off by this much and the daemon could not set it. The
/// daemon tracks an offset of 0 after a successful set, so an offset of a
/// minute or more means "not set" (#1945).
class TimeSyncClockError {
  const TimeSyncClockError({required this.seconds, required this.slow});

  /// Absolute size of the error.
  final double seconds;

  /// True when the daemon's clock is behind (positive tracked offset).
  final bool slow;

  /// "2 min 5 s", "1 h 3 min": for a notice.
  String get label {
    final t = seconds.round();
    final h = t ~/ 3600, m = t % 3600 ~/ 60, s = t % 60;
    return [if (h > 0) '$h h', if (m > 0) '$m min', if (h == 0 && s > 0) '$s s']
        .join(' ');
  }
}

/// "The rig's clock is 2 min slow."
String clockWrongText(AppLocalizations l, TimeSyncClockError e) =>
    e.slow ? l.clockWrongSlow(e.label) : l.clockWrongFast(e.label);

/// Smallest tracked offset read as a clock that was not set.
const kClockErrorThresholdSeconds = 60.0;

/// The rig's clock error read from the daemon's time-sync GET (so every
/// connected device sees it, not only the one that pushed), or null when the
/// clock is fine, no server is active or the read failed. Not cached: each
/// read asks the rig.
final timeSyncClockErrorProvider =
    FutureProvider.autoDispose<TimeSyncClockError?>((ref) async {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  // Its own short-lived client: this is read once per Start.
  final api = ref.read(timeSyncApiFactoryProvider)(server);
  try {
    final off = (await api.getState()).systemTimeOffsetSeconds;
    if (off.abs() < kClockErrorThresholdSeconds) return null;
    return TimeSyncClockError(seconds: off.abs(), slow: off > 0);
  } catch (_) {
    return null;
  } finally {
    try {
      api.close();
    } catch (_) {}
  }
});

/// The §31.1 on-connect push: ask the daemon whether it holds a fresh,
/// trustworthy sync and, when it doesn't, push this device's clock (waterfall
/// step 1 — an NTP-synced laptop/phone clock at medium trust covers ~95% of
/// sessions per the playbook). Best-effort by design: a failure is logged and
/// swallowed — a Pi with no time sync degrades exactly as before this existed,
/// and the next connect retries.
final timeSyncOnConnectProvider = Provider<TimeSyncOnConnect>(
  (ref) => TimeSyncOnConnect(ref),
);

/// What the on-connect check read from the rig BEFORE its own push. A new
/// instance per check (no `==`), so a reconnect with the same answer still
/// notifies. The Companion time offer reads this, never a second `getState()`
/// that would race the push and see the clock it just set.
class TimeSyncCheck {
  const TimeSyncCheck({required this.synced});
  final bool synced;
}

final timeSyncCheckProvider =
    NotifierProvider<_TimeSyncCheckNotifier, TimeSyncCheck?>(
      _TimeSyncCheckNotifier.new,
    );

class _TimeSyncCheckNotifier extends Notifier<TimeSyncCheck?> {
  @override
  TimeSyncCheck? build() => null;
  void record(bool synced) => state = TimeSyncCheck(synced: synced);
}

class TimeSyncOnConnect {
  final Ref _ref;

  /// Injectable clock for tests.
  DateTime Function() now = () => DateTime.now().toUtc();

  TimeSyncOnConnect(this._ref);

  Future<void> syncIfNeeded() async {
    final api = _ref.read(timeSyncApiProvider);
    if (api == null) return;
    try {
      final state = await api.getState();
      _ref.read(timeSyncCheckProvider.notifier).record(state.synced);
      if (state.synced) {
        return; // fresh + trustworthy — nothing to do (§31.1 top branch)
      }
      await api.pushClientTime(now());
      debugPrint(
        '[time-sync] pushed device clock (server was ${state.source}/${state.trust})',
      );
      // The push can succeed and the clock stay wrong (#1945): say so.
      final err = await _ref.read(timeSyncClockErrorProvider.future);
      if (err != null) {
        _ref.read(simpleMessagesProvider.notifier).show(
              SimpleMessageKind.warning,
              clockWrongText(l10nFor(_ref.read(appLocaleProvider)), err),
              sticky: true,
            );
      }
    } catch (e) {
      // Best-effort: never surface an error for an opportunistic sync.
      debugPrint('[time-sync] on-connect push failed: $e');
    }
  }
}
