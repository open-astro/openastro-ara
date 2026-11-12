import 'package:flutter/material.dart';

/// True where the controls take their iOS look: iPhone and iPad. Flutter's
/// `.adaptive` constructors also pick the iOS look on macOS; Ara keeps
/// Material on macOS and every other platform (#1632).
bool useCupertinoControls(BuildContext context) =>
    Theme.of(context).platform == TargetPlatform.iOS;

/// A slider: the iOS slider on iPhone and iPad, Material elsewhere. [key]
/// goes on the inner [Slider], so finders by key reach the slider itself.
class AdaptiveSlider extends StatelessWidget {
  // The key belongs to the inner control (tests and state find it there).
  // ignore: use_key_in_widget_constructors
  const AdaptiveSlider({
    Key? key,
    required this.value,
    required this.onChanged,
    this.onChangeEnd,
    this.min = 0.0,
    this.max = 1.0,
    this.divisions,
    this.label,
    this.semanticFormatterCallback,
  }) : _sliderKey = key;

  final Key? _sliderKey;
  final double value;
  final ValueChanged<double>? onChanged;
  final ValueChanged<double>? onChangeEnd;
  final double min;
  final double max;
  final int? divisions;
  final String? label;
  final SemanticFormatterCallback? semanticFormatterCallback;

  @override
  Widget build(BuildContext context) => useCupertinoControls(context)
      ? Slider.adaptive(
          key: _sliderKey,
          value: value,
          onChanged: onChanged,
          onChangeEnd: onChangeEnd,
          min: min,
          max: max,
          divisions: divisions,
          label: label,
          semanticFormatterCallback: semanticFormatterCallback,
        )
      : Slider(
          key: _sliderKey,
          value: value,
          onChanged: onChanged,
          onChangeEnd: onChangeEnd,
          min: min,
          max: max,
          divisions: divisions,
          label: label,
          semanticFormatterCallback: semanticFormatterCallback,
        );
}

/// A checkbox: the iOS checkbox on iPhone and iPad, Material elsewhere.
/// [key] goes on the inner [Checkbox].
class AdaptiveCheckbox extends StatelessWidget {
  // The key belongs to the inner control (tests and state find it there).
  // ignore: use_key_in_widget_constructors
  const AdaptiveCheckbox({
    Key? key,
    required this.value,
    required this.onChanged,
    this.visualDensity,
  }) : _checkboxKey = key;

  final Key? _checkboxKey;
  final bool? value;
  final ValueChanged<bool?>? onChanged;
  final VisualDensity? visualDensity;

  @override
  Widget build(BuildContext context) => useCupertinoControls(context)
      ? Checkbox.adaptive(
          key: _checkboxKey,
          value: value,
          onChanged: onChanged,
          visualDensity: visualDensity,
        )
      : Checkbox(
          key: _checkboxKey,
          value: value,
          onChanged: onChanged,
          visualDensity: visualDensity,
        );
}

/// A progress spinner: the iOS activity indicator on iPhone and iPad, the
/// Material ring elsewhere. iOS ignores [valueColor] and [strokeWidth]. A
/// determinate ring (a [value]) stays Material everywhere: the iOS indicator
/// draws it as a fixed 20 px spoke wheel.
class AdaptiveProgressIndicator extends StatelessWidget {
  // The key belongs to the inner control (tests and state find it there).
  // ignore: use_key_in_widget_constructors
  const AdaptiveProgressIndicator({
    Key? key,
    this.value,
    this.valueColor,
    this.strokeWidth,
  }) : _indicatorKey = key;

  final Key? _indicatorKey;
  final double? value;
  final Animation<Color?>? valueColor;
  final double? strokeWidth;

  @override
  Widget build(BuildContext context) =>
      useCupertinoControls(context) && value == null
      ? CircularProgressIndicator.adaptive(
          key: _indicatorKey,
          value: value,
          valueColor: valueColor,
          strokeWidth: strokeWidth,
        )
      : CircularProgressIndicator(
          key: _indicatorKey,
          value: value,
          valueColor: valueColor,
          strokeWidth: strokeWidth,
        );
}
