import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/filter_wheel_status.dart';
import '../equipment/filter_wheel_state.dart';

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

  const ExposureParams({
    this.exposure = const Duration(seconds: 5),
    this.gain = 100,
    this.offset = 10,
    this.bin = 1,
    this.filterSlot = 'L',
    this.frameKind = FrameKind.light,
  });

  ExposureParams copyWith({
    Duration? exposure,
    int? gain,
    int? offset,
    int? bin,
    String? filterSlot,
    FrameKind? frameKind,
  }) =>
      ExposureParams(
        exposure: exposure ?? this.exposure,
        gain: gain ?? this.gain,
        offset: offset ?? this.offset,
        bin: bin ?? this.bin,
        filterSlot: filterSlot ?? this.filterSlot,
        frameKind: frameKind ?? this.frameKind,
      );
}

enum FrameKind { light, dark, bias, flat }

class ExposureController extends Notifier<ExposureParams> {
  /// The physical wheel slot whose name [state.filterSlot] last followed.
  /// Kept on the notifier so a manual picker choice survives until the wheel
  /// actually lands on a different slot.
  int? _lastSyncedSlot;

  @override
  ExposureParams build() {
    // §25.5 follow-up: the Imaging picker follows the physical wheel. Whenever
    // the wheel is connected and parked on a slot — moved here via the §37.4
    // Filter Wheel panel's Select, a sequence, or another client — the picker
    // snaps to that slot's name so manual captures record the filter actually
    // in the light path. Edge-triggered on the slot position: a manual picker
    // choice is left alone until the wheel really moves.
    ref.listen(filterWheelProvider, (prev, next) {
      final status = next.maybeWhen(data: (s) => s, orElse: () => null);
      if (status == null || !status.isConnected || status.isMoving) return;
      _syncFilterToSlot(status);
    });
    // ref.listen only fires on changes — if the wheel was already parked on a
    // slot before this provider first built (e.g. another tab polled it first),
    // snap once now so the picker is truthful from the first frame.
    Future.microtask(() {
      final status = ref
          .read(filterWheelProvider)
          .maybeWhen(data: (s) => s, orElse: () => null);
      if (status != null && status.isConnected && !status.isMoving) {
        _syncFilterToSlot(status);
      }
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
}

final exposureControllerProvider =
    NotifierProvider<ExposureController, ExposureParams>(
        ExposureController.new);
