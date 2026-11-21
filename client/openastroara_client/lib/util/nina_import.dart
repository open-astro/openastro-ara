import '../models/sequence/sequence_v2_kinds.dart';

/// Client-side NINA import (docs/specs/sequence-v2.md, "NINA JSON import",
/// #2026): the successor of the `$type`-renaming translation. It maps a NINA
/// Json.NET export to a sequence v2 document (a tree of `kind` nodes) and
/// collects the warnings the user sees before the upload. The file lives on
/// this machine, so the mapping is client code and the daemon's v2 validator
/// gates the create. The kind table and the rename rules are data in
/// `sequence_v2_kinds.dart`.
///
/// What cannot be mapped becomes a placeholder (`kind: "unknown"`) that keeps
/// the source node; a file that cannot be imported at all is refused with
/// [NinaImportRefused] and nothing is uploaded.
class NinaImportTranslation {
  final Map<String, dynamic> body;
  final List<String> warnings;
  const NinaImportTranslation({required this.body, required this.warnings});
}

/// Why a whole import was refused. The widget shows the localized sentence for
/// each; [NinaImportRefused.message] is the English one the spec words.
enum NinaRefusal { notNina, sharedNode }

class NinaImportRefused implements Exception {
  const NinaImportRefused(this.refusal);

  final NinaRefusal refusal;

  String get message => switch (refusal) {
    NinaRefusal.notNina => 'This file is not a NINA sequence.',
    NinaRefusal.sharedNode =>
      'This sequence has a shared node, which OpenAstro Ara cannot import.',
  };

  @override
  String toString() => 'NinaImportRefused: $message';
}

/// The value of `schemaVersion` the importer writes.
const String schemaVersion = 'openastroara-sequence-v2';

const _reasonNoKind = 'OpenAstro Ara has no kind for this NINA type.';
const _reasonPlugin =
    'This type comes from a NINA plugin that OpenAstro Ara does not support.';
const _reasonNoType = 'This node has no type.';

String _placeholderWarning(String originalType) =>
    '"$originalType" is not supported by OpenAstro Ara and was not imported. '
    'It stays in the sequence as a placeholder that is skipped when the '
    'sequence runs.';

String _deviceWarning(String device, String kind) =>
    'The device "$device" of "$kind" is not one of this rig\'s equipment '
    'roles. It was kept as found; assign it in the editor.';

/// Map a decoded NINA export to a v2 document and its warnings. A body that
/// already says [schemaVersion] is returned as written; any other
/// `schemaVersion`, or a body with no top-level `$type`, is refused. The input
/// is never mutated. [fileName] and [now] fill `metadata.importedFrom`.
NinaImportTranslation translateNinaSequence(
  Map<String, dynamic> nina, {
  String fileName = '',
  DateTime? now,
}) {
  if (nina.containsKey('schemaVersion')) {
    if (nina['schemaVersion'] != schemaVersion) {
      throw const NinaImportRefused(NinaRefusal.notNina);
    }
    return NinaImportTranslation(body: nina, warnings: const []);
  }
  final type = nina[r'$type'];
  final kind = type is String ? _kindFor(type) : null;
  if (kind == null || kind.role != SequenceV2Role.container) {
    throw const NinaImportRefused(NinaRefusal.notNina);
  }
  final mapper = _Mapper(nina);
  final mapped = mapper.node(nina, kind);
  final name = nina['Name'];
  final title = name is String && name.trim().isNotEmpty
      ? name
      : fileName.replaceFirst(RegExp(r'\.json$', caseSensitive: false), '');
  return NinaImportTranslation(
    body: {
      'schemaVersion': schemaVersion,
      'name': title,
      'metadata': {
        'importedFrom': {
          'format': 'nina',
          'fileName': fileName,
          'importedUtc': (now ?? DateTime.now()).toUtc().toIso8601String(),
        },
      },
      'targets': mapper.targets,
      // A registered container other than the root class is wrapped in one.
      'root': kind.kind == 'root'
          ? mapped
          : {
              'kind': 'root',
              'name': title,
              'items': [mapped],
            },
    },
    warnings: mapper.warnings,
  );
}

/// The kind of a `$type` token, or null when the table names none. Only the
/// NINA and OpenAstroAra sequencer namespaces and the Connector plugin's six
/// classes map; the assembly is not read, so a plugin class that shares a
/// kind's name stays a placeholder.
SequenceV2Kind? _kindFor(String type) {
  final full = type.split(',').first.trim();
  final dot = full.lastIndexOf('.');
  if (dot < 0) return null;
  final namespace = full.substring(0, dot + 1);
  final cls = full.substring(dot + 1);
  if (ninaSequencerNamespaces.any(namespace.startsWith)) {
    return ninaAreaContainers.contains(cls)
        ? sequenceV2KindByNinaClass['SequentialContainer']
        : sequenceV2KindByNinaClass[cls];
  }
  if (namespace.startsWith(ninaConnectorNamespace) &&
      ninaConnectorClasses.contains(cls)) {
    return sequenceV2KindByNinaClass[cls];
  }
  return null;
}

