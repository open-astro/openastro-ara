import '../services/camera_geometry_api.dart';
import '../services/filter_wheel_names_api.dart';
import '../services/focuser_props_api.dart';
import '../services/rotator_props_api.dart';
import '../services/telescope_optics_api.dart';
import '../state/settings/equipment_connection_state.dart';
import 'discovered_device.dart';

/// §76.3 — one device card's verification state on the Wizard 2.0
/// "Your equipment" screen. Facts are READ from Alpaca and displayed, never
/// asked; a gap sends the user to the Alpaca setup page (the fact's owner)
/// rather than opening a duplicate ARA form.
enum ReadinessState {
  /// Connect + read in flight — the card shows a spinner.
  reading,

  /// Every required fact was read — ✅.
  ready,

  /// Connected and read, but at least one fact is missing — ⚠️ with the
  /// deep link + Recheck affordance.
  gaps,

  /// The assigned device wasn't on the bridge, never finished connecting, or
  /// the read failed — ⚠️ with a retry affordance.
  unreachable,
}

/// How much a missing fact matters. A [required] gap makes the card amber
/// (imaging math needs it — pixel size, focal length); an [informational] one
/// is merely noted (e.g. a focuser that doesn't report its step size — common
/// and workable).
enum FactNeed { required, informational }

/// One fact successfully read from the device, ready for display
/// ("Pixel size" / "3.76 µm").
class DeviceFact {
  final String label;
  final String value;
  const DeviceFact(this.label, this.value);
}

/// One fact the device SHOULD have reported but didn't. [hint] says where to
/// fix it (usually "set it in AlpacaBridge, then Recheck").
class DeviceGap {
  final String label;
  final FactNeed need;
  final String hint;
  const DeviceGap(this.label, this.need, this.hint);
}

/// The full readiness picture for one assigned device slot.
class DeviceReadiness {
  final EquipmentDeviceType type;

  /// Display label — the real device name from the bridge when known,
  /// otherwise the generic discovery name.
  final String label;

  final ReadinessState state;
  final List<DeviceFact> facts;
  final List<DeviceGap> gaps;

  /// The Alpaca-standard browser setup page for this device, when the
  /// discovery record is known ([alpacaDeviceSetupUri]).
  final Uri? setupUri;

  const DeviceReadiness({
    required this.type,
    required this.label,
    required this.state,
    this.facts = const [],
    this.gaps = const [],
    this.setupUri,
  });

  /// True when a REQUIRED fact is missing — the ⚠️ that gates green.
  bool get hasBlockingGap =>
      gaps.any((g) => g.need == FactNeed.required) ||
      state == ReadinessState.unreachable;

  DeviceReadiness copyWith({
    ReadinessState? state,
    List<DeviceFact>? facts,
    List<DeviceGap>? gaps,
    String? label,
    Uri? setupUri,
  }) =>
      DeviceReadiness(
        type: type,
        label: label ?? this.label,
        state: state ?? this.state,
        facts: facts ?? this.facts,
        gaps: gaps ?? this.gaps,
        setupUri: setupUri ?? this.setupUri,
      );
}

/// The device's browser setup page per the ASCOM Alpaca spec
/// (`/setup/v1/{device_type}/{device_number}/setup`) — spec-mandated on every
/// conformant server, so the wizard's "fix it in AlpacaBridge" deep link works
/// against AlpacaBridge and third-party servers alike.
Uri alpacaDeviceSetupUri(DiscoveredDevice d) => Uri(
      scheme: d.useHttps ? 'https' : 'http',
      host: d.ipAddress.isNotEmpty ? d.ipAddress : d.hostName,
      port: d.ipPort,
      path:
          '/setup/v1/${DiscoveredDevice.pathSegmentFor(d.deviceType)}/${d.alpacaDeviceNumber}/setup',
    );

/// Trim a double for display: "3.76", "530", "2.9".
String _num(double v) {
  final s = v.toStringAsFixed(2);
  return s.replaceFirst(RegExp(r'\.?0+$'), '');
}

const String _fixInBridge = 'Set it in AlpacaBridge, then Recheck.';

/// §76.3 classifiers — pure result → (facts, gaps) rules, one per fact-bearing
/// device type. Each takes the API read result where `null` means "connected
/// but reported nothing usable" (transport failures never reach a classifier —
/// the notifier maps those to [ReadinessState.unreachable]).
abstract final class ReadinessRules {
  static DeviceReadiness camera(String label, Uri? setupUri, CameraGeometry? g) {
    if (g == null) {
      return DeviceReadiness(
        type: EquipmentDeviceType.camera,
        label: label,
        state: ReadinessState.gaps,
        setupUri: setupUri,
        gaps: const [
          DeviceGap('Pixel size + sensor geometry', FactNeed.required,
              'The camera did not report a usable sensor. $_fixInBridge'),
        ],
      );
    }
    return DeviceReadiness(
      type: EquipmentDeviceType.camera,
      label: label,
      state: ReadinessState.ready,
      setupUri: setupUri,
      facts: [
        DeviceFact('Pixel size', '${_num(g.pixelSizeUm)} µm'),
        DeviceFact('Sensor', '${g.sensorWidthPx}×${g.sensorHeightPx}'),
        if (g.maxBin > 1) DeviceFact('Binning', 'up to ${g.maxBin}×${g.maxBin}'),
      ],
    );
  }

