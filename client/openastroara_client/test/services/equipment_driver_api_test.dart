import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _Guider extends GuiderStatusNotifier {
  @override
  Future<GuiderStatus?> build() async => _status('disconnected');
  void set(String s) => state = AsyncData(_status(s));
  static GuiderStatus _status(String s) =>
      GuiderStatus.fromJson({'device_id': 'g', 'name': 'PHD2', 'state': s});
}

void main() {
  test('the guide camera is asked again when the guider connects (it only knows its camera then)', () async {
    final guider = _Guider();
    final c = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWith((ref) => null),
        guiderStatusProvider.overrideWith(() => guider),
      ],
    );
    addTearDown(c.dispose);
    final seen = <AsyncValue<DeviceDriver?>>[];
    c.listen(
      deviceDriverProvider('guidecamera'),
      (_, next) => seen.add(next),
      fireImmediately: true,
    );
    await c.read(guiderStatusProvider.future);
    await c.read(deviceDriverProvider('guidecamera').future);
    seen.clear();
    guider.set('connected');
    await c.read(deviceDriverProvider('guidecamera').future);
    expect(seen.any((v) => v.isLoading), isTrue, reason: 'asked again');
  });
}
