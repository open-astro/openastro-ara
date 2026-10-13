import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/library/mosaic.dart';

void main() {
  test('Mosaic parses the daemon wire shape and degrades on garbage', () {
    final m = Mosaic.fromJson({
      'id': 'abc',
      'name': 'Veil',
      'center_ra_degrees': 312.75,
      'center_dec_degrees': 30.5,
      'panel_count_x': 3,
      'panel_count_y': 2,
      'overlap_percent': 15,
      'position_angle_degrees': 20,
      'total_panels': 6,
      'created_utc': '2026-10-12T22:00:00+00:00',
    });
    expect(m.id, 'abc');
    expect(m.gridLabel, '3×2');
    expect(m.totalPanels, 6);
    expect(m.createdUtc, DateTime.utc(2026, 10, 12, 22));
    final bad = Mosaic.fromJson({'id': 'x', 'name': 7, 'panel_count_x': 'two'});
    expect(bad.name, '');
    expect(bad.panelCountX, 0);
  });

  test('panel status and progress parse', () {
    final p = MosaicPanel.fromJson({
      'panel_index': 2,
      'panel_x': 2,
      'panel_y': 0,
      'status': 'in_progress',
      'frames_captured': 4,
      'crosses_ra_wrap': true,
    });
    expect(p.status, MosaicPanelStatus.inProgress);
    expect(p.framesCaptured, 4);
    expect(p.crossesRaWrap, isTrue);
    expect(MosaicPanelStatus.parse('done'), MosaicPanelStatus.done);
    expect(MosaicPanelStatus.parse('whatever'), MosaicPanelStatus.pending);

    final pr = MosaicProgress.fromJson({
      'completed_panels': 2,
      'total_panels': 6,
      'current_panel_index': 2,
      'frames_per_panel_target': 20,
      'frames_captured_total': 44,
    });
    expect(pr.completedPanels, 2);
    expect(pr.currentPanelIndex, 2);
    expect(pr.complete, isFalse);
    expect(
      const MosaicProgress(completedPanels: 6, totalPanels: 6).complete,
      isTrue,
    );
  });
}
