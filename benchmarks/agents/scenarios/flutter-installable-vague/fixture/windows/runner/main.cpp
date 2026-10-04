#include <flutter/dart_project.h>
#include <windows.h>
#include "flutter_window.h"

int APIENTRY wWinMain(HINSTANCE instance, HINSTANCE prev, wchar_t* command_line, int show_command) {
  flutter::DartProject project(L"data");
  FlutterWindow window(project);
  if (!window.Create(L"Fernhill Notes", {10, 10}, {1280, 720})) return EXIT_FAILURE;
  window.SetQuitOnClose(true);
  MSG msg;
  while (::GetMessage(&msg, nullptr, 0, 0)) { ::TranslateMessage(&msg); ::DispatchMessage(&msg); }
  return EXIT_SUCCESS;
}
