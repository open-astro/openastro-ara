import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Simple Mode's answer for a profile set to ask about flats: picked inline
/// on the Plan sheet and sent when the run starts. The daemon's wire tokens;
/// `later` is "not tonight", what an unanswered run does.
final simpleFlatsChoiceProvider =
    NotifierProvider<_SimpleFlatsChoice, String>(_SimpleFlatsChoice.new);

class _SimpleFlatsChoice extends Notifier<String> {
  @override
  String build() => 'later';
  void set(String v) => state = v;
}
