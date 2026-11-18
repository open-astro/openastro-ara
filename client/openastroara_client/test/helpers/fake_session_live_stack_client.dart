import 'dart:async';
import 'dart:typed_data';

import 'package:openastroara/services/session_live_stack_api.dart';

/// The daemon's Session Live Stack as one fake: [enabled], [unsupported],
/// [seq] and [lights] are what GET /live-stack reports, [frameNow] what
/// GET /live-stack/frame answers (null = 204). [calls] lists every request.
class FakeSessionLiveStackClient implements SessionLiveStackClient {
  final List<String> calls = [];
  bool enabled = false;
  String? unsupported;
  int seq = 0;
  int lights = 0;
  String? target;
  String? filter;
  StackFrame? frameNow;

  /// When set, status() answers only once it completes.
  Completer<void>? statusGate;
  Object? statusError;

  int count(String call) => calls.where((c) => c == call).length;

  /// A light stacked: the stack moves on one step and its frame with it.
  void stackOne() {
    seq++;
    lights++;
    frameNow = StackFrame(Uint8List.fromList([seq]), seq, lights);
  }

  @override
  Future<SessionLiveStackStatus> status() async {
    calls.add('status');
    if (statusGate != null) await statusGate!.future;
    if (statusError != null) throw statusError!;
    return SessionLiveStackStatus(
      enabled: enabled,
      unsupported: unsupported,
      stackSeq: seq,
      lightsStacked: lights,
      targetName: target,
      filterName: filter,
    );
  }

  @override
  Future<void> start() async {
    calls.add('start');
    enabled = true;
  }

  @override
  Future<void> reset() async {
    calls.add('reset');
    seq = 0;
    lights = 0;
    frameNow = null;
  }

  @override
  Future<void> stop() async {
    calls.add('stop');
    enabled = false;
    seq = 0;
    lights = 0;
    frameNow = null;
  }

  @override
  Future<StackFrame?> frame() async {
    calls.add('frame');
    return frameNow;
  }

  @override
  void close() => calls.add('close');
}
