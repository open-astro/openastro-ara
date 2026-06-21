import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Settings navigation per §25.5.5. Phase 12h.1 ships the tree structure +
/// the selected-panel id state. Phase 12h.3 wires the §61 smart-search
/// ⌘K cross-cutting that uses this same tree to deep-link to any setting.

class SettingsPanelInfo {
  final String id;
  final String label;
  final String groupId;
  const SettingsPanelInfo({
    required this.id,
    required this.label,
    required this.groupId,
  });
}

class SettingsGroup {
  final String id;
  final String label;
  final List<SettingsPanelInfo> panels;
  const SettingsGroup({
    required this.id,
    required this.label,
    required this.panels,
  });
}

/// Canonical settings tree per playbook §25.5.5 + §61. Order matters —
/// matches NINA's hierarchy. Panels referenced by id from
/// `selectedSettingsPanelProvider`.
const List<SettingsGroup> settingsTree = <SettingsGroup>[
  SettingsGroup(
    id: 'equipment',
    label: 'Equipment',
    panels: <SettingsPanelInfo>[
      SettingsPanelInfo(id: 'eq.camera', label: 'Camera', groupId: 'equipment'),
      SettingsPanelInfo(id: 'eq.mount', label: 'Mount', groupId: 'equipment'),
      SettingsPanelInfo(id: 'eq.focuser', label: 'Focuser', groupId: 'equipment'),
      SettingsPanelInfo(id: 'eq.filterwheel', label: 'Filter Wheel', groupId: 'equipment'),
      SettingsPanelInfo(id: 'eq.rotator', label: 'Rotator', groupId: 'equipment'),
      SettingsPanelInfo(id: 'eq.guider', label: 'Guider (PHD2)', groupId: 'equipment'),
      SettingsPanelInfo(id: 'eq.flat', label: 'Flat Panel', groupId: 'equipment'),
      SettingsPanelInfo(id: 'eq.dome', label: 'Dome', groupId: 'equipment'),
      SettingsPanelInfo(id: 'eq.weather', label: 'Weather', groupId: 'equipment'),
      SettingsPanelInfo(id: 'eq.safety', label: 'Safety Monitor', groupId: 'equipment'),
      SettingsPanelInfo(id: 'eq.switch', label: 'Switch', groupId: 'equipment'),
    ],
  ),
  SettingsGroup(
    id: 'imaging',
    label: 'Imaging',
    panels: <SettingsPanelInfo>[
      SettingsPanelInfo(id: 'img.defaults', label: 'Defaults', groupId: 'imaging'),
      SettingsPanelInfo(id: 'img.optics', label: 'Optics (FOV)', groupId: 'imaging'),
      SettingsPanelInfo(id: 'img.autofocus', label: 'Autofocus', groupId: 'imaging'),
      SettingsPanelInfo(id: 'img.platesolve', label: 'Plate Solving', groupId: 'imaging'),
    ],
  ),
  SettingsGroup(
    id: 'session',
    label: 'Session',
    panels: <SettingsPanelInfo>[
      SettingsPanelInfo(id: 'session.storage', label: 'Storage', groupId: 'session'),
      SettingsPanelInfo(id: 'session.filenames', label: 'File saving + naming', groupId: 'session'),
      SettingsPanelInfo(id: 'session.notifications', label: 'Notifications', groupId: 'session'),
    ],
  ),
  SettingsGroup(
    id: 'safety',
    label: 'Safety',
    panels: <SettingsPanelInfo>[
      SettingsPanelInfo(id: 'safety.policies', label: 'Policies', groupId: 'safety'),
      SettingsPanelInfo(id: 'safety.diagnostics', label: 'Diagnostics mode', groupId: 'safety'),
      SettingsPanelInfo(id: 'safety.site', label: 'Site preferences', groupId: 'safety'),
    ],
  ),
  SettingsGroup(
    id: 'skyatlas',
    label: 'Sky Atlas',
    panels: <SettingsPanelInfo>[
      SettingsPanelInfo(id: 'sky.data', label: 'Data Manager', groupId: 'skyatlas'),
    ],
  ),
  SettingsGroup(
    id: 'profile',
    label: 'Profile',
    panels: <SettingsPanelInfo>[
      SettingsPanelInfo(id: 'profile.active', label: 'Active profile', groupId: 'profile'),
      SettingsPanelInfo(id: 'profile.wizard', label: 'Run wizard again', groupId: 'profile'),
    ],
  ),
  SettingsGroup(
    id: 'system',
    label: 'System',
    panels: <SettingsPanelInfo>[
      SettingsPanelInfo(id: 'app.monitoring', label: 'Monitoring', groupId: 'system'),
      SettingsPanelInfo(id: 'app.changelog', label: 'About', groupId: 'system'),
    ],
  ),
];

class SelectedSettingsPanelNotifier extends Notifier<String> {
  @override
  String build() => 'img.defaults';
  void select(String panelId) => state = panelId;
}

final selectedSettingsPanelProvider =
    NotifierProvider<SelectedSettingsPanelNotifier, String>(
        SelectedSettingsPanelNotifier.new);

class HighlightedSettingNotifier extends Notifier<String?> {
  @override
  String? build() => null;

  void highlight(String settingId) {
    state = settingId;
    // Auto-clear after 2 seconds per §61.2.
    Future.delayed(const Duration(seconds: 2), () {
      if (state == settingId) state = null;
    });
  }
}

final highlightedSettingProvider =
    NotifierProvider<HighlightedSettingNotifier, String?>(
        HighlightedSettingNotifier.new);


SettingsPanelInfo? findPanelInfo(String id) {
  for (final g in settingsTree) {
    for (final p in g.panels) {
      if (p.id == id) return p;
    }
  }
  return null;
}
