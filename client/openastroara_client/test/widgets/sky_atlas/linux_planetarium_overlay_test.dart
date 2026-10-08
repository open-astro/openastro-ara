import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/widgets/sky_atlas/linux_planetarium_overlay.dart';

const _channel = MethodChannel('org.openastro.openastroara/planetarium');

void main() {
  testWidgets('a window that grew while Planning was hidden re-pushes the bounds after showing', (t) async {
    final calls = <String>[];
    t.binding.defaultBinaryMessenger.setMockMethodCallHandler(_channel, (call) async {
      final a = call.arguments;
      calls.add(call.method == 'setBounds'
          ? 'setBounds ${(a as Map)['width']}x${a['height']}'
          : call.method == 'setVisible'
              ? 'setVisible ${(a as Map)['visible']}'
              : call.method);
      return null;
    });
    addTearDown(() => t.binding.defaultBinaryMessenger.setMockMethodCallHandler(_channel, null));

    final size = ValueNotifier(const Size(400, 300));
    final container = ProviderContainer();
    addTearDown(container.dispose);
    await t.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: MaterialApp(
        navigatorObservers: [planetariumRouteObserver],
        home: Align(
          alignment: Alignment.topLeft,
          child: ValueListenableBuilder<Size>(
            valueListenable: size,
            builder: (context, s, _) =>
                SizedBox.fromSize(size: s, child: const LinuxPlanetariumOverlay(url: 'http://127.0.0.1:1/')),
          ),
        ),
      ),
    ));
    await t.pump();
    expect(calls, containsAllInOrder(['setBounds 400.0x300.0', 'setVisible true']));

    // Another tab, then the window grows while Planning is hidden.
    container.read(selectedTabIndexProvider.notifier).select(1);
    await t.pump();
    await t.pump();
    size.value = const Size(800, 600);
    await t.pump();
    await t.pump();
    calls.clear();

    // Back to Planning: shown first, then the current bounds, even though the
    // hidden resize already sent them once.
    container.read(selectedTabIndexProvider.notifier).select(0);
    await t.pump();
    await t.pump();
    expect(calls, ['setVisible true', 'setBounds 800.0x600.0']);
  });
}
