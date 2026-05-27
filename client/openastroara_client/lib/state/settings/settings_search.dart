import 'settings_nav.dart';

/// §61 smart-search registry. Indexes every settable panel from
/// `settingsTree` plus a small per-panel keyword corpus so users can find
/// settings by intent ("dither", "park", "cooler") not just by exact label.
///
/// Phase 12h.3 ships the index + a simple substring-with-keyword-boost
/// scorer. Future phases (12h.4+?) may upgrade to fuzzy matching or
/// per-row indexing (so "exposure" finds Imaging Defaults's
/// "Default exposure (s)" row directly).

class SettingsSearchEntry {
  final String panelId;
  final String label;
  final String groupLabel;
  final List<String> keywords;

  const SettingsSearchEntry({
    required this.panelId,
    required this.label,
    required this.groupLabel,
    required this.keywords,
  });
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
  ],
  'eq.focuser': ['steps', 'backlash', 'autofocus', 'temp', 'compensation'],
  'eq.filterwheel': ['filter', 'slot', 'L', 'R', 'G', 'B', 'Ha', 'OIII', 'SII'],
  'eq.rotator': ['angle', 'rotate', 'reverse'],
  'eq.guider': [
    'phd2',
    'guide',
    'dither',
    'settle',
    'calibration',
    'rms',
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
  ],
  'img.autofocus': [
    'af',
    'autofocus',
    'hfr',
    'v-curve',
    'steps',
    'drift',
    'trigger',
  ],
  'img.platesolve': [
    'astap',
    'astrometry',
    'solve',
    'sync',
    'center',
    'blind',
    'iterations',
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
    'aladin',
    'catalog',
    'tycho',
    'gaia',
    'ucac',
    'thumbnails',
    'ephemerides',
    'de440',
    'mpc',
  ],
  'profile.active': [
    'profile',
    'switch',
    'export',
    'import',
    'json',
    'duplicate',
    'delete',
  ],
  'profile.wizard': ['wizard', 'rerun', 'setup', 'walkthrough'],
};

List<SettingsSearchEntry> buildSearchIndex() {
  final entries = <SettingsSearchEntry>[];
  for (final group in settingsTree) {
    for (final panel in group.panels) {
      entries.add(SettingsSearchEntry(
        panelId: panel.id,
        label: panel.label,
        groupLabel: group.label,
        keywords: _panelKeywords[panel.id] ?? const [],
      ));
    }
  }
  return entries;
}

/// Score an entry against a normalized lower-cased query. Higher = better.
/// Returns 0 for no match.
int _scoreEntry(SettingsSearchEntry entry, String query) {
  if (query.isEmpty) return 0;
  final label = entry.label.toLowerCase();
  final group = entry.groupLabel.toLowerCase();

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
  return keywordScore;
}

/// Top N matches for `query`, ranked by score descending. Capped at 20
/// (a fully empty query returns an empty list so the palette starts blank).
List<SettingsSearchEntry> searchSettings(
  List<SettingsSearchEntry> index,
  String query, {
  int limit = 20,
}) {
  final q = query.trim().toLowerCase();
  if (q.isEmpty) return const [];
  final scored = <(int, SettingsSearchEntry)>[];
  for (final entry in index) {
    final s = _scoreEntry(entry, q);
    if (s > 0) scored.add((s, entry));
  }
  scored.sort((a, b) => b.$1.compareTo(a.$1));
  return scored.take(limit).map((p) => p.$2).toList();
}