/// The lists a node is found in, and the roles each takes.
enum _List {
  items({SequenceV2Role.container, SequenceV2Role.instruction}),
  conditions({SequenceV2Role.condition}),
  triggers({SequenceV2Role.trigger}),
  run({SequenceV2Role.instruction});

  const _List(this.roles);
  final Set<SequenceV2Role> roles;
}

class _Mapper {
  _Mapper(Map<String, dynamic> root) {
    _index(root);
  }

  final targets = <Map<String, dynamic>>[];
  final warnings = <String>[];
  final _ids = <String, Map>{};
  final _active = Set<Object>.identity();

  void _index(Object? v) {
    if (v is Map) {
      final id = v[r'$id'];
      if (id is String) _ids[id] = v;
      v.values.forEach(_index);
    } else if (v is List) {
      v.forEach(_index);
    }
  }

  Never _refuse([NinaRefusal why = NinaRefusal.notNina]) =>
      throw NinaImportRefused(why);

  /// A `$ref` in a value position is the earlier object with that `$id`.
  Map _expand(Map v) {
    if (!v.containsKey(r'$ref')) return v;
    return _ids[v[r'$ref']] ?? _refuse();
  }

  /// A value copied as found but for its keys (lowerCamelCase, no `$type`,
  /// `$id` or `$ref`) and its `$values` wrappers.
  Object? value(Object? v) {
    if (v is List) return [for (final e in v) value(e)];
    if (v is! Map) return v;
    final map = _expand(v);
    if (!_active.add(map)) _refuse();
    try {
      final values = map[r'$values'];
      if (values is List) return value(values);
      return {
        for (final e in map.entries)
          if (!(e.key as String).startsWith(r'$'))
            lowerCamel(e.key as String): field(
              lowerCamel(e.key as String),
              e.value,
            ),
      };
    } finally {
      _active.remove(map);
    }
  }

  Object? field(String name, Object? v) =>
      name == 'imageType' && v == 'DARKFLAT' ? 'DARK' : value(v);

  /// A nested value that keeps only the keys [ninaNestedKeys] names.
  Object? nested(String name, Object? v) {
    if (v is! Map) return v;
    final keep = ninaNestedKeys[name]!;
    final map = _expand(v);
    return {
      for (final e in map.entries)
        if (keep.contains(
          lowerCamel((e.key as String).replaceFirst(RegExp('^_'), '')),
        ))
          lowerCamel((e.key as String).replaceFirst(RegExp('^_'), '')): value(
            e.value,
          ),
    };
  }

  /// The entries of a collection value (`$values` or a plain array).
  List<Object?> _entries(Object? v) {
    final resolved = v is Map ? _expand(v) : v;
    final list = resolved is Map ? resolved[r'$values'] : resolved;
    if (list == null) return const [];
    return list is List ? list : _refuse();
  }

  List<Map<String, dynamic>> _children(Object? v, _List list) => [
    for (final e in _entries(v)) _listEntry(e, list),
  ];

  /// A node found in [list]: mapped by its kind, or a placeholder.
  Map<String, dynamic> _listEntry(Object? entry, _List list) {
    if (entry is! Map) _refuse();
    if (entry.containsKey(r'$ref')) _refuse(NinaRefusal.sharedNode);
    final src = entry.cast<String, dynamic>();
    final type = src[r'$type'];
    if (type is! String || type.isEmpty) {
      return _placeholder(src, list, '', _reasonNoType);
    }
    final kind = _kindFor(type);
    if (kind == null ||
        kind.kind == 'root' ||
        !list.roles.contains(kind.role)) {
      final core = ninaSequencerNamespaces.any(type.startsWith);
      return _placeholder(
        src,
        list,
        type,
        core ? _reasonNoKind : _reasonPlugin,
      );
    }
    return node(src, kind);
  }

  Map<String, dynamic> _placeholder(
    Map<String, dynamic> src,
    _List list,
    String originalType,
    String reason,
  ) {
    warnings.add(_placeholderWarning(originalType));
    final full = originalType.split(',').first.trim();
    final dot = full.lastIndexOf('.');
    final namespace = dot < 0 ? '' : full.substring(0, dot);
    final role = switch (list) {
      _List.conditions => 'condition',
      _List.triggers => 'trigger',
      _List.run => 'instruction',
      _List.items =>
        namespace.endsWith('Container') || full.endsWith('Container')
            ? 'container'
            : 'instruction',
    };
    return {
      'kind': 'unknown',
      'role': role,
      'name': originalType,
      'originalType': originalType,
      'originalPayload': _copy(src),
      'reason': reason,
    };
  }

