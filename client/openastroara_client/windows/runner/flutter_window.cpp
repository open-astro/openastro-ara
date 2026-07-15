#include "flutter_window.h"

#include <flutter_windows.h>

#include <optional>

#include "flutter/generated_plugin_registrant.h"

FlutterWindow::FlutterWindow(const flutter::DartProject& project)
    : project_(project) {}

FlutterWindow::~FlutterWindow() {}

bool FlutterWindow::OnCreate() {
  if (!Win32Window::OnCreate()) {
    return false;
  }

  RECT frame = GetClientArea();

  // The size here must match the window dimensions to avoid unnecessary surface
  // creation / destruction in the startup path.
  flutter_controller_ = std::make_unique<flutter::FlutterViewController>(
      frame.right - frame.left, frame.bottom - frame.top, project_);
  // Ensure that basic setup of the controller was successful.
  if (!flutter_controller_->engine() || !flutter_controller_->view()) {
    return false;
  }
  RegisterPlugins(flutter_controller_->engine());
  SetChildContent(flutter_controller_->view()->GetNativeWindow());

  // openastroara/window — the Dart router flips between the compact launchpad
  // and the maximized §25 workstation shell. Mirrors macOS/Linux runners.
  window_mode_channel_ =
      std::make_unique<flutter::MethodChannel<flutter::EncodableValue>>(
          flutter_controller_->engine()->messenger(), "openastroara/window",
          &flutter::StandardMethodCodec::GetInstance());
  window_mode_channel_->SetMethodCallHandler(
      [this](const flutter::MethodCall<flutter::EncodableValue>& call,
             std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>>
                 result) {
        HWND hwnd = GetHandle();
        if (call.method_name() == "workstation") {
          min_size_ = {1100, 700};
          ShowWindow(hwnd, SW_SHOWMAXIMIZED);
          result->Success();
        } else if (call.method_name() == "launchpad") {
          min_size_ = {760, 560};
          ShowWindow(hwnd, SW_RESTORE);
          // DPI-scaled size, re-CENTERED on the work area — SW_RESTORE lands
          // wherever the window last was, which after a maximized session is
          // not necessarily centered (review #846 r3; matches macOS center()).
          UINT dpi = FlutterDesktopGetDpiForHWND(hwnd);
          double scale = dpi / 96.0;
          int w = static_cast<int>(960 * scale);
          int h = static_cast<int>(680 * scale);
          RECT wa;
          SystemParametersInfo(SPI_GETWORKAREA, 0, &wa, 0);
          int x = wa.left + ((wa.right - wa.left) - w) / 2;
          int y = wa.top + ((wa.bottom - wa.top) - h) / 2;
          SetWindowPos(hwnd, nullptr, x, y, w, h,
                       SWP_NOZORDER | SWP_NOACTIVATE);
          result->Success();
        } else {
          result->NotImplemented();
        }
      });

  flutter_controller_->engine()->SetNextFrameCallback([&]() {
    this->Show();
  });

  // Flutter can complete the first frame before the "show window" callback is
  // registered. The following call ensures a frame is pending to ensure the
  // window is shown. It is a no-op if the first frame hasn't completed yet.
  flutter_controller_->ForceRedraw();

  return true;
}

void FlutterWindow::OnDestroy() {
  if (flutter_controller_) {
    flutter_controller_ = nullptr;
  }

  Win32Window::OnDestroy();
}

LRESULT
FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                              WPARAM const wparam,
                              LPARAM const lparam) noexcept {
  // Give Flutter, including plugins, an opportunity to handle window messages.
  if (flutter_controller_) {
    std::optional<LRESULT> result =
        flutter_controller_->HandleTopLevelWindowProc(hwnd, message, wparam,
                                                      lparam);
    if (result) {
      return *result;
    }
  }

  switch (message) {
    case WM_FONTCHANGE:
      flutter_controller_->engine()->ReloadSystemFonts();
      break;
    case WM_GETMINMAXINFO: {
      // Enforce the current mode's layout floor (DPI-scaled) so a manual
      // un-maximize + drag can't shrink the shell into overflow.
      MINMAXINFO* info = reinterpret_cast<MINMAXINFO*>(lparam);
      UINT dpi = FlutterDesktopGetDpiForHWND(hwnd);
      double scale = dpi / 96.0;
      info->ptMinTrackSize.x = static_cast<LONG>(min_size_.x * scale);
      info->ptMinTrackSize.y = static_cast<LONG>(min_size_.y * scale);
      return 0;
    }
  }

  return Win32Window::MessageHandler(hwnd, message, wparam, lparam);
}