  static DeviceReadiness mount(String label, Uri? setupUri,
      TelescopeOptics? optics, MountProps? props) {
    final facts = <DeviceFact>[];
    final gaps = <DeviceGap>[];
    final fl = optics?.focalLengthMm;
    final ap = optics?.apertureMm;
    if (fl != null) {
      facts.add(DeviceFact('Focal length', '${_num(fl)} mm'));
    } else {
      gaps.add(const DeviceGap('Focal length', FactNeed.required,
          'The driver does not report it. $_fixInBridge'));
    }
    if (ap != null) {
      facts.add(DeviceFact('Aperture', '${_num(ap)} mm'));
    } else {
      gaps.add(const DeviceGap('Aperture', FactNeed.required,
          'The driver does not report it. $_fixInBridge'));
    }
    final rate = props?.maxSlewRateDegPerSec;
    if (rate != null) {
      facts.add(DeviceFact('Max slew rate', '${_num(rate)}°/s'));
    }
    return DeviceReadiness(
      type: EquipmentDeviceType.mount,
      label: props?.name ?? label,
      state: gaps.isEmpty ? ReadinessState.ready : ReadinessState.gaps,
      setupUri: setupUri,
      facts: facts,
      gaps: gaps,
    );
  }

  static DeviceReadiness filterWheel(
      String label, Uri? setupUri, FilterWheelSlots? slots) {
    final list = slots?.slots ?? const <FilterWheelSlot>[];
    final named = list.where((s) => s.name.trim().isNotEmpty).toList();
    if (named.isEmpty) {
      return DeviceReadiness(
        type: EquipmentDeviceType.filterWheel,
        label: label,
        state: ReadinessState.gaps,
        setupUri: setupUri,
        gaps: [
          DeviceGap(
              'Filter names',
              FactNeed.required,
              slots == null
                  ? 'The wheel reported no slots. $_fixInBridge'
                  : 'No slot has a name. $_fixInBridge'),
        ],
      );
    }
    final anyOffset = named.any((s) => s.focusOffset != 0);
    return DeviceReadiness(
      type: EquipmentDeviceType.filterWheel,
      label: label,
      state: ReadinessState.ready,
      setupUri: setupUri,
      facts: [
        DeviceFact('Filters', named.map((s) => s.name.trim()).join(' · ')),
        if (anyOffset) const DeviceFact('Focus offsets', 'read from the wheel'),
      ],
    );
  }

  static DeviceReadiness focuser(
      String label, Uri? setupUri, FocuserProps? props) {
    final facts = <DeviceFact>[];
    final gaps = <DeviceGap>[];
    final um = props?.stepSizeUm;
    if (um != null) {
      facts.add(DeviceFact('Step size', '${_num(um)} µm/step'));
    } else {
      // Most focusers (e.g. the ZWO EAF) don't report it — workable, autofocus
      // operates in steps; the µm figure only refines seeding.
      gaps.add(const DeviceGap('Step size', FactNeed.informational,
          'Not reported (common — e.g. ZWO EAF). Autofocus works in steps.'));
    }
    facts.add(DeviceFact('Temperature compensation',
        (props?.canTempComp ?? false) ? 'available' : 'not available'));
    return DeviceReadiness(
      type: EquipmentDeviceType.focuser,
      label: label,
      state: ReadinessState.ready,
      setupUri: setupUri,
      facts: facts,
      gaps: gaps,
    );
  }

  static DeviceReadiness rotator(
      String label, Uri? setupUri, RotatorProps? props) {
    final facts = <DeviceFact>[];
    final gaps = <DeviceGap>[];
    final step = props?.stepDeg;
    if (step != null) {
      facts.add(DeviceFact('Step size', '${_num(step)}°'));
    } else {
      gaps.add(const DeviceGap('Step size', FactNeed.informational,
          'Not reported (common — e.g. ZWO CAA).'));
    }
    if (props?.canReverse ?? false) {
      facts.add(
          DeviceFact('Reverse', (props?.reverse ?? false) ? 'on' : 'off'));
    }
    return DeviceReadiness(
      type: EquipmentDeviceType.rotator,
      label: label,
      state: ReadinessState.ready,
      setupUri: setupUri,
      facts: facts,
      gaps: gaps,
    );
  }
}