  Map<String, dynamic> node(Map<String, dynamic> src, SequenceV2Kind kind) {
    final fields = kind.fields;
    final out = <String, dynamic>{'kind': kind.kind};
    Map<String, dynamic>? target;
    if (kind.kind == 'target') {
      // Reserved before the children are walked, so ids follow the tree order.
      target = {'id': 't${targets.length + 1}'};
      targets.add(target);
      out['targetId'] = target['id'];
    }
    final shims = <String, Object?>{};
    for (final e in src.entries) {
      final key = e.key;
      if (key.startsWith(r'$')) continue;
      if (key == ninaTargetProperty && target != null) {
        _fillTarget(target, e.value);
        continue;
      }
      final name = ninaFieldRenames[key] ?? lowerCamel(key);
      if (!fields.contains(name)) {
        if (fields.contains('data') && altitudeShims.containsKey(key)) {
          shims[key] = e.value;
        }
        continue;
      }
      final v = e.value;
      switch (name) {
        case 'items':
          out[name] = _children(v, _List.items);
        case 'conditions':
          out[name] = _children(v, _List.conditions);
        case 'triggers':
          out[name] = _children(v, _List.triggers);
        case 'run':
          if (v is Map && v.containsKey(r'$ref')) {
            _refuse(NinaRefusal.sharedNode);
          }
          // An empty runner is the default, so it is not written.
          final run = v is Map ? _children(v['Items'], _List.run) : const [];
          if (run.isNotEmpty) out[name] = run;
        case 'errorBehavior':
          _word(out, name, v, errorBehaviorTokens);
        case 'trackingMode':
          _word(out, name, v, trackingModeNames);
        case 'provider':
          final provider = v is Map ? _expand(v)[r'$type'] : null;
          final cls = provider is String
              ? provider.split(',').first.split('.').last.trim()
              : null;
          if (sequenceV2Providers[cls] case final p?) out[name] = p;
        case 'binning' || 'filter':
          out[name] = nested(name, v);
        default:
          out[name] = field(name, v);
      }
    }
    if (fields.contains('data')) _foldAltitude(out, shims);
    _warnDevices(out, kind);
    return out;
  }

  /// An enum the engine writes as a number is its word; a string stays.
  void _word(
    Map<String, dynamic> out,
    String name,
    Object? v,
    List<String> words,
  ) {
    final word = v is int ? (v >= 0 && v < words.length ? words[v] : null) : v;
    if (word is String) out[name] = word;
  }

  /// `data` is `Data` recased; a deprecation shim wins over it, and the
  /// comparator is an engine name, the inclusive operators folded.
  void _foldAltitude(Map<String, dynamic> out, Map<String, Object?> shims) {
    final data = <String, dynamic>{
      if (out['data'] case final Map d) ...d.cast<String, dynamic>(),
    };
    for (final e in shims.entries) {
      data[altitudeShims[e.key]!] = value(e.value);
    }
    final comparator = data['comparator'];
    if (comparator != null) {
      final word = comparator is int
          ? (comparator >= 0 && comparator < comparatorNames.length
                ? comparatorNames[comparator]
                : null)
          : comparator;
      if (word is String) data['comparator'] = comparatorFolds[word] ?? word;
    }
    if (data.isNotEmpty) out['data'] = data;
  }

  void _warnDevices(Map<String, dynamic> out, SequenceV2Kind kind) {
    final device = out['selectedDevice'];
    if (device is String &&
        device.isNotEmpty &&
        !rigRoleKeys.contains(device)) {
      warnings.add(_deviceWarning(device, kind.kind));
    }
    final number = out['alpacaDeviceNumber'];
    if (number is int && number != -1) {
      warnings.add(_deviceWarning('$number', kind.kind));
    }
  }

  /// The element of `targets` for a `DeepSkyObjectContainer`'s `Target`.
  void _fillTarget(Map<String, dynamic> target, Object? v) {
    final src = v is Map ? _expand(v) : const {};
    final coordinates = {...targetCoordinateKeys};
    final input = src['InputCoordinates'];
    if (input is Map) {
      for (final e in _expand(input).entries) {
        final key = lowerCamel(e.key as String);
        if (coordinates.containsKey(key) && e.value != null) {
          coordinates[key] = e.value as Object;
        }
      }
    }
    final name = src['TargetName'];
    final angle = src['PositionAngle'];
    final rotation = src['Rotation'];
    target['name'] = name is String ? name : '';
    target['coordinates'] = coordinates;
    // `Rotation` is the legacy `360 - PositionAngle` (`InputTarget`).
    target['positionAngle'] = angle is num
        ? angle
        : rotation is num
        ? (360 - rotation) % 360
        : 0.0;
  }

  Object? _copy(Object? v) => switch (v) {
    Map m => {for (final e in m.entries) e.key as String: _copy(e.value)},
    List l => [for (final e in l) _copy(e)],
    _ => v,
  };
}
