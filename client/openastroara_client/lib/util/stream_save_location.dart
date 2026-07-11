import 'dart:io';

import 'package:file_picker/file_picker.dart';

/// §54/§29.9 streaming downloads — pick a destination PATH before the bytes
/// exist, so the download can stream to disk instead of being buffered whole
/// in memory. file_picker v12's `saveFile` always takes the bytes and writes
/// them itself, so the streaming flow asks for a directory instead and places
/// [suggestedName] inside it, uniquified (`name (1).ext`, …) rather than
/// silently overwriting. Returns null when the user cancels.
Future<String?> pickStreamSavePath(
  String dialogTitle,
  String suggestedName,
) async {
  final dir = await FilePicker.getDirectoryPath(dialogTitle: dialogTitle);
  if (dir == null) return null;
  return uniquePathIn(dir, suggestedName);
}

/// [name] inside [dir], with ` (1)`, ` (2)`, … inserted before the extension
/// until the path is free. Exposed for tests.
String uniquePathIn(String dir, String name) {
  final sep = Platform.pathSeparator;
  final dot = name.lastIndexOf('.');
  final stem = dot > 0 ? name.substring(0, dot) : name;
  final ext = dot > 0 ? name.substring(dot) : '';
  var candidate = '$dir$sep$name';
  var n = 1;
  while (FileSystemEntity.typeSync(candidate) !=
      FileSystemEntityType.notFound) {
    candidate = '$dir$sep$stem ($n)$ext';
    n++;
  }
  return candidate;
}
