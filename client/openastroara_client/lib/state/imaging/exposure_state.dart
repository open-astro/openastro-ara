import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/filter_wheel_status.dart';
import '../app_shell_state.dart';
import '../equipment/filter_wheel_state.dart';
import '../settings/settings_nav.dart';

/// State for the Imaging tab's exposure controls per playbook §25.5.1.
/// Per-frame values are kept in memory only; the active profile's defaults
/// (loaded from the wizard's `ImagingDefaults`) seed these on first build,
/// and `Take One` pushes the current values to the daemon. Sequence-driven
/// values come from the Sequencer tab and override these.

class ExposureParams {
  final Duration exposure;
  final int gain;
  final int offset;
  final int bin;
  final String filterSlot;
  final FrameKind frameKind;
  /// True while the wheel is being homed to slot 0 (L) on first launch — the
  /// picker shows busy until the wheel is OBSERVED there.
  final bool homing;

  const ExposureParams({
    this.exposure = const Duration(seconds: 5),
    this.gain = 100,
    this.offset = 10,
    this.bin = 1,
    this.filterSlot = 'L',
    this.frameKind = FrameKind.light,
    this.homing = false,
  });

  ExposureParams copyWith({
    Duration? exposure,
    int? gain,
    int? offset,
    int? bin,
    String? filterSlot,
    FrameKind? frameKind,
    bool? homing,
  }) =>
      ExposureParams(
        exposure: exposure ?? this.exposure,
        gain: gain ?? this.gain,
        offset: offset ?? this.offset,
        bin: bin ?? this.bin,
        filterSlot: filterSlot ?? this.filterSlot,
        frameKind: frameKind ?? this.frameKind,
        homing: homing ?? this.homing,
      );
}

enum FrameKind { light, dark, bias, flat }

class ExposureController extends Notifier<ExposureParams> {

  /// The physical wheel slot whose name [state.filterSlot] last followed.
  /// Kept on the notifier so a manual picker choice survives until the wheel
  /// actually lands on a different slot. Cleared on disconnect/device change
  /// so a reconnect at the same physical position re-syncs (a pick made while
  /// the wheel was offline must not go stale forever).
  int? _lastSyncedSlot;
  String? _lastDeviceId;

  /// Whether the wheel has been homed to slot 0 (L) this session — the
  /// default filter on first launch. Only the FIRST connect homes it.
  bool _homed = false;

  /// Safety valve: if a home command fails (rejected / re-entrancy / driver
  /// error) or the wheel never reports reaching slot 0, the picker must not
  /// stay busy forever.
  Timer? _homeTimeout;

  @override
  ExposureParams build() {
    ref.onDispose(() => _homeTimeout?.cancel());
    // §25.5 follow-up: the Imaging picker follows the physical wheel. Whenever
    // the wheel is connected and parked on a slot — moved here via the §37.4
    // Filter Wheel panel's Select, a sequence, or another client — the picker
    // snaps to that slot's name so manual captures record the filter actually
    // in the light path. Edge-triggered on the slot position: a manual picker
    // choice is left alone until the wheel really moves.
    ref.listen(filterWheelProvider, (prev, next) {
      // Only ACTUAL device states may touch the latch. AsyncLoading/AsyncError
      // are transient (a failed poll while the wheel stays physically
      // connected and parked) — clearing here would make the next successful
      // poll look like a fresh connection and clobber a manual pick.
      final data = next.asData;
      if (data == null) return;
      final status = data.value;
      // Genuinely no device, or disconnected: forget the latch so a reconnect
      // at the same physical position still re-syncs — otherwise a pick made
      // while the wheel was offline would survive a reconnect forever even
      // though the wheel is parked somewhere else.
      if (status == null || !status.isConnected) {
        _lastSyncedSlot = null;
        _lastDeviceId = null;
        return;
      }
      // A different wheel (or the first sighting): the old latch belongs to
      // the previous device — re-sync against this one. On the FIRST connect
      // of a session, home the wheel to slot 0 (L) — the default filter.
      if (status.deviceId != _lastDeviceId) {
        final firstConnect = _lastDeviceId == null;
        _lastSyncedSlot = null;
        _lastDeviceId = status.deviceId;
        if (firstConnect &&
            !_homed &&
            status.currentSlot != null &&
            status.currentSlot != 0) {
          _homeToSlot0();
        }
      }
      // Homing completes once the wheel is observed on slot 0 (or the wheel
      // goes away) — the picker drops the busy state and shows L.
      if (state.homing &&
          (status.currentSlot == 0 ||
              !status.isConnected ||
              status.deviceId != _lastDeviceId)) {
        setHoming(false);
      }
      if (status.isMoving) return;
      _syncFilterToSlot(status);
    });
    // ref.listen only fires on changes — if the wheel was already parked on a
    // slot before this provider first built (e.g. another tab polled it first),
    // snap once now so the picker is truthful from the first frame.
    Future.microtask(() {
      if (!ref.mounted) return;
      final status = ref
          .read(filterWheelProvider)
          .maybeWhen(data: (s) => s, orElse: () => null);
      if (status != null && status.isConnected && !status.isMoving) {
        // First launch with the wheel already connected before this provider
        // built: home to slot 0 (L) too.
        if (!_homed && status.currentSlot != null && status.currentSlot != 0) {
          _homeToSlot0();
        }
        if (state.homing && status.currentSlot == 0) {
          state = state.copyWith(homing: false);
        }
        _syncFilterToSlot(status);
      }
    });
    // Re-entering the Live/Imaging tab: reflect the wheel's CURRENT slot even
    // when the latch would normally skip it (the wheel may have moved while on
    // another tab — a sequence, the §37.4 panel, or an external client).
    ref.listen(selectedTabIndexProvider, (prev, next) {
      if (next != kLiveTabIndex) return;
      Future.microtask(() {
        if (!ref.mounted) return;
        final status = ref
            .read(filterWheelProvider)
            .maybeWhen(data: (s) => s, orElse: () => null);
        if (status == null || !status.isConnected || status.isMoving) return;
        _lastSyncedSlot = null; // force: the wheel's current slot wins here
        _syncFilterToSlot(status);
      });
    });
    return const ExposureParams();
  }

