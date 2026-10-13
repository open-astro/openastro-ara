import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/library/mosaic.dart';
import 'package:openastroara/services/mosaic_api.dart';
import 'package:openastroara/state/library/mosaics_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/widgets/library/mosaics_section.dart';

class _FakeMosaics implements MosaicClient {
  final List<MosaicProject> projects;
  _FakeMosaics(this.projects);
  @override
  Future<List<MosaicProject>> fetchProjects() async => projects;
  @override
  Future<Mosaic> create({
    required String name,
    required double centerRaDeg,
    required double centerDecDeg,
    required int cols,
    required int rows,
    required double overlapPercent,
    required double positionAngleDeg,
    required List<({double raDeg, double decDeg})> panels,
    required int framesPerPanel,
  }) async => Mosaic(id: 'new', name: name);
  @override
  Future<void> delete(String id) async {}
  @override
  void close() {}
}

MosaicProject _veil() => MosaicProject(
  mosaic: const Mosaic(
    id: 'veil',
    name: 'Veil nebula',
    panelCountX: 3,
    panelCountY: 2,
    totalPanels: 6,
  ),
  progress: const MosaicProgress(
    completedPanels: 2,
    totalPanels: 6,
    currentPanelIndex: 2,
    framesPerPanelTarget: 20,
    framesCapturedTotal: 44,
  ),
  panels: [
    for (var i = 0; i < 6; i++)
      MosaicPanel(
        panelIndex: i,
        panelX: i % 3,
        panelY: i ~/ 3,
        status: i < 2
            ? MosaicPanelStatus.done
            : i == 2
            ? MosaicPanelStatus.inProgress
            : MosaicPanelStatus.pending,
        framesCaptured: i < 2 ? 20 : (i == 2 ? 4 : 0),
      ),
  ],
);

Future<void> _pump(WidgetTester tester, List<MosaicProject> projects) async {
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        activeServerProvider.overrideWithValue(
          const AraServer(hostname: 'ara.test', port: 5555),
        ),
        mosaicApiFactoryProvider.overrideWithValue(
          (_) => _FakeMosaics(projects),
        ),
      ],
      child: const MaterialApp(home: Scaffold(body: MosaicsSection())),
    ),
  );
  await tester.pumpAndSettle();
}

void main() {
  testWidgets(
    '#1146: a project row says how many panels are done and draws a dot per panel',
    (tester) async {
      await _pump(tester, [_veil()]);
      expect(find.text('Mosaics'), findsOneWidget);
      expect(find.text('Veil nebula'), findsOneWidget);
      expect(
        find.textContaining(
          '3×2 · 2/6 panels done · 44 light frames · 20 per panel',
        ),
        findsOneWidget,
      );
      final tip = tester.widget<Tooltip>(find.byType(Tooltip));
      expect(tip.message, contains('Panel 1: done'));
      expect(tip.message, contains('Panel 3: 4 so far'));
      expect(tip.message, contains('Panel 6: owed'));
    },
  );

  testWidgets('#1146: no projects → the section takes no space', (
    tester,
  ) async {
    await _pump(tester, const []);
    expect(find.text('Mosaics'), findsNothing);
  });
}
