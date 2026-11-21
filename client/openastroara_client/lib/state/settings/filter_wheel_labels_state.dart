import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';
import '../equipment/filter_wheel_state.dart';
import '../profile_management_state.dart';
import 'loaded_section.dart';

/// §37.4 + §46.2 filter wheel slot labels, daemon-backed. ADR 0015: the
/// notifier loads itself from `GET /api/v1/profile/filter-wheel/labels` and
/// holds nothing until the rig answered (no reference-8 defaults), so a slot
/// edit can never write defaults over the names on the rig. Every consumer —
/// the §38 editor's filter picker, the filter-set "seed from wheel labels"
/// button, the Settings panel — reads `.value`. Edits persist via
/// [persistToServer] (the Settings panel calls it per committed row).

class FilterWheelLabels {
  // Stored unmodifiable so consumers can't mutate the slot list in place.
  // Values are trimmed at this layer so the invariant holds regardless of
  // whether they came in via the notifier setter or a future daemon-hydration
  // call site that constructs the model directly.
  final List<String> _labels;

  // Required: no reference-8 default, labels come only from the rig (ADR 0015).
  FilterWheelLabels({required List<String> labels})
    : _labels = List.unmodifiable(labels.map((s) => s.trim()));

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

class FilterWheelLabelsNotifier extends SelfLoadingSection<FilterWheelLabels> {
  @override
  LoadedSection<FilterWheelLabels> build() {
    // The daemon mirrors the device's slot names into these labels
    // (MirrorSlotLabels, outside the profile routes) once the wheel reports
    // them. A daemon with #1807 publishes `profile.updated` for that write;
    // an older one does not, so reload when the reported names go from none
    // to the device's list or one changes; the connect edge alone does not
    // (the labels are not mirrored yet then).
    ref.listen(
      filterWheelProvider.select(
        (s) => s.value?.slots.map((e) => e.name).join('\u0000') ?? '',
      ),
      (previous, next) {
        final api = ref.read(profileApiProvider);
        if (next.isNotEmpty && next != previous && api != null) load(api);
      },
    );
    return super.build();
  }

  void setLabel(int slot, String label) =>
      edit((c) => c.withLabel(slot, label));

  /// Save the edited labels; the panel calls it per committed row.
  Future<void> persistToServer() {
    final api = ref.read(profileApiProvider);
    if (api == null) {
      throw StateError('Not connected — connect to your rig to save this.');
    }
    return save(api);
  }

  @override
  Future<Tagged<FilterWheelLabels>> fetch(ProfileApi api) =>
      api.getFilterWheelLabelsSection();

  @override
  Future<Tagged<FilterWheelLabels>> send(
    ProfileApi api,
    FilterWheelLabels value,
    String? etag,
  ) => api.putFilterWheelLabelsSection(value, ifMatch: etag);
}

final filterWheelLabelsProvider =
    NotifierProvider<
      FilterWheelLabelsNotifier,
      LoadedSection<FilterWheelLabels>
    >(FilterWheelLabelsNotifier.new);
