import '../../help/registry.dart';
import '../../settings/registry.dart';
import 'settings_nav.dart';

/// §61 smart-search registry. Indexes every settable panel from
/// `settingsTree`, individual settings from `settingsRegistry`, and — §68.4 —
/// the §69 help entries from `helpRegistry` (informational hits that open the
/// help sheet rather than jumping to a panel). Users can find things by intent
/// ("dither", "park", "equipment hub down") not just by exact label.

class SettingsSearchEntry {
  final String? panelId;
  final String? settingId;

  /// §68.4 — set for an informational help hit: activating it opens the §69
  /// help sheet instead of navigating to a settings panel.
  final String? helpKey;

  /// §61.10 — set for a "Go to `<tab>`" navigation hit: activating it switches
  /// the main tab instead of opening a settings panel.
  final int? tabIndex;
  final String label;
  final String groupLabel;
  final List<String> keywords;
  final String? description;
  final List<String> relatedSettings;

  const SettingsSearchEntry({
    this.panelId,
    this.settingId,
    this.helpKey,
    this.tabIndex,
    required this.label,
    required this.groupLabel,
    required this.keywords,
    this.description,
    this.relatedSettings = const <String>[],
  });

  String get id =>
      settingId ?? panelId ?? helpKey ?? (tabIndex != null ? 'nav.$tabIndex' : '');
}

/// Per-panel keyword corpus. Each entry lists user-intent words the panel
/// answers, beyond what the panel label already says. Curated by hand so
/// the palette feels useful from day one.
const Map<String, List<String>> _panelKeywords = {
  'eq.camera': [
    'sensor',
    'cooler',
    'cooling',
    'temperature',
    'pixel',
    'gain',
    'offset',
    'bayer',
    'alpaca',
    'connect',
    'image processing',
    'opencv',
    'opencvsharp',
    'bitmap',
    'warmup',
    'thermal shock',
    'condensation',
  ],
  'eq.mount': [
    'slew',
    'tracking',
    'park',
    'meridian',
    'flip',
    'altitude',
    'limit',
    'sidereal',
    'stop mount',
    'safety',
    'slew safety',
  ],
  'eq.focuser': [
    'steps',
    'backlash',
    'autofocus',
    'temp',
    'compensation',
    'smart focus',
    'hocus focus',
  ],
  'eq.filterwheel': ['filter', 'slot', 'L', 'R', 'G', 'B', 'Ha', 'OIII', 'SII', 'efw'],
  'eq.rotator': ['angle', 'rotate', 'reverse'],
  'eq.guider': [
    'phd2',
    'guide',
    'dither',
    'settle',
    'calibration',
    'rms',
    'openastro-phd2',
    'aggressiveness',
  ],
  'eq.flat': ['cover', 'calibrator', 'panel', 'brightness', 'adu'],
  'eq.dome': ['shutter', 'slave', 'azimuth', 'home'],
  'eq.weather': [
    'cloud',
    'wind',
    'humidity',
    'rain',
    'dew',
    'observe',
    'poll',
    'wifi drops',
    'network unstable',
  ],
  'eq.safety': ['unsafe', 'safe', 'resume', 'monitor'],
  'img.defaults': [
    'exposure',
    'gain',
    'offset',
    'bin',
    'frame',
    'light',
    'cooler',
    'warmup',
    'target',
    'image rendering',
    'stf',
    'auto stretch',
  ],
  'img.autofocus': [
    'af',
    'autofocus',
    'hfr',
    'v-curve',
    'steps',
    'drift',
    'trigger',
    'recalibrate',
  ],
  'img.platesolve': [
    'astap',
    'astrometry',
    'solve',
    'sync',
    'center',
    'blind',
    'iterations',
    'convergence',
  ],
  'session.storage': [
    'disk',
    'sd',
    'usb',
    'ext4',
    'format',
    'compression',
    'rice',
    'fits',
    'directory',
    'quarantine',
    'damaged file',
    'reformat',
    'eject',
  ],
  'session.filenames': [
    'naming',
    'template',
    'tokens',
    'date',
    'filter',
    'target',
    'fits',
    'xisf',
    'underscore',
    'snake case',
  ],
  'session.notifications': [
    'push',
    'pushover',
    'telegram',
    'banner',
    'desktop',
    'sound',
    'alert',
    'alarm',
  ],
  'safety.policies': [
    'pause',
    'abort',
    'park',
    'meridian',
    'guider',
    'altitude',
    'unsafe',
    'session',
    'end',
  ],
  'safety.diagnostics': [
    'diagnostics',
    'notify',
    'pause',
    'abort',
    'critical',
    'mode',
    'smart corrections',
    'auto refocus',
    'aggression dial',
  ],
  'safety.site': [
    'latitude',
    'longitude',
    'elevation',
    'timezone',
    'horizon',
    'bortle',
    'seeing',
    'twilight',
  ],
  'sky.data': [
    'hips',
    'survey',
    'catalog',
    'tycho',
    'gaia',
    'ucac',
    'thumbnails',
    'ephemerides',
    'de440',
    'mpc',
    'data manager',
    'downloads',
  ],
  'profile.active': [
    'profile',
    'switch',
    'export',
    'import',
    'json',
    'duplicate',
    'delete',
    'nina profile',
  ],
  'profile.wizard': ['wizard', 'rerun', 'setup', 'walkthrough'],
  'app.changelog': [
    'changelog',
    'release notes',
    'what\'s new',
    'version history',
    'updates',
    'what changed',
  ],
  'app.monitoring': [
    'health check',
    'healthz',
    'readyz',
    'uptime',
    'prometheus',
    'performance',
    'latency',
    'slow',
    'websocket catalog',
    'ws protocol',
  ],
};

