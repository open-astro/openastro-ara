import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/polar_align.dart';
import '../equipment/mount_state.dart';
import '../settings/polar_align_settings_state.dart';
import '../settings/site_settings_state.dart';
import 'polar_align_state.dart';

/// #2068 (ARA-18) the Start card's pre-flight list, as the rig evaluates it:
/// every check a start refuses on. Read when a Start card opens
/// (autoDispose), and again ([refresh]) whenever something a check reads
/// changes: the routine's phase (a run started, ended or failed), the mount
/// (connected, parked, its position trusted, a slew ending, another mount),
/// the polar-align section (a clearance saved here or on another device, a
/// profile switch) and the site; not while a run moves the mount (its phase
/// change after the turns reads it). A failing mount-clock check is read again a
/// few seconds later ([clockRetryDelay]): the rig's clock push after a
/// connect clears it with no event the app sees. Null with no rig, or from a
/// rig that predates the list (the card then shows none, and the rig's own
/// start refusals stand).
///
/// The list is the one for a Turn RA by hand start (Joey, 2026-11-21): a
/// clearance that is not confirmed is a warn there, so Start stays on and its
/// confirm step offers Turn RA by hand alone (the motor waits for the
/// clearance).
class PolarAlignPreflightNotifier extends AsyncNotifier<PolarAlignPreflight?> {
  /// How long after a failing mount-clock check the list is read again: the
  /// rig's push takes up to about 10 s.
  static const clockRetryDelay = Duration(seconds: 12);

  /// At most this many clock re-reads in a row, so a mount whose clock never
  /// verifies is not polled while the card stays open.
  static const maxClockRetries = 3;

  static const _clockCheck = 'mount_clock_or_site';

  /// The routine's phases in which it may move the mount.
  static const _movingPhases = {
    PolarAlignStates.homing,
    PolarAlignStates.seeding,
    PolarAlignStates.checking,
  };

  // The read that may set the state: an older reply never overwrites a newer
  // one.
  int _seq = 0;
  int _clockRetries = 0;
  Timer? _clockRetry;

  @override
  Future<PolarAlignPreflight?> build() async {
    final api = ref.watch(polarAlignApiProvider);
    ref.onDispose(() => _clockRetry?.cancel());
    if (api == null) return null;
    // While a run moves the mount (Home, the seed's turns, a Check's turns) the
    // mount's state flips with every turn, and each read makes the rig read
    // the mount afresh; the list is read again when the phase moves on (into
    // the adjust loop, or the run's end).
    bool runMovesMount() =>
        _movingPhases.contains(ref.read(polarAlignLiveProvider).phase);
    void changed(Object? prev, Object? next) {
      if (prev != next && !runMovesMount()) unawaited(refresh());
    }

    ref.listen<Object?>(polarAlignLiveProvider.select((l) => l.phase), changed);
    ref.listen<Object?>(
      mountProvider.select((m) {
        final v = m.value;
        return v == null
            ? null
            : (
                v.deviceId,
                v.connectionState,
                v.parked,
                v.positionUnverified,
                v.isBusy,
              );
      }),
      changed,
    );
    ref.listen<Object?>(
      polarAlignSettingsProvider.select((s) => s.value),
      changed,
    );
    ref.listen<Object?>(
      siteSettingsProvider.select(
        (s) => (s.value?.latitudeDeg, s.value?.longitudeDeg),
      ),
      changed,
    );
    final seq = ++_seq;
    final list = await api.preflight(handTurn: true);
    if (seq == _seq) _scheduleClockRetry(list);
    return list;
  }

  /// Read the list again. A failed read is shown (with Retry); it never keeps
  /// an old list that might say a failing check passes.
  Future<void> refresh() async {
    final api = ref.read(polarAlignApiProvider);
    if (api == null) return;
    final seq = ++_seq;
    try {
      final next = await api.preflight(handTurn: true);
      if (!ref.mounted || seq != _seq) return;
      state = AsyncData(next);
      _scheduleClockRetry(next);
    } catch (e, st) {
      if (ref.mounted && seq == _seq) state = AsyncError(e, st);
    }
  }

  void _scheduleClockRetry(PolarAlignPreflight? list) {
    _clockRetry?.cancel();
    _clockRetry = null;
    final clockFails = list?.check(_clockCheck)?.fails ?? false;
    if (!clockFails) {
      _clockRetries = 0;
      return;
    }
    if (_clockRetries >= maxClockRetries) return;
    _clockRetries++;
    _clockRetry = Timer(clockRetryDelay, () {
      if (ref.mounted) unawaited(refresh());
    });
  }
}

final polarAlignPreflightProvider =
    AsyncNotifierProvider.autoDispose<
      PolarAlignPreflightNotifier,
      PolarAlignPreflight?
    >(PolarAlignPreflightNotifier.new);

/// True when a check of the rig's list fails, so Start is off: the rig would
/// refuse it. While the list loads, after a failed read, or from a rig with no
/// list, Start stays on and the rig's own start refusals stand. Pure —
/// unit-tested through the panels.
bool polarPreflightBlocksStart(AsyncValue<PolarAlignPreflight?> preflight) =>
    switch (preflight) {
      AsyncData(:final value) => value?.blocksStart ?? false,
      _ => false,
    };

/// [polarPreflightBlocksStart] for a Check: only the checks that do not depend
/// on the turn step ([PolarAlignPreflight.blocksCheck]). The list is the fine
/// stage's; the rig judges a Check's own start pointing when it starts.
bool polarPreflightBlocksCheck(AsyncValue<PolarAlignPreflight?> preflight) =>
    switch (preflight) {
      AsyncData(:final value) => value?.blocksCheck ?? false,
      _ => false,
    };
