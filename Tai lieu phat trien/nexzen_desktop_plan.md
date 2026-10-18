# Kế hoạch triển khai: Nexzen Browser - Desktop Edition (C# + Avalonia)

Dự án đưa trình duyệt Nexzen lên nền tảng Desktop (Windows, macOS, Linux) là một bước tiến rất lớn và đầy tiềm năng. Bản nháp 7 phase bạn đưa ra cực kỳ logic, có cấu trúc chặt chẽ và đi đúng vào trọng tâm của việc phát triển một trình duyệt hiện đại đa nền tảng.

Dưới đây là kế hoạch sơ bộ đã được tinh chỉnh, bổ sung thêm các phân tích kỹ thuật chuyên sâu và các rủi ro cần giải quyết.

## User Review Required

> [!CAUTION]  
> **Rủi ro lớn nhất nằm ở Phase 3 (Browser Engine - Gecko):** Khác với Android có sẵn `GeckoView` được Mozilla hỗ trợ chính thức, trên Desktop **không có thư viện nhúng Gecko hiện đại nào được hỗ trợ chính thức cho .NET/C#**. Dự án `GeckoFX` trước đây đã bị bỏ hoang từ rất lâu và chỉ chạy trên WinForms/Windows cũ. Việc tự đóng gói Gecko (biên dịch Firefox từ mã nguồn C++ và viết wrapper cho C#/Avalonia) là một khối lượng công việc khổng lồ, phức tạp vô cùng. 
> 
> **Đề xuất thay thế (Khuyến nghị cao):** Chúng ta nên cân nhắc sử dụng **Chromium (CEF)** thông qua `CefNet` hoặc `CefGlue` cho Avalonia, HOẶC dùng **System WebView** (Edge WebView2 trên Windows, WebKit trên macOS/Linux qua thư viện `Photino` / `Avalonia.WebView`). Điều này sẽ tiết kiệm hàng nghìn giờ làm việc mà vẫn giữ được hiệu năng cực tốt. Bạn hãy cân nhắc kỹ điều này nhé.

## Open Questions

> [!IMPORTANT]  
> 1. **Engine:** Bạn có bắt buộc phải giữ lại Gecko Core bằng mọi giá không? Nếu có, dự án sẽ kéo dài thêm rất nhiều tháng chỉ để build engine. Nếu linh hoạt chuyển sang CEF (Chromium), tiến độ sẽ nhanh hơn gấp nhiều lần. Ý kiến của bạn thế nào?
> 2. **Đồng bộ dữ liệu:** Bạn có muốn lên kế hoạch xây dựng tính năng Sync (Đồng bộ Bookmark, History, Tab) giữa bản Android và Desktop thông qua một Backend/Cloud server riêng không? Hay trước mắt dữ liệu ai nấy giữ?

---

## Chi tiết kế hoạch triển khai (8 Phases)

### Phase 0: Gecko Engine Proof of Concept (PoC)
*Cô lập và xử lý rủi ro lớn nhất trước khi bắt đầu viết UI.*
Sử dụng lộ trình PoC (Proof of Concept) sau theo chiến lược "Chia để trị":
1. **C++ Gecko Core:** Viết thư viện C++ (`.dll` / `.so`) để khởi tạo Gecko engine hoàn toàn độc lập với C#.
2. **Native Window:** Thư viện C++ tự tạo một cửa sổ Native (HWND/X11) và render giao diện web lên đó.
3. **Load URL:** Thử nghiệm gọi lệnh tải `https://example.com` bằng C++ để xác nhận Engine hoạt động.
4. **C# P/Invoke:** Expose các hàm C đơn giản từ thư viện C++ và dùng `[DllImport]` bên C# để gọi.
5. **Avalonia NativeControlHost:** Dùng `NativeControlHost` của Avalonia để cấp một vùng UI (truyền Handle IntPtr xuống C++) để Gecko vẽ thẳng lên giao diện Avalonia.
*(Chỉ khi Phase 0 thành công, chúng ta mới tiếp tục các Phase tiếp theo. Nếu thất bại, chuyển sang phương án dự phòng CEF).*

