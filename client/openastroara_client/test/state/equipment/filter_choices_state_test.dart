import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/state/equipment/filter_choices_state.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/settings/filter_wheel_labels_state.dart';

class _Wheel extends FilterWheelNotifier {
  static FilterWheelStatus? status;
  @override
  Future<FilterWheelStatus?> build() async => status;
}

class _Labels extends FilterWheelLabelsNotifier {
  @override
  FilterWheelLabels build() =>
      FilterWheelLabels(labels: const ['L', 'R', 'G', 'B', 'Hα', '', '']);
}

FilterWheelStatus _wheel(
  List<String> names, {
  EquipmentConnectionState state = EquipmentConnectionState.connected,
}) => FilterWheelStatus(
  deviceId: 'ZWO_EFW_0',
  name: 'ZWO EFW',
  connectionState: state,
  runtimeState: 'idle',
  currentSlot: 0,
  slots: [
    for (var i = 0; i < names.length; i++)
      FilterSlot(position: i, name: names[i], focusOffset: 0),
  ],
);

Future<List<FilterChoice>> _choices() async {
  final c = ProviderContainer(
    overrides: [
      filterWheelProvider.overrideWith(_Wheel.new),
      filterWheelLabelsProvider.overrideWith(_Labels.new),
    ],
  );
  addTearDown(c.dispose);
  await c.read(filterWheelProvider.future);
  return c.read(filterChoicesProvider);
}

void main() {
  test(
    "a connected wheel's Alpaca names win over the profile labels",
    () async {
      _Wheel.status = _wheel([
        'Luminance',
        'Red',
        'Green',
        'Blue',
        'Ha',
        'OIII',
        'SII',
      ]);
      final choices = await _choices();
      expect(choices.map((c) => c.name), [
        'Luminance',
        'Red',
        'Green',
        'Blue',
        'Ha',
        'OIII',
        'SII',
      ]);
      expect(choices[4].position, 4);
    },
  );

  test(
    'custom and default ("Filter N") wheel names pass through verbatim',
    () async {
      _Wheel.status = _wheel(['Antlia Ha 3nm', 'Filter 2', 'Filter 3']);
      expect((await _choices()).map((c) => c.name), [
        'Antlia Ha 3nm',
        'Filter 2',
        'Filter 3',
      ]);
    },
  );

  test('an unnamed wheel slot takes the label at its position; dupes keep '
      'the first', () async {
    _Wheel.status = _wheel(['L', '', 'L']);
    final choices = await _choices();
    expect(choices.map((c) => (c.name, c.position)), [('L', 0), ('R', 1)]);
  });

  test('no connected wheel → the profile labels', () async {
    _Wheel.status = _wheel([
      'Luminance',
    ], state: EquipmentConnectionState.disconnected);
    expect((await _choices()).map((c) => c.name), ['L', 'R', 'G', 'B', 'Hα']);
    _Wheel.status = null;
    expect((await _choices()).map((c) => c.name), ['L', 'R', 'G', 'B', 'Hα']);
  });
}
