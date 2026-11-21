import 'dart:convert';
import 'dart:io';

/// Test-only helpers that read the daemon's v2 sources of truth from the repo
/// (the client tests already read daemon files this way, e.g.
/// `refusal_openapi_drift_test.dart`): the generated schema and the kind table.
/// They are how the Dart importer's mapping is held to the C# one (#2026).
const _schemaPath = '../../docs/specs/sequence-v2.schema.json';
const _kindsPath = '../../OpenAstroAra.Sequencer/Format/SequenceKinds.cs';

Map<String, dynamic> loadSequenceV2Schema() =>
    jsonDecode(File(_schemaPath).readAsStringSync()) as Map<String, dynamic>;

/// `kind` -> engine class name, for every kind the schema gives an engine
/// class (so not `unknown`, `glover-exposure` or the target element).
Map<String, String> schemaKindClasses() {
  final defs = loadSequenceV2Schema()[r'$defs'] as Map<String, dynamic>;
  return {
    for (final e in defs.entries)
      if ((e.value as Map)['x-ara-engine-class'] != null)
        e.key: (e.value as Map)['x-ara-engine-class'] as String,
  };
}

/// One row of `SequenceKinds.All`: the kind, the engine class and its role
/// (`Container`, `Instruction`, `Condition`, `Trigger`).
typedef DaemonKind = ({String kind, String engineClass, String role});

List<DaemonKind> daemonKinds() {
  final text = File(_kindsPath).readAsStringSync();
  final row = RegExp(
    r'new\("([a-z-]+)", typeof\((\w+)\), SequenceNodeRole\.(\w+)',
  );
  return [
    for (final m in row.allMatches(text))
      (kind: m.group(1)!, engineClass: m.group(2)!, role: m.group(3)!),
  ];
}

bool _isType(Object? v, String t) => switch (t) {
  'string' => v is String,
  // The daemon refuses the `5.0` token for an integer (`FitsInteger`).
  'integer' => v is int,
  'number' => v is num,
  'boolean' => v is bool,
  'array' => v is List,
  'object' => v is Map,
  'null' => v == null,
  _ => false,
};

/// The roles a list takes, as `SequenceNodeRole` names in lower case.
const _listRoles = {
  'items': {'container', 'instruction'},
  'conditions': {'condition'},
  'triggers': {'trigger'},
  'run': {'instruction'},
};

/// The violations of a v2 document that the daemon validator reports for what
/// the importer controls: unknown keys, missing required keys, wrong value
/// kinds (an integer is an `int`), an unknown `kind`, `wrong_role` by list (a
/// placeholder by its `role`), `misplaced_root`, `duplicate_target_id` and an
/// unresolved `targetId`. Whether a capturable instruction is reachable is
/// [hasCapturable]. An `unknown` node's payload is opaque and not walked, as
/// in the daemon.
List<String> schemaViolations(Map<String, dynamic> doc) {
  final schema = loadSequenceV2Schema();
  final defs = schema[r'$defs'] as Map<String, dynamic>;
  final roleOf = {for (final k in daemonKinds()) k.kind: k.role.toLowerCase()};
  const roles = {'container', 'instruction', 'condition', 'trigger'};
  final errors = <String>[];

  void object(Object? value, Map<String, dynamic> def, String path) {
    if (value is! Map) {
      errors.add('$path: not an object');
      return;
    }
    final props = def['properties'] as Map<String, dynamic>;
    for (final key in value.keys) {
      final prop = props[key];
      if (prop == null) {
        errors.add('$path.$key: unknown field');
        continue;
      }
      final spec = prop as Map<String, dynamic>;
      if (spec.containsKey('const')) {
        if (value[key] != spec['const']) {
          errors.add('$path.$key: not ${spec['const']}');
        }
        continue;
      }
      final type = spec['type'];
      final types = type is List
          ? type.cast<String>()
          : [if (type != null) type as String];
      if (types.isNotEmpty && !types.any((t) => _isType(value[key], t))) {
        errors.add('$path.$key: wrong type ${value[key].runtimeType}');
      }
    }
    for (final key in (def['required'] as List? ?? const []).cast<String>()) {
      if (!value.containsKey(key)) errors.add('$path.$key: missing');
    }
  }

  final targetIds = <String>{};
  void node(Object? n, String path, String list) {
    if (n is! Map) {
      errors.add('$path: not an object');
      return;
    }
    final kind = n['kind'];
    final def = kind is String ? defs[kind] as Map<String, dynamic>? : null;
    if (def == null) {
      errors.add('$path: unknown kind $kind');
      return;
    }
    object(n, def, path);
    if (kind == 'root' && path != r'$.root') {
      errors.add('$path: misplaced root');
    }
    final role = kind == 'unknown' ? n['role'] : roleOf[kind];
    if (kind == 'unknown' && !roles.contains(role)) {
      errors.add('$path.role: not a role: $role');
    } else if (!_listRoles[list]!.contains(role)) {
      errors.add('$path: wrong role $role in $list');
    }
    if (kind == 'unknown') return;
    if (kind == 'target') {
      final id = n['targetId'];
      if (id is! String || !targetIds.contains(id)) {
        errors.add('$path.targetId: unresolved $id');
      }
    }
    for (final list in const ['conditions', 'triggers', 'items', 'run']) {
      final children = n[list];
      if (children is List) {
        for (var i = 0; i < children.length; i++) {
          node(children[i], '$path.$list[$i]', list);
        }
      }
    }
  }

  object(doc, schema, r'$');
  final targets = doc['targets'];
  if (targets is List) {
    for (var i = 0; i < targets.length; i++) {
      object(
        targets[i],
        defs['target-element'] as Map<String, dynamic>,
        '\$.targets[$i]',
      );
      if (targets[i] is Map && (targets[i] as Map)['id'] is String) {
        final id = (targets[i] as Map)['id'] as String;
        if (!targetIds.add(id)) errors.add('\$.targets[$i].id: duplicate $id');
      }
    }
  }
  final root = doc['root'];
  if (root is Map && root['kind'] != 'root') {
    errors.add(r'$.root: kind is not root');
  }
  node(root, r'$.root', 'items');
  return errors;
}

/// Whether the daemon counts a capturable instruction: a `capture`,
/// `flat-panel-flats` or `sky-flats` reachable from the root through `items`
/// (not through a condition, a trigger's `run` or a placeholder's payload).
bool hasCapturable(Map<String, dynamic> doc) {
  bool walk(Map<String, dynamic> n) {
    if (const {
      'capture',
      'flat-panel-flats',
      'sky-flats',
    }.contains(n['kind'])) {
      return true;
    }
    final items = n['items'];
    return items is List &&
        items.any((c) => c is Map && walk(c.cast<String, dynamic>()));
  }

  final root = doc['root'];
  return root is Map && walk(root.cast<String, dynamic>());
}

/// Every node (a map with a `kind`) under [node], [node] first, in tree order
/// (`conditions`, `triggers` with each `run`, then `items`). A placeholder's
/// payload is not walked.
Iterable<Map<String, dynamic>> v2Nodes(Map<String, dynamic> node) sync* {
  yield node;
  if (node['kind'] == 'unknown') return;
  for (final list in const ['conditions', 'triggers', 'run', 'items']) {
    final children = node[list];
    if (children is List) {
      for (final c in children) {
        yield* v2Nodes((c as Map).cast<String, dynamic>());
      }
    }
  }
}
