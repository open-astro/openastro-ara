import 'package:flutter/foundation.dart';

import 'equipment_device_status.dart';

/// One slot of the filter wheel: its [position] index, the configured filter
/// [name], and the per-filter [focusOffset] (steps) applied on selection.
class FilterSlot {
  final int position;
  final String name;
  final int focusOffset;

  const FilterSlot(
      {required this.position, required this.name, required this.focusOffset});

  factory FilterSlot.fromJson(Map<String, dynamic> json) => FilterSlot(
        position: (json['position'] as num?)?.toInt() ?? 0,
        name: json['name'] as String? ?? '',
        focusOffset: (json['focus_offset'] as num?)?.toInt() ?? 0,
      );

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is FilterSlot &&
          other.position == position &&
          other.name == name &&
          other.focusOffset == focusOffset);

  @override
  int get hashCode => Object.hash(position, name, focusOffset);
}

/// Live status of the connected ASCOM FilterWheel
/// (`GET /api/v1/equipment/filterwheel` → `FilterWheelDto`). The slot names +
/// focus offsets are the device's own (authoritative), not local labels.
class FilterWheelStatus extends EquipmentDeviceStatus {
  final String deviceId;
  @override
  final String name;
  @override
  final EquipmentConnectionState connectionState;

  final String runtimeState;
  /// The active slot position, or `null` while moving (`-1` on the wire) / unknown.
  final int? currentSlot;
  final List<FilterSlot> slots;

  FilterWheelStatus({
    required this.deviceId,
    required this.name,
    required this.connectionState,
    required this.runtimeState,
    required this.currentSlot,
    required this.slots,
  });

  bool get isMoving => runtimeState == 'moving';

  @override
  bool get isBusy => isMoving;

  /// The currently-selected slot, or `null` if none / mid-move.
  FilterSlot? get current {
    for (final s in slots) {
      if (s.position == currentSlot) return s;
    }
    return null;
  }

  factory FilterWheelStatus.fromJson(Map<String, dynamic> json) {
    final runtime = json['runtime'];
    final r = runtime is Map<String, dynamic> ? runtime : const <String, dynamic>{};
    final rawSlots = json['slots'];
    final slots = rawSlots is List
        ? rawSlots
            .whereType<Map<String, dynamic>>()
            .map(FilterSlot.fromJson)
            .toList(growable: false)
        : const <FilterSlot>[];
    // The wire sends -1 while moving / when no slot is selected; normalize that to
    // null so `currentSlot != null` reliably means "a slot is selected".
    final rawSlot = (r['current_slot'] as num?)?.toInt();
    return FilterWheelStatus(
      deviceId: json['device_id'] as String? ?? '',
      name: json['name'] as String? ?? '',
      connectionState: equipmentConnectionStateFromWire(json['state'] as String?),
      runtimeState: r['state'] as String? ?? '',
      currentSlot: (rawSlot != null && rawSlot >= 0) ? rawSlot : null,
      slots: slots,
    );
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is FilterWheelStatus &&
          other.deviceId == deviceId &&
          other.name == name &&
          other.connectionState == connectionState &&
          other.runtimeState == runtimeState &&
          other.currentSlot == currentSlot &&
          listEquals(other.slots, slots));

  @override
  int get hashCode => Object.hash(deviceId, name, connectionState, runtimeState,
      currentSlot, Object.hashAll(slots));
}
