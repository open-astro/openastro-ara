import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/sky_model_calibration.dart';
import 'package:openastroara/services/planning_api.dart';
import 'package:openastroara/state/planning/sky_model_calibration_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _Api extends PlanningApi {
  _Api({this.stored, this.fail = false}) : super(const AraServer(hostname: 'h', port: 1));
  SkyModelCalibration? stored;
  final bool fail;
  final calls = <String>[];
  @override
  Future<SkyModelCalibration?> read() async {
    calls.add('read');
    if (fail) throw StateError('down');
    return stored;
  }
  @override
  Future<SkyModelCalibration?> recalibrate() async {
    calls.add('recalibrate');
    return stored = SkyModelCalibration(fittedUtc: DateTime.utc(2026), framesConsidered: 9, filters: const []);
  }
  @override
  Future<void> clear() async {
    calls.add('clear');
    stored = null;
  }
  @override
  void close() {}
}

ProviderContainer _make(_Api api, {bool server = true}) {
  final c = ProviderContainer(overrides: [
    activeServerProvider.overrideWithValue(server ? const AraServer(hostname: 'h', port: 1) : null),
    planningApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  test('#1140 P2 reads the record for the active server; none → null without a call', () async {
    final api = _Api(stored: SkyModelCalibration(fittedUtc: DateTime.utc(2026), framesConsidered: 1, filters: const []));
    expect((await _make(api).read(skyModelCalibrationProvider.future))!.framesConsidered, 1);
    expect(api.calls, ['read']);
    final idle = _Api();
    expect(await _make(idle, server: false).read(skyModelCalibrationProvider.future), isNull);
    expect(idle.calls, isEmpty);
  });

  test('#1140 P2 an unreachable rig reads as not fitted, never an error', () async {
    final c = _make(_Api(fail: true));
    expect(await c.read(skyModelCalibrationProvider.future), isNull);
    expect(c.read(skyModelCalibrationProvider).hasError, isFalse);
  });

  test('#1140 P2 recalibrate stores the daemon\'s answer; clear empties it', () async {
    final api = _Api();
    final c = _make(api);
    expect(await c.read(skyModelCalibrationProvider.future), isNull);
    final record = await c.read(skyModelCalibrationProvider.notifier).recalibrate();
    expect(record!.framesConsidered, 9);
    expect(c.read(skyModelCalibrationProvider).requireValue!.framesConsidered, 9);
    await c.read(skyModelCalibrationProvider.notifier).clear();
    expect(c.read(skyModelCalibrationProvider).requireValue, isNull);
    expect(api.calls, ['read', 'recalibrate', 'clear']);
  });
}
