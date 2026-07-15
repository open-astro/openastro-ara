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

  test('a missing native handler is swallowed (mobile / tests)', () async {
    // No mock handler registered → MissingPluginException inside — must not
    // escape, and the mode is still recorded so the router isn't retry-spammed.
    final svc = WindowModeService();
    await svc.set(WindowMode.workstation); // completes without throwing
  });

  test('a native error is swallowed too', () async {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async {
      throw PlatformException(code: 'boom');
    });
    final svc = WindowModeService();
    await svc.set(WindowMode.launchpad); // completes without throwing
  });
}
