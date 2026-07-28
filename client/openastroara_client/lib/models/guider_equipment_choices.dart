/// Client mirror of the daemon's §63.17 guider equipment-choice lists
/// (`GuiderEquipmentChoicesDto`, returned inside
/// `GuiderEquipmentChoicesResponseDto` from `GET /api/v1/equipment/guider/choices`).
/// Snake_case wire.
library;

/// Per-slot device-name lists as the guider daemon offers them (its own
/// equipment-dialog strings). Values are passed back verbatim to the §63.17
/// apply path; empty lists mean the daemon offers nothing for that slot.
/// Defensive parse — missing/wrong-typed fields degrade rather than throw.
class GuiderEquipmentChoices {
  final List<String> cameras;
  final List<String> mounts;
  final List<String> auxMounts;
  final List<String> adaptiveOptics;
  final List<String> rotators;

  const GuiderEquipmentChoices({
    this.cameras = const [],
    this.mounts = const [],
    this.auxMounts = const [],
    this.adaptiveOptics = const [],
    this.rotators = const [],
  });

  factory GuiderEquipmentChoices.fromJson(Map<String, dynamic> json) =>
      GuiderEquipmentChoices(
        cameras: _strList(json['cameras']),
        mounts: _strList(json['mounts']),
        auxMounts: _strList(json['aux_mounts']),
        adaptiveOptics: _strList(json['adaptive_optics']),
        rotators: _strList(json['rotators']),
      );

  static List<String> _strList(dynamic v) =>
      v is List ? List.unmodifiable(v.whereType<String>()) : const [];

  static bool _listEq(List<String> a, List<String> b) {
    if (a.length != b.length) return false;
    for (var i = 0; i < a.length; i++) {
      if (a[i] != b[i]) return false;
    }
    return true;
  }

  // Value equality so an unchanged poll doesn't churn the widgets watching the
  // provider (matches the project's other models).
  @override
  bool operator ==(Object other) =>
      other is GuiderEquipmentChoices &&
      _listEq(other.cameras, cameras) &&
      _listEq(other.mounts, mounts) &&
      _listEq(other.auxMounts, auxMounts) &&
      _listEq(other.adaptiveOptics, adaptiveOptics) &&
      _listEq(other.rotators, rotators);

  @override
  int get hashCode => Object.hashAll([
        Object.hashAll(cameras),
        Object.hashAll(mounts),
        Object.hashAll(auxMounts),
        Object.hashAll(adaptiveOptics),
        Object.hashAll(rotators),
      ]);
}

/// The choices read's envelope: [connected] distinguishes "guider not
/// connected" ([choices] null) from "connected, here are the device lists" —
/// same 200-always contract as `CalibrationStatusResponse`.
class GuiderEquipmentChoicesResponse {
  final bool connected;
  final GuiderEquipmentChoices? choices;
  const GuiderEquipmentChoicesResponse({required this.connected, this.choices});

  factory GuiderEquipmentChoicesResponse.fromJson(Map<String, dynamic> json) {
    final connected = json['connected'];
    final c = json['choices'];
    return GuiderEquipmentChoicesResponse(
      connected: connected is bool && connected,
      choices:
          c is Map<String, dynamic> ? GuiderEquipmentChoices.fromJson(c) : null,
    );
  }

  @override
  bool operator ==(Object other) =>
      other is GuiderEquipmentChoicesResponse &&
      other.connected == connected &&
      other.choices == choices;

  @override
  int get hashCode => Object.hash(connected, choices);
}
