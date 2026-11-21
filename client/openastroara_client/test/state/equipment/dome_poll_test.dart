import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/dome_status.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/state/equipment/dome_state.dart';
import 'package:openastroara/state/equipment/equipment_device_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// A dome whose runtime state the test sets, counting reads.
class _DomeApi implements EquipmentDeviceClient<DomeStatus> {
  String runtimeState = 'shutter_moving';
  int reads = 0;

  @override
  Future<DomeStatus?> getStatus() async {
    reads++;
    return DomeStatus.fromJson({
      'device_id': 'd',
      'name': 'Dome',
      'state': 'connected',
      'runtime': {'state': runtimeState},
    });
  }

  @override
  Future<void> forget() async {}
  @override
  Future<void> connect(DiscoveredDevice device) async {}
  @override
  Future<void> disconnect() async {}
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async {}
  @override
  void close() {}
}

void main() {
  test(
    'a dome is read every 1.5 s through a long move, so its end is seen at once',
    () {
      // A 90 s shutter outlasts the ~60 s settle cap other devices get; on
      // the 15 s liveness poll its end showed late and the Dome window
      // learned a shutter time up to 15 s too long (#1662).
      fakeAsync((async) {
        final api = _DomeApi();
        final container = ProviderContainer(
          overrides: [
            domeApiProvider.overrideWithValue(api),
            wsEventsProvider.overrideWith(
              (ref) => const Stream<WsEvent>.empty(),
            ),
          ],
        );
        container.listen(domeProvider, (_, _) {});
        async.flushMicrotasks();
        async.elapse(const Duration(seconds: 75));
        final before = api.reads;
        async.elapse(EquipmentDeviceNotifier.settleInterval * 4);
        expect(api.reads - before, 4, reason: 'still the fast poll at 75 s');
        container.dispose();
      });
    },
  );

  test('a dome stuck moving drops to the liveness poll after 5 min', () {
    fakeAsync((async) {
      final api = _DomeApi()..runtimeState = 'slewing';
      final container = ProviderContainer(
        overrides: [
          domeApiProvider.overrideWithValue(api),
          wsEventsProvider.overrideWith((ref) => const Stream<WsEvent>.empty()),
        ],
      );
      container.listen(domeProvider, (_, _) {});
      async.flushMicrotasks();
      async.elapse(const Duration(minutes: 5, seconds: 3));
      final before = api.reads;
      async.elapse(EquipmentDeviceNotifier.livePollInterval * 2);
      expect(api.reads - before, 2);
      container.dispose();
    });
  });
}
