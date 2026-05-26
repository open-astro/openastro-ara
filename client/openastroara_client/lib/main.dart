import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'screens/first_run_screen.dart';

void main() {
  runApp(const ProviderScope(child: OpenAstroAraApp()));
}

class OpenAstroAraApp extends StatelessWidget {
  const OpenAstroAraApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'OpenAstro Ara WILMA',
      theme: ThemeData.dark(useMaterial3: true),
      home: const FirstRunScreen(),
    );
  }
}
