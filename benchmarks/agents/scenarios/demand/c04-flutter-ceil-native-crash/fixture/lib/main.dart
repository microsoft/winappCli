import 'dart:async';
import 'dart:ui';

import 'package:flutter/material.dart';
import 'window_size.dart';

void main() {
  FlutterError.onError = (details) {
    debugPrint('FlutterError: ${details.exception}');
  };

  PlatformDispatcher.instance.onError = (error, stack) {
    debugPrint('Platform error: $error');
    return true;
  };

  runZonedGuarded(() {
    final initialSize = chooseInitialSize(const Size(1016, 640), 1.0, 1.0);
    runApp(MaterialApp(home: Text('Size: $initialSize')));
  }, (error, stack) {
    debugPrint('Zone error: $error');
  });
}
