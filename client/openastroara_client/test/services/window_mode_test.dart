import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/window_mode.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  const channel = MethodChannel('openastroara/window');

  tearDown(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, null);
  });

  test('forwards the mode name and is idempotent per mode', () async {
    final calls = <String>[];
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async {
      calls.add(call.method);
      return null;
    });
    final svc = WindowModeService();
    await svc.set(WindowMode.workstation);
    await svc.set(WindowMode.workstation); // duplicate — dropped
    await svc.set(WindowMode.launchpad);
    await svc.set(WindowMode.launchpad); // duplicate — dropped
    await svc.set(WindowMode.workstation);
    expect(calls, ['workstation', 'launchpad', 'workstation']);
  });

  test('a missing native handler is swallowed and stops further calls', () async {
    // No mock handler registered → MissingPluginException inside — must not
    // escape; the target is unsupported for the process, so later requests
    // don't retry-spam the channel.
    final svc = WindowModeService();
    await svc.set(WindowMode.workstation); // completes without throwing
    await svc.set(WindowMode.launchpad); // still silent
  });

  test('a transient native error rolls the mode back so a retry re-applies',
      () async {
    var fail = true;
    final calls = <String>[];
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async {
      calls.add(call.method);
      if (fail) throw PlatformException(code: 'boom');
      return null;
    });
    final svc = WindowModeService();
    await svc.set(WindowMode.launchpad); // fails — swallowed, mode rolled back
    fail = false;
    await svc.set(WindowMode.launchpad); // NOT deduped: the retry re-applies
    expect(calls, ['launchpad', 'launchpad']);
  });
}
