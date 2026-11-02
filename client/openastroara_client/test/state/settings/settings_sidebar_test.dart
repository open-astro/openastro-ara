import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/settings_nav.dart';

void main() {
  test('the Options sidebar leaves out Equipment: the top-bar chips open those panels', () {
    expect(settingsSidebarGroups.map((g) => g.id), isNot(contains('equipment')));
    expect(settingsSidebarGroups.length, settingsTree.length - 1);
  });

  test('the equipment panels still resolve, so the chips and search can open them', () {
    for (final id in const ['eq.camera', 'eq.mount', 'eq.guider', 'eq.filterwheel', 'eq.switch']) {
      expect(findPanelInfo(id), isNotNull, reason: id);
    }
  });
}