### Phase 1: Shared Architecture & Core Models (C# .NET 8)
*Tách biệt hoàn toàn Logic khỏi Android.*
- Thiết lập giải pháp **.NET 8 (hoặc 9)** với kiến trúc Clean Architecture.
- Chuyển đổi mã nguồn (port từ Kotlin sang C#) các Core Modules:
  - `Database`: Sử dụng Entity Framework Core (SQLite) cho Bookmark, History.
  - `Tab Model` & `Browser State`: Quản lý danh sách Tab, trạng thái duyệt web.
  - `Adblock Engine`: Port bộ lọc filter list, parser.
  - `Settings`: Quản lý cấu hình JSON cục bộ.

### Phase 2: Desktop Shell & Framework Setup
*Thiết lập hạ tầng UI đa nền tảng.*
- **Công nghệ:** C# + Avalonia UI (Sử dụng MVVM với CommunityToolkit.Mvvm).
- Tạo base project hỗ trợ build cho 3 nền tảng (Windows, macOS, Linux).
- Thiết lập Window Management: Custom Titlebar (viền cửa sổ tùy chỉnh giống Chrome/Edge), hỗ trợ Dark/Light mode tự động theo hệ điều hành.

### Phase 3: Browser Engine Integration
*Kết nối chặt chẽ Engine (đã chứng minh ở Phase 0) với UI của Avalonia.*
- Xây dựng hệ thống IPC (Inter-Process Communication) nếu chạy đa tiến trình.
- Khởi tạo `IBrowserEngine` interface để trừu tượng hóa các API: `LoadUrl()`, `GoBack()`, `EvaluateJavascript()`, `InjectCss()`.
- Móc nối (Hook) vào Network Layer của Gecko C++ để chặn Request (phục vụ cho Adblock) và quản lý Cookie/Session.
- Xây dựng JS Bridge để C# và Javascript trên web có thể giao tiếp hai chiều.

### Phase 4: Nexzen UI (Porting)
*Mang thiết kế tuyệt đẹp của Nexzen Android lên Desktop.*
- Xây dựng **Tab Bar**: Các tab trên cùng (giống Chrome), hỗ trợ kéo thả, hiệu ứng chuyển tab, pin tab, lock tab.
- Xây dựng **Address Bar (Omnibox)**: Thanh địa chỉ thông minh, gợi ý history/bookmark, tích hợp công cụ tìm kiếm.
- Xây dựng **Page Properties Menu** (Port từ bản Android): Tích hợp đầy đủ các tính năng:
  - Zoom in/out, Toggle Theme, Desktop View.
  - Quản lý History, Download, Bookmark, Extensions, Passwords.
  - Tính năng trang: Translate, DevTools (Inspect Element), Find in Page.
  - Công cụ nhanh: Camera Filter, Share/NexShare, Add to Shortcut, Save PDF.
  - Công cụ dọn dẹp: Clear Site Data.
  - Media & Crawl: Download/View Video, Image Crawler.
- Xây dựng **Side Panels**: Panel quản lý Bookmark, History, Download trượt từ bên trái/phải.
- Xây dựng **Bottom Controls/Status Bar**: Thanh trạng thái hiển thị URL khi hover.

### Phase 5: Desktop-specific Features
*Tận dụng thế mạnh của bàn phím và môi trường Desktop.*
- **Keyboard Shortcuts:** Ctrl+T (New Tab), Ctrl+W (Close Tab), Ctrl+L (Focus Address bar), Ctrl+Shift+T (Reopen Tab), Ctrl+Tab (Switch Tabs).
- **Multi-window:** Hỗ trợ kéo 1 Tab ra ngoài để tạo Window mới, và gộp lại.
- **Native Integration:** 
  - Dialog File Picker (chọn file tải xuống/upload).
  - Context menu (nhấp chuột phải vào web để hiện menu).
  - System Tray (chạy ngầm).
  - Native Notifications (Thông báo tải xong, v.v.).

### Phase 6: Advanced Nexzen (Các tính năng "Vũ khí bí mật")
*Phát triển các tính năng độc quyền tạo nên sự khác biệt.*
- **Adblock (Nâng cao):** Tối ưu hóa hiệu năng lọc request trên môi trường Desktop.
- **Video Downloader & Image Crawler:** Phân tích DOM, bắt HTTP requests media, cho phép người dùng bulk-download.
- **Tools tích hợp:** Quét/Tạo mã QR, OCR (Trích xuất văn bản từ hình ảnh trên web), PIP (Picture in Picture) cho video.
- **Custom Site Menu:** Script tiêm vào các trang phổ biến (như YouTube, Facebook) để thêm tính năng.
- **Dynamic Weather Effects (Hiệu ứng thời tiết):** Port hệ thống vẽ hiệu ứng thời tiết (`WeatherOverlayView`, `WeatherEffectController`) lên Desktop. Giao diện sẽ hiển thị các animation sinh động (như sương mù bay, mưa rơi, chớp giật màn hình) chạy đè lên toàn màn hình dựa theo thời tiết thực tế tại khu vực của người dùng.
- **Hệ sinh thái NexShare (Porting P2P Core):** Hệ thống chia sẻ nội bộ cực mạnh của Nexzen (tương tự AirDrop/Snapdrop) gồm:
  - *Discovery (Phát hiện):* mDNS (LAN), Bluetooth Low Energy (BLE), và quét QR Code/Link.
  - *Transport (Truyền tải):* P2P đa giao thức: LAN WebSocket, Wi-Fi Direct, và WebRTC DataChannel.
  - *Security:* Mã hóa E2EE (End-to-End Encryption) và xác thực thiết bị bằng mã SAS (Short Authentication String).
  - *Features:* Handoff (chuyển URL qua lại giữa PC-Mobile), nhắn tin LAN (Chat), chia sẻ file tốc độ cao qua Local HTTP Server (`NexShareWebHttpServer`). Điểm đặc biệt của kiến trúc này là máy tính không cài Nexzen vẫn có thể kết nối vào qua Web Access.

### Phase 7: Packaging, CI/CD & Distribution
*Phân phối đến tay người dùng cuối.*
- Đóng gói **Windows**: `.exe` hoặc MSIX installer.
- Đóng gói **macOS**: `.dmg` (Hỗ trợ Apple Silicon ARM64 và Intel x64), tích hợp ký chứng chỉ (Notarization).
- Đóng gói **Linux**: `.AppImage`, `.deb`, `.rpm`.
- **CI/CD:** Xây dựng luồng GitHub Actions tự động build, test và release các bản nightly / stable cross-platform. Tự động kiểm tra lỗ hổng bảo mật.