/// §61.10 — the main tabs as palette navigation targets. Indices come from
/// the settings_nav constants, which app_shell runtime-asserts against the
/// actual tab labels — a rail reorder fails loudly instead of misnavigating.
/// Keywords cover what people expect each tab to be called, not just its label.
const List<SettingsSearchEntry> _navEntries = <SettingsSearchEntry>[
  SettingsSearchEntry(
    tabIndex: kPlanningTabIndex,
    label: 'Go to Planning',
    groupLabel: 'Navigate',
    keywords: ['planning', 'tonight', 'sky atlas', 'targets', 'framing', 'planetarium', 'what to shoot'],
  ),
  SettingsSearchEntry(
    tabIndex: kRunTabIndex,
    label: 'Go to Run',
    groupLabel: 'Navigate',
    keywords: ['run', 'sequencer', 'sequence', 'session', 'start', 'progress'],
  ),
  SettingsSearchEntry(
    tabIndex: kLiveTabIndex,
    label: 'Go to Live',
    groupLabel: 'Navigate',
    keywords: ['live', 'imaging', 'camera', 'live view', 'capture', 'exposure', 'preview'],
  ),
  SettingsSearchEntry(
    tabIndex: kOptionsTabIndex,
    label: 'Go to Options',
    groupLabel: 'Navigate',
    keywords: ['options', 'settings', 'preferences', 'configuration'],
  ),
];

List<SettingsSearchEntry> buildSearchIndex() {
  final entries = <SettingsSearchEntry>[];
  // 0. §61.10 — tab navigation ("go to run", "planning", …).
  entries.addAll(_navEntries);
  // 1. Index panels.
  for (final group in settingsTree) {
    for (final panel in group.panels) {
      entries.add(SettingsSearchEntry(
        panelId: panel.id,
        label: panel.label,
        groupLabel: group.label,
        keywords: _panelKeywords[panel.id] ?? const <String>[],
      ));
    }
  }
  // 2. Index individual settings.
  for (final s in settingsRegistry) {
    entries.add(SettingsSearchEntry(
      settingId: s.id,
      label: s.label,
      groupLabel: s.path.join(' › '),
      keywords: s.keywords,
      description: s.description,
      relatedSettings: s.relatedSettings,
      // Map setting to its parent panel if possible for jumping.
      panelId: _mapSettingToPanel(s),
    ));
  }
  // 3. §68.4 — index help entries as informational hits. The body doubles as
  // the description so a phrase from the help text itself still matches (the
  // lowest-score tier, same as setting descriptions).
  for (final h in helpRegistry.values) {
    entries.add(SettingsSearchEntry(
      helpKey: h.key,
      label: h.title,
      groupLabel: 'Help',
      keywords: h.keywords,
      description: h.body,
    ));
  }
  return entries;
}

