import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/switch_device.dart';
import 'package:openastroara/widgets/equipment/switch_port_layout.dart';

SwitchPort _port(
  int id,
  String name, {
  double value = 0,
  double min = 0,
  double max = 1,
  bool canWrite = true,
  double step = 0,
}) =>
    SwitchPort(
      id: id,
      name: name,
      value: value,
      min: min,
      max: max,
      canWrite: canWrite,
      step: step,
    );

void main() {
  group('classifySwitchPort', () {
    test('a writable two-state port is a toggle', () {
      expect(classifySwitchPort(_port(0, 'DC2')), SwitchPortKind.toggle);
    });

    test('a read-only two-state port is an indicator', () {
      expect(
        classifySwitchPort(_port(0, 'AHT20 Sensor', canWrite: false)),
        SwitchPortKind.indicator,
      );
    });

    test('read-only telemetry is a reading', () {
      expect(
        classifySwitchPort(
            _port(0, 'Input Voltage', value: 12.6, max: 30, canWrite: false)),
        SwitchPortKind.reading,
      );
    });

    test('a wide writable range is a level', () {
      expect(
        classifySwitchPort(_port(0, 'DEW6', max: 100)),
        SwitchPortKind.level,
      );
    });

    test('a narrow whole-step writable range is a mode', () {
      expect(
        classifySwitchPort(_port(0, 'DEW6 Mode', max: 2)),
        SwitchPortKind.mode,
      );
    });

    test('a degenerate writable port is stuck, not a slider or a reading', () {
      // Slider asserts min < max; a malformed device must not crash the panel,
      // and calling a writable port a "reading" would claim it is read-only.
      expect(
        classifySwitchPort(_port(0, 'Broken', min: 5, max: 5)),
        SwitchPortKind.stuck,
      );
    });

    test('a degenerate read-only port is still a reading', () {
      expect(
        classifySwitchPort(_port(0, 'Fixed', min: 5, max: 5, canWrite: false)),
        SwitchPortKind.reading,
      );
    });

    test('a fractional narrow range is a level, not a mode', () {
      expect(
        classifySwitchPort(_port(0, 'Fine', max: 1.5)),
        SwitchPortKind.level,
      );
    });
  });

  group('switchPortUnit', () {
    test('infers units from the port name', () {
      expect(switchPortUnit(_port(0, 'Input Voltage', canWrite: false)), 'V');
      expect(switchPortUnit(_port(0, 'Output Current', canWrite: false)), 'A');
      expect(switchPortUnit(_port(0, 'Output Power', canWrite: false)), 'W');
      expect(
          switchPortUnit(_port(0, 'Ambient Humidity', canWrite: false)), '%');
      expect(
          switchPortUnit(_port(0, 'Lens Temperature', canWrite: false)), '°C');
      expect(switchPortUnit(_port(0, 'Dew Point', canWrite: false)), '°C');
    });

    test('a writable 0..100 port reads as a duty cycle', () {
      expect(switchPortUnit(_port(0, 'DEW6', max: 100)), '%');
    });

    test('an unrecognised name gets no unit rather than a wrong one', () {
      expect(switchPortUnit(_port(0, 'Aux', canWrite: false, max: 7)), '');
    });

    test('matches whole words — "Lamp" is not amps', () {
      expect(switchPortUnit(_port(0, 'Flat Lamp', max: 100)), '%');
      expect(switchPortUnit(_port(0, 'Lamp', canWrite: false, max: 7)), '');
    });

    test('a writable duty cycle beats a units word in its name', () {
      expect(switchPortUnit(_port(0, 'Dew Power', max: 100)), '%');
    });
  });

  group('formatSwitchValue', () {
    test('keeps integers integral', () {
      expect(formatSwitchValue(100), '100');
      expect(formatSwitchValue(0), '0');
    });

    test('trims trailing zeros rather than padding to two decimals', () {
      expect(formatSwitchValue(12.6), '12.6');
      expect(formatSwitchValue(0.11), '0.11');
      expect(formatSwitchValue(23.59), '23.59');
    });
  });

  group('groupSwitchPorts', () {
    test('gathers a shared prefix and leaves singletons in the fallback', () {
      final ports = [
        _port(0, 'USB A'),
        _port(1, 'USB B'),
        _port(2, 'DC1', canWrite: false),
        _port(3, 'DC2'),
        _port(4, 'Aux'),
      ];
      final groups = groupSwitchPorts(ports, fallbackTitle: 'Switches');
      expect(groups.map((g) => g.title), ['USB', 'DC', 'Switches']);
      expect(groups[0].ports.map((p) => p.name), ['USB A', 'USB B']);
      // The always-on rail groups with its switchable siblings.
      expect(groups[1].ports.map((p) => p.name), ['DC1', 'DC2']);
      expect(groups[2].ports.single.name, 'Aux');
    });

    test('groups appear in device order, not alphabetical order', () {
      final groups = groupSwitchPorts(
        [_port(0, 'ZZ 1'), _port(1, 'ZZ 2'), _port(2, 'AA 1'), _port(3, 'AA 2')],
        fallbackTitle: 'Switches',
      );
      expect(groups.map((g) => g.title), ['ZZ', 'AA']);
    });

    test('read-only bits sharing a prefix are not a control group', () {
      // "USB2 Status"/"USB3 Status" are presence bits; as toggle rows they
      // would each read "Always on".
      final groups = groupSwitchPorts(
        [
          _port(0, 'USB2 Status', canWrite: false),
          _port(1, 'USB3 Status', canWrite: false),
        ],
        fallbackTitle: 'Switches',
      );
      expect(groups.map((g) => g.title), ['Switches']);
      expect(groups.single.ports, hasLength(2));
    });

    test('a status bit does not join its rail prefix just by sharing it', () {
      // "USB1 Status" carries a descriptor its switchable siblings lack, so it
      // is a bit ABOUT the port, not the port — as a toggle row it would read
      // "Always on".
      final groups = groupSwitchPorts(
        [
          _port(0, 'USB1'),
          _port(1, 'USB2'),
          _port(2, 'USB1 Status', canWrite: false),
        ],
        fallbackTitle: 'Switches',
      );
      expect(groups.map((g) => g.title), ['USB', 'Switches']);
      expect(groups[0].ports.map((p) => p.name), ['USB1', 'USB2']);
      expect(groups[1].ports.single.name, 'USB1 Status');
    });

    test('a read-only rail still joins a prefix that has writable siblings', () {
      final groups = groupSwitchPorts(
        [_port(0, 'DC1', canWrite: false), _port(1, 'DC2')],
        fallbackTitle: 'Switches',
      );
      expect(groups.map((g) => g.title), ['DC']);
      expect(groups.single.ports, hasLength(2));
    });

    test('no fallback group is emitted when every port is grouped', () {
      final groups = groupSwitchPorts(
        [_port(0, 'USB A'), _port(1, 'USB B')],
        fallbackTitle: 'Switches',
      );
      expect(groups.map((g) => g.title), ['USB']);
    });
  });

  group('findModePort', () {
    test('pairs a level with its "<name> Mode" companion', () {
      final level = _port(11, 'DEW6', max: 100);
      final mode = _port(13, 'DEW6 Mode', max: 2);
      final all = [level, _port(12, 'DEW7', max: 100), mode];
      expect(findModePort(level, all)?.id, 13);
    });

    test('returns null when the level has no companion', () {
      final level = _port(11, 'DEW6', max: 100);
      expect(findModePort(level, [level]), isNull);
    });

    test('ignores a read-only companion — it is telemetry, not a control', () {
      final level = _port(11, 'DEW6', max: 100);
      final ro = _port(13, 'DEW6 Mode', max: 2, canWrite: false);
      expect(findModePort(level, [level, ro]), isNull);
    });

    test('ignores a companion too wide to be a mode', () {
      final level = _port(11, 'DEW6', max: 100);
      final wide = _port(13, 'DEW6 Mode', max: 5);
      expect(findModePort(level, [level, wide]), isNull);
    });
  });

  group('quantiseSwitchWrite', () {
    test('rounds to a whole step on a whole range', () {
      final wide = _port(0, 'Big', max: 65535);
      expect(quantiseSwitchWrite(wide, 41234.87), 41235);
    });

    test('leaves a genuinely fractional range alone', () {
      final fine = _port(0, 'Fine', max: 1.5);
      expect(quantiseSwitchWrite(fine, 0.75), 0.75);
    });

    test('rounds on the port own grid when min is not whole', () {
      // A 0.5..3.5 port has a whole SPAN but its steps are 0.5/1.5/2.5/3.5 —
      // snapping to absolute integers would write a value it never offers.
      final offset = _port(0, 'Odd', min: 0.5, max: 3.5);
      expect(quantiseSwitchWrite(offset, 1.9), 1.5);
      expect(quantiseSwitchWrite(offset, 2.2), 2.5);
    });

    test("snaps to the device's step when it reports one (#1680)", () {
      expect(quantiseSwitchWrite(_port(0, 'Light Box', max: 100, step: 10), 37), 40);
      // Exact: 0.1 * 85 is 8.500000000000002 in floating point.
      expect(quantiseSwitchWrite(_port(0, 'Temperature', max: 15, step: 0.1), 8.47), 8.5);
      expect(quantiseSwitchWrite(_port(0, 'Odd', min: 0.5, max: 3.5, step: 0.5), 1.8), 2);
    });

    test('a step finer than 1000 stops over the range is treated as unknown', () {
      expect(quantiseSwitchWrite(_port(0, 'Big', max: 65535, step: 0.01), 41234.87), 41235);
    });

    test('never escapes the port bounds', () {
      final p = _port(0, 'DEW6', max: 100);
      expect(quantiseSwitchWrite(p, 100.4), 100);
      expect(quantiseSwitchWrite(p, -0.4), 0);
    });
  });

  group('switchSliderDivisions', () {
    test("one stop per device step when the step is known (#1680)", () {
      expect(switchSliderDivisions(_port(0, 'Light Box', max: 100, step: 10)), 10);
      expect(switchSliderDivisions(_port(0, 'Temperature', max: 15, step: 0.1)), 150);
    });

    test('without a step: one stop per whole unit on a whole range, else continuous', () {
      expect(switchSliderDivisions(_port(0, 'DEW6', max: 100)), 100);
      expect(switchSliderDivisions(_port(0, 'Fine', max: 1.5)), isNull);
      expect(switchSliderDivisions(_port(0, 'Big', max: 65535)), isNull, reason: 'capped at 1000');
    });
  });

  group('isDewModePort / isAutoRegulating', () {
    test('recognises the 0..2 dew convention only', () {
      expect(isDewModePort(_port(0, 'DEW6 Mode', max: 2)), isTrue);
      expect(isDewModePort(_port(0, 'Fan Speed', max: 3)), isFalse);
      expect(isDewModePort(_port(0, 'Something', max: 2)), isFalse);
    });

    test('Auto is mode 0 on a dew channel', () {
      expect(isAutoRegulating(_port(0, 'DEW6 Mode', value: 0, max: 2)), isTrue);
      expect(isAutoRegulating(_port(0, 'DEW6 Mode', value: 1, max: 2)), isFalse);
      expect(isAutoRegulating(null), isFalse);
      // A 0..3 fan speed at 0 is "off", not "self-regulating".
      expect(isAutoRegulating(_port(0, 'Fan Speed', value: 0, max: 3)), isFalse);
    });
  });

  group('switchModeLabels', () {
    test('names the 0..2 dew convention', () {
      expect(switchModeLabels(_port(0, 'DEW6 Mode', max: 2)), kDewModeLabels);
    });

    test('falls back to raw numbers for an unknown range', () {
      expect(switchModeLabels(_port(0, 'Speed', max: 3)), ['0', '1', '2', '3']);
    });
  });
}
