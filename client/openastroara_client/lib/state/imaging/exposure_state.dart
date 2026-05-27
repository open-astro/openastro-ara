import 'package:flutter_riverpod/flutter_riverpod.dart';

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
  @override
  ExposureParams build() => const ExposureParams();

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