  void _syncFilterToSlot(FilterWheelStatus status) {
    final pos = status.currentSlot;
    if (pos == null || pos == _lastSyncedSlot) return;
    for (final slot in status.slots) {
      if (slot.position == pos && slot.name.isNotEmpty) {
        // Latch only once a named slot was actually found: a wheel parked on
        // an as-yet-unnamed slot (driver hasn't reported names yet) must still
        // sync when the name arrives on a later poll — latching early would
        // short-circuit that forever (the slot never "moves").
        _lastSyncedSlot = pos;
        if (slot.name != state.filterSlot) {
          state = state.copyWith(filterSlot: slot.name);
        }
        return;
      }
    }
  }

  // Setters validate at the boundary so downstream consumers (Take One
  // payload, sequence import) can't propagate physically-impossible values.
  void setExposure(Duration d) {
    if (d <= Duration.zero) return;
    state = state.copyWith(exposure: d);
  }

  void setGain(int v) {
    if (v < 0) return;
    state = state.copyWith(gain: v);
  }

  void setOffset(int v) {
    if (v < 0) return;
    state = state.copyWith(offset: v);
  }

  void setBin(int v) {
    if (v < 1) return;
    state = state.copyWith(bin: v);
  }

  void setFilterSlot(String s) {
    if (s.isEmpty) return;
    state = state.copyWith(filterSlot: s);
  }

  void setFrameKind(FrameKind k) => state = state.copyWith(frameKind: k);

  /// Marks the first-launch home-to-L as in progress (or done). The picker
  /// renders busy while true, so the pre-home slot is never shown as current.
  void setHoming(bool v) => state = state.copyWith(homing: v);

  /// Homes the wheel to slot 0 (L) on first connect. The picker shows busy
  /// until the wheel is OBSERVED on slot 0; if the command fails, is dropped
  /// by re-entrancy, or the wheel never reports arriving, [homing] is cleared
  /// so the picker can never be left disabled forever.
  void _homeToSlot0() {
    _homed = true;
    if (!state.homing) setHoming(true);
    _homeTimeout?.cancel();
    _homeTimeout = Timer(const Duration(seconds: 20), () {
      // The wheel never reported slot 0 (or the command was dropped) — stop
      // showing busy; the follow-logic keeps the picker truthful to wherever
      // the wheel actually is.
      if (state.homing) setHoming(false);
    });
    ref.read(filterWheelProvider.notifier).changeFilter(0).then((ok) {
      if (!ok && state.homing) setHoming(false); // dropped (re-entrancy)
    }).catchError((Object e) {
      if (state.homing) setHoming(false); // home failed — don't stay stuck
    });
  }
}

final exposureControllerProvider =
    NotifierProvider<ExposureController, ExposureParams>(
        ExposureController.new);
