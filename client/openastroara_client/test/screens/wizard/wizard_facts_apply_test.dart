import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_readiness.dart';
import 'package:openastroara/models/profile_draft.dart';
import 'package:openastroara/screens/wizard/wizard_facts_apply.dart';
import 'package:openastroara/services/camera_geometry_api.dart';
import 'package:openastroara/services/filter_wheel_names_api.dart';
import 'package:openastroara/services/rotator_props_api.dart';
import 'package:openastroara/services/telescope_optics_api.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';

DeviceReadiness _r(EquipmentDeviceType type, DeviceFactsPayload payload) =>
    DeviceReadiness(
        type: type,
        label: 'x',
        state: ReadinessState.ready,
        payload: payload);

void main() {
  test('camera facts land in the draft', () {
    final d = ProfileDraft();
    applyFactsToDraft(
        d,
        _r(
            EquipmentDeviceType.camera,
            const DeviceFactsPayload(
                camera: CameraGeometry(
                    sensorWidthPx: 6248,
                    sensorHeightPx: 4176,
                    pixelSizeUm: 3.76))));
    expect(d.camera.pixelSizeMicrons, 3.76);
  });

  test('unreported optics never clear a user-typed fallback value', () {
    final d = ProfileDraft();
    d.telescope.focalLengthMm = 530; // typed into the card's fallback field
    applyFactsToDraft(
        d,
        _r(
            EquipmentDeviceType.mount,
            const DeviceFactsPayload(
                optics: TelescopeOptics(), mountProps: MountProps())));
    expect(d.telescope.focalLengthMm, 530);
  });

  test('mount name seeds telescope name only when untyped', () {
    final d = ProfileDraft();
    applyFactsToDraft(
        d,
        _r(
            EquipmentDeviceType.mount,
            const DeviceFactsPayload(
                mountProps: MountProps(name: 'iOptron CEM70'))));
    expect(d.mount.name, 'iOptron CEM70');
    expect(d.telescope.name, 'iOptron CEM70');

    d.telescope.name = 'FSQ-106';
    applyFactsToDraft(
        d,
        _r(EquipmentDeviceType.mount,
            const DeviceFactsPayload(mountProps: MountProps(name: 'CEM70'))));
    expect(d.telescope.name, 'FSQ-106', reason: 'a name is identity, kept');
  });

  test('filter slots replace the draft list with inferred types', () {
    final d = ProfileDraft();
    d.filterWheel.filters.add(FilterDef()..name = 'stale');
    applyFactsToDraft(
        d,
        _r(
            EquipmentDeviceType.filterWheel,
            const DeviceFactsPayload(
                filterWheelSlots: FilterWheelSlots([
              FilterWheelSlot(name: 'L', focusOffset: 0),
              FilterWheelSlot(name: 'Hα 3nm', focusOffset: 25),
              FilterWheelSlot(name: 'R', focusOffset: 5),
            ]))));
    expect(d.filterWheel.filters.map((f) => f.name), ['L', 'Hα 3nm', 'R']);
    expect(d.filterWheel.filters[0].type, FilterType.luminance);
    expect(d.filterWheel.filters[1].type, FilterType.narrowband);
    expect(d.filterWheel.filters[1].focusOffsetSteps, 25);
    expect(d.filterWheel.filters[2].type, FilterType.broadband);
  });

  test('an empty wheel read leaves an existing draft filter list alone', () {
    final d = ProfileDraft();
    d.filterWheel.filters.add(FilterDef()..name = 'Hand-set');
    applyFactsToDraft(
        d,
        _r(EquipmentDeviceType.filterWheel,
            const DeviceFactsPayload(filterWheelSlots: FilterWheelSlots([]))));
    expect(d.filterWheel.filters.single.name, 'Hand-set');
  });

  test('rotator reverse mirrors the driver only when it CAN reverse', () {
    final d = ProfileDraft();
    applyFactsToDraft(
        d,
        _r(
            EquipmentDeviceType.rotator,
            const DeviceFactsPayload(
                rotatorProps:
                    RotatorProps(canReverse: true, reverse: true))));
    expect(d.rotator.reverse, isTrue);

    applyFactsToDraft(
        d,
        _r(
            EquipmentDeviceType.rotator,
            const DeviceFactsPayload(
                rotatorProps:
                    RotatorProps(canReverse: false, reverse: false))));
    expect(d.rotator.reverse, isTrue,
        reason: 'a non-reversing driver reports a meaningless false');
  });

  group('inferFilterType', () {
    test('classifies common filter names', () {
      expect(inferFilterType('L'), FilterType.luminance);
      expect(inferFilterType('Luminance'), FilterType.luminance);
      expect(inferFilterType('Clear'), FilterType.clear);
      expect(inferFilterType('OIII'), FilterType.narrowband);
      expect(inferFilterType('SII 6nm'), FilterType.narrowband);
      expect(inferFilterType('Hα'), FilterType.narrowband);
      expect(inferFilterType('R'), FilterType.broadband);
      expect(inferFilterType('Green'), FilterType.broadband);
    });
  });
}
