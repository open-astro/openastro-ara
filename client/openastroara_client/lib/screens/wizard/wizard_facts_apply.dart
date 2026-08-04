import '../../models/equipment_readiness.dart';
import '../../models/profile_draft.dart';
import '../../state/settings/equipment_connection_state.dart';

/// §76 — write a card's read facts into the [ProfileDraft] so the existing
/// wizard-save mappers persist them. Only non-null facts are written; a value
/// the device didn't report never CLEARS what the user typed into a card's
/// fallback field (the inline "enter it here" escape hatch for non-reporting
/// gear). Pure, so the rules unit-test without widgets.
void applyFactsToDraft(ProfileDraft draft, DeviceReadiness r) {
  final p = r.payload;
  if (p == null) return; // unreachable card — nothing was read

  switch (r.type) {
    case EquipmentDeviceType.camera:
      final g = p.camera;
      if (g != null) {
        draft.camera.pixelSizeMicrons = g.pixelSizeUm;
      }
      break;
    case EquipmentDeviceType.mount:
      final optics = p.optics;
      if (optics?.focalLengthMm != null) {
        draft.telescope.focalLengthMm = optics!.focalLengthMm;
      }
      if (optics?.apertureMm != null) {
        draft.telescope.apertureMm = optics!.apertureMm;
      }
      final props = p.mountProps;
      if (props?.name != null) {
        draft.mount.name = props!.name;
        // The optics ride on the mount's Alpaca device, so its name is the
        // best available "telescope name" too (only when the user hasn't
        // typed one — a name is identity, not telemetry).
        draft.telescope.name ??= props.name;
      }
      if (props?.maxSlewRateDegPerSec != null) {
        draft.mount.slewRateDegPerSec = props!.maxSlewRateDegPerSec;
      }
      break;
    case EquipmentDeviceType.filterWheel:
      final named = p.filterWheelSlots?.slots
              .where((s) => s.name.trim().isNotEmpty)
              .toList() ??
          const [];
      if (named.isNotEmpty) {
        draft.filterWheel.filters
          ..clear()
          ..addAll(named.map((s) => FilterDef()
            ..name = s.name.trim()
            ..type = inferFilterType(s.name)
            ..focusOffsetSteps = s.focusOffset));
      }
      break;
    case EquipmentDeviceType.focuser:
      final props = p.focuserProps;
      if (props?.stepSizeUm != null) {
        draft.focuser.stepSizeMicrons = props!.stepSizeUm;
      }
      break;
    case EquipmentDeviceType.rotator:
      final props = p.rotatorProps;
      if (props?.stepDeg != null) {
        draft.rotator.stepDeg = props!.stepDeg;
      }
      if (props != null && props.canReverse) {
        // Reverse is a DRIVER fact (e.g. the ZWO CAA's own setting) — mirror
        // it; the driver stays the source of truth (§76.1).
        draft.rotator.reverse = props.reverse;
      }
      break;
    default:
      break; // fact-free types carry no draft fields
  }
}

/// Best-effort filter classification from its name — narrowband lines and
/// "nm" widths, luminance/clear tokens, else broadband. The user can refine
/// later in Options; this only seeds the §37.8 default the old Filter Wheel
/// screen used to ask for.
FilterType inferFilterType(String rawName) {
  final n = rawName.trim().toLowerCase();
  if (n.isEmpty) return FilterType.broadband;
  // Word-boundary matching so "Chroma" isn't a Hα and "Astronomik" isn't an
  // "nm" — narrowband tokens must stand alone within the name.
  final narrowband = RegExp(
      r'(^|[^a-zα-ω])(ha|hα|halpha|h-alpha|oiii|o3|sii|s2|nb)([^a-zα-ω]|$)');
  if (narrowband.hasMatch(n) || RegExp(r'\d+\s*nm(\W|$)').hasMatch(n)) {
    return FilterType.narrowband;
  }
  if (n == 'l' || n.startsWith('lum')) return FilterType.luminance;
  if (n.contains('clear') || n == 'c') return FilterType.clear;
  return FilterType.broadband;
}
