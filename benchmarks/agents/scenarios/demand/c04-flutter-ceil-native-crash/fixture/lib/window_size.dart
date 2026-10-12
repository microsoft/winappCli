import 'dart:ui';

int scaleDimension(double source, double scaleFactor, double userScale) {
  return (source * scaleFactor * userScale).ceil();
}

Size chooseInitialSize(Size appWindowSize, double scaleFactor, double userScale) {
  final normalWidth = scaleDimension(appWindowSize.width, scaleFactor, userScale);
  final normalHeight = scaleDimension(appWindowSize.height, scaleFactor, userScale);
  return Size(normalWidth.toDouble(), normalHeight.toDouble());
}