String? _mapSettingToPanel(Setting s) {
  // Map a setting's dotted ID to a panel ID in settingsTree.
  //
  // Two flavors:
  //   (a) IDs whose first segment already matches the panel-ID first segment
  //       (eq./img./session./safety.) — take the first two segments verbatim.
  //   (b) IDs whose first segment is a topic name that lives inside a panel
  //       (imaging.* lives in img., diagnostics.* in safety.diagnostics, etc.)
  //       — hand-map by topic.
  if (s.id.startsWith('eq.')) return s.id.split('.').take(2).join('.');
  if (s.id.startsWith('img.')) return s.id.split('.').take(2).join('.');
  if (s.id.startsWith('session.')) return s.id.split('.').take(2).join('.');
  if (s.id.startsWith('safety.')) return s.id.split('.').take(2).join('.');
  // Topic-name → panel mappings. Keep in sync with registry IDs + settingsTree.
  if (s.id.startsWith('imaging.defaults.')) return 'img.defaults';
  if (s.id.startsWith('imaging.autofocus.')) return 'img.autofocus';
  if (s.id.startsWith('imaging.platesolve.')) return 'img.platesolve';
  if (s.id.startsWith('diagnostics.')) return 'safety.diagnostics';
  if (s.id.startsWith('guider.')) return 'eq.guider';
  if (s.id.startsWith('camera.')) return 'eq.camera';
  if (s.id.startsWith('storage.')) return 'session.storage';
  if (s.id.startsWith('filenames.')) return 'session.filenames';
  return null;
}

/// Score an entry against a normalized lower-cased query. Higher = better.
/// Returns 0 for no match.
int _scoreEntry(SettingsSearchEntry entry, String query) {
  if (query.isEmpty) return 0;
  final label = entry.label.toLowerCase();
  final group = entry.groupLabel.toLowerCase();
  final desc = entry.description?.toLowerCase() ?? '';

  // Exact label match wins.
  if (label == query) return 1000;
  // Label starts with query.
  if (label.startsWith(query)) return 500;
  // Label contains query.
  if (label.contains(query)) return 300;
  // Group label match (e.g. typing "equipment" surfaces the group).
  if (group.startsWith(query)) return 250;
  if (group.contains(query)) return 200;
  // Keyword exact or prefix match.
  var keywordScore = 0;
  for (final kw in entry.keywords) {
    final k = kw.toLowerCase();
    if (k == query) {
      keywordScore = 180;
      break;
    }
    if (k.startsWith(query)) {
      keywordScore = keywordScore < 120 ? 120 : keywordScore;
    } else if (k.contains(query)) {
      keywordScore = keywordScore < 80 ? 80 : keywordScore;
    }
  }
  if (keywordScore > 0) return keywordScore;

  // Description match.
  if (desc.contains(query)) return 50;

  return 0;
}

class _ScoredEntry {
  final int score;
  final SettingsSearchEntry entry;
  _ScoredEntry(this.score, this.entry);
}

/// Top N matches for `query`, ranked by score descending. Capped at 20
/// (a fully empty query returns an empty list so the palette starts blank).
List<SettingsSearchEntry> searchSettings(
  List<SettingsSearchEntry> index,
  String query, {
  int limit = 20,
}) {
  final q = query.trim().toLowerCase();
  if (q.isEmpty) return const <SettingsSearchEntry>[];
  if (limit <= 0) return const <SettingsSearchEntry>[];
  final List<_ScoredEntry> scored = [];
  for (final entry in index) {
    final s = _scoreEntry(entry, q);
    if (s > 0) scored.add(_ScoredEntry(s, entry));
  }
  scored.sort((a, b) => b.score.compareTo(a.score));
  return scored.take(limit).map((p) => p.entry).toList();
}
