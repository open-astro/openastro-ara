import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../profile_management_state.dart';

/// §37.4 + §46.2 filter wheel slot labels, daemon-backed (the 12h.2b
/// round-trip): the notifier SELF-HYDRATES from
/// `GET /api/v1/profile/filter-wheel/labels` whenever the active server
/// changes, so every consumer — the §38 editor's filter picker, the
/// filter-set "seed from wheel labels" button, the Settings panel — sees the
/// profile's labels without per-surface plumbing; offline it keeps the
/// reference-8 defaults (which the daemon's own default mirrors, so first
/// hydration is visually a no-op). Edits persist via [persistToServer]
/// (the Settings panel calls it per committed row).

class FilterWheelLabels {
  // Stored unmodifiable so consumers can't mutate the slot list in place.
  // Values are trimmed at this layer so the invariant holds regardless of
  // whether they came in via the notifier setter or a future daemon-hydration
  // call site that constructs the model directly.
  final List<String> _labels;

  FilterWheelLabels({
    List<String> labels = const ['L', 'R', 'G', 'B', 'Hα', 'OIII', 'SII', ''],
  }) : _labels = List.unmodifiable(labels.map((s) => s.trim()));

  int get slotCount => _labels.length;

  /// 1-indexed (slot 1 is the first slot). Out-of-range returns empty.
  String labelAt(int slot) {
    final i = slot - 1;
    if (i < 0 || i >= _labels.length) return '';
    return _labels[i];
  }

  FilterWheelLabels withLabel(int slot, String label) {
    final i = slot - 1;
    if (i < 0 || i >= _labels.length) return this;
    final next = [..._labels];
    next[i] = label.trim();
    return FilterWheelLabels(labels: next);
  }

  @override
  bool operator ==(Object other) {
    if (identical(this, other)) return true;
    if (other is! FilterWheelLabels) return false;
    if (other._labels.length != _labels.length) return false;
    for (var i = 0; i < _labels.length; i++) {
      if (other._labels[i] != _labels[i]) return false;
    }
    return true;
  }

  @override
  int get hashCode => Object.hashAll(_labels);
}

class FilterWheelLabelsNotifier extends Notifier<FilterWheelLabels> {
  // Bumped per build (= per active-server change): an in-flight hydration
  // from the OLD server must not land over the new server's state.
  int _generation = 0;

  @override
  FilterWheelLabels build() {
    final api = ref.watch(profileApiProvider);
    final gen = ++_generation;
    if (api != null) {
      // Self-hydrate once the build settles (a provider can't be modified
      // mid-build). Failures keep the defaults — offline authoring still works.
      Future.microtask(() async {
        try {
          final loaded = await api.getFilterWheelLabels();
          if (_generation == gen) state = loaded;
        } catch (e) {
          debugPrint('[filter-wheel] labels hydration failed (keeping defaults): $e');
        }
      });
    }
    return FilterWheelLabels();
  }

  void setLabel(int slot, String label) {
    state = state.withLabel(slot, label);
  }

  /// Send the current labels to the daemon; returns its echo (labels trimmed
  /// server-side) and adopts it. Throws on transport/validation failure — the
  /// caller surfaces it (the panel shows a SnackBar).
  Future<void> persistToServer() async {
    final api = ref.read(profileApiProvider);
    if (api == null) {
      throw StateError('No active server — connect to a daemon first.');
    }
    state = await api.putFilterWheelLabels(state);
  }
}

final filterWheelLabelsProvider =
    NotifierProvider<FilterWheelLabelsNotifier, FilterWheelLabels>(
        FilterWheelLabelsNotifier.new);
