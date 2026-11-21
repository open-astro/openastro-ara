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

  test('setTitle rides the channel with the title as argument', () async {
    final calls = <(String, Object?)>[];
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async {
      calls.add((call.method, call.arguments));
      return null;
    });
    final svc = WindowModeService();
    await svc.setTitle('OpenAstro Ara 0.0.1a');
    expect(calls, [('title', 'OpenAstro Ara 0.0.1a')]);
  });

  test('setTitle without a native handler is silent, and marks the channel '
      'unsupported for mode calls too', () async {
    // No mock handler → MissingPluginException inside — must not escape, and
    // the shared _unsupported latch means later mode calls don't retry-spam.
    final svc = WindowModeService();
    await svc.setTitle('OpenAstro Ara 0.0.1a'); // completes without throwing
    await svc.set(WindowMode.workstation); // still silent
  });

  test('a transient native error on setTitle is swallowed and does not poison '
      'later calls', () async {
    var fail = true;
    final calls = <String>[];
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(channel, (call) async {
      calls.add(call.method);
      if (fail) throw PlatformException(code: 'boom');
      return null;
    });
    final svc = WindowModeService();
    await svc.setTitle('OpenAstro Ara 0.0.1a'); // fails — swallowed
    fail = false;
    await svc.setTitle('OpenAstro Ara 0.0.1a'); // retries cleanly
    await svc.set(WindowMode.workstation); // channel still healthy
    expect(calls, ['title', 'title', 'workstation']);
  });
}
