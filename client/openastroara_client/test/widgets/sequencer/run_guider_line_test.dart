import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/services/guider_api.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/widgets/sequencer/run_dashboard_band.dart';

class _Stub extends GuiderStatusNotifier {
  final GuiderStatus? status;
  int refreshes = 0;
  _Stub(this.status);
  @override
  Future<GuiderStatus?> build() async => status;
  @override
  Future<void> refresh([GuiderClient? client]) async => refreshes++;
}

Future<_Stub> _pump(WidgetTester t, GuiderStatus? status) async {
  final stub = _Stub(status);
  await t.pumpWidget(
    ProviderScope(
      overrides: [guiderStatusProvider.overrideWith(() => stub)],
      child: const MaterialApp(home: Scaffold(body: RunGuiderLine())),
    ),
  );
  await t.pump();
  return stub;
}

void main() {
  testWidgets('the run shows the guider calibrating with its leg', (t) async {
    final stub = await _pump(
      t,
      GuiderStatus.fromJson(<String, dynamic>{
        'state': 'connected',
        'runtime': <String, dynamic>{
          'state': 'calibrating',
          'calibration_direction': 'North',
          'calibration_step': 7,
          'calibration_distance_px': 11.9,
        },
      }),
    );
    expect(
      find.text('Guider · Calibrating · North step 7 · 11.9 px'),
      findsOneWidget,
    );
    // It polls while the run band is up.
    await t.pump(RunGuiderLine.pollInterval);
    expect(stub.refreshes, greaterThanOrEqualTo(1));
    await t.pumpWidget(const SizedBox());
  });

  testWidgets('nothing shows without a connected guider', (t) async {
    await _pump(
      t,
      GuiderStatus.fromJson(<String, dynamic>{'state': 'disconnected'}),
    );
    expect(find.byKey(const Key('run-guider-line')), findsNothing);
    await t.pumpWidget(const SizedBox());
  });
}
