# Báo Cáo Kỹ Thuật Chi Tiết NexShare

Báo cáo này trình bày chi tiết về kiến trúc, luồng hoạt động, cơ chế bảo mật và truyền tải của tính năng **NexShare** (một thành phần chia sẻ file và chat ngang hàng) bên trong NexZen Browser.

---

## 1. Cách Hoạt Động Chung
NexShare là hệ thống chia sẻ file và nhắn tin cục bộ đa nền tảng (Android - Android, Android - PC). 
Hệ thống này cho phép:
- Tìm kiếm thiết bị xung quanh thông qua mạng LAN, Bluetooth Low Energy (BLE), hoặc mDNS.
- Kết nối tới PC thông qua WebRTC / WebSocket Signaling Server (bằng cách quét mã QR).
- Chat và truyền file trực tiếp giữa các thiết bị với nhau.
- Tự động duy trì và khôi phục kết nối khi rớt mạng.
- Đảm bảo an toàn thông qua cơ chế mã hoá đầu cuối (End-to-End Encryption - E2E).

---

## 2. Các Bước Thực Hiện Code & Nhiệm Vụ Của Từng File
Mã nguồn của NexShare được tổ chức theo kiến trúc phân lớp, tách biệt rõ ràng giữa Giao diện (UI), Điều phối (Orchestrator), Giao thức (Protocol), Khám phá (Discovery) và Truyền tải file (File Transfer).

### Các File Chính Và Nhiệm Vụ:
- **`NexShareOrchestrator.kt`**: "Bộ não" trung tâm của NexShare. Chịu trách nhiệm quản lý state machine, điều phối các transport (WebSocket, WebRTC), gọi các module Discovery và kích hoạt các quá trình gửi/nhận file hoặc tin nhắn.
- **`NexShareProtocol.kt`**: Lớp định nghĩa giao thức giao tiếp. Chịu trách nhiệm encode/decode các gói tin JSON (như `DISCOVERY`, `HELLO`, `TEXT`, `FILE_OFFER`, `FILE_CHUNK`, v.v.) thành các object tương ứng (`WireEnvelope`).
- **`NexShareCrypto.kt`**: Module chịu trách nhiệm mã hóa và bảo mật. Cung cấp các hàm tạo khóa phiên (session key) qua HKDF, mã hóa/giải mã AES/GCM và tạo mã xác nhận SAS (Short Authentication String).
- **`NexShareFileTransferManager.kt`**: Quản lý toàn bộ luồng truyền file (nhận yêu cầu offer, chia file thành chunk hoặc gọi HTTP server, lưu trữ file nhận).
- **Nhóm Discovery (Tìm thiết bị)**:
  - `LanDiscovery.kt`: Phát UDP broadcast trên mạng LAN để tìm thiết bị.
  - `NexShareMdnsManager.kt`: Sử dụng mDNS (NSD) để quảng bá và tìm kiếm.
  - `NexShareBleDiscovery.kt`: Sử dụng Bluetooth LE để tìm các thiết bị gần (phục vụ tính năng NexDrop).
- **Nhóm Transport (Vận chuyển)**:
  - `WebSocketSignalingClient.kt`: Giao tiếp với Signaling Server để kết nối với Web PC.
  - `GeckoWebRtcTransport.kt`: Kênh truyền WebRTC DataChannel (kết nối trực tiếp qua trình duyệt PC).
  - `LanWebSocketClientTransport.kt` / `LanWebSocketServer.kt`: Kênh WebSocket trực tiếp trên mạng LAN.
- **Nhóm File Server/Client**:
  - `LanFileHttpServer.kt`: HTTP server siêu nhẹ chạy ở background để host file cần gửi.
  - `LanFileDownloader.kt`: HTTP client để kéo file tốc độ cao từ Server của người gửi.
  - `ChunkFileStore.kt`: Quản lý việc ghi các "mảnh" (chunk) file khi phải truyền qua WebSocket/WebRTC.
- **`NexShareReconnectManager.kt`**: Quản lý bộ đếm và vòng lặp tự động kết nối lại khi mất mạng.

---

## 3. Cơ Chế Truyền File
NexShare hỗ trợ 2 phương thức truyền tải file tùy thuộc vào hoàn cảnh kết nối:

1. **Truyền tốc độ cao qua HTTP (LAN HTTP File Transfer)**
   - *Khi nào dùng:* Khi 2 thiết bị nằm trong cùng mạng LAN và có thể ping trực tiếp địa chỉ IP của nhau (có `httpPort > 0`).
   - *Cách hoạt động:* Thiết bị gửi khởi tạo một `LanFileHttpServer` và host file đó ở một URL (chứa `transferId`). Thiết bị nhận sau khi đồng ý (`FILE_ACCEPT`) sẽ dùng `LanFileDownloader` để gọi HTTP GET và tải file về máy ở tốc độ mạng cục bộ cao nhất.
   
2. **Truyền dạng mảnh qua DataChannel (Chunking)**
   - *Khi nào dùng:* Khi kết nối qua WebRTC (với PC) hoặc không thể gọi HTTP trực tiếp.
   - *Cách hoạt động:* File được chia nhỏ thành các byte arrays. Sender gửi lần lượt các gói `FILE_CHUNK`. Receiver nhận các gói này, giải mã (nếu có E2E) và ghi nối vào file thông qua `ChunkFileStore`. Sau khi ghi đủ kích thước, gửi gói `FILE_DONE`.

---

## 4. Cơ Chế Phát Hiện Thiết Bị (Device Discovery)
NexShare tìm kiếm thiết bị bằng cách phối hợp đa cơ chế để tăng tỷ lệ thành công:
- **LAN UDP Broadcast (`LanDiscovery.kt`)**: Gửi gói JSON `DISCOVERY` qua port 43334. Gói này đính kèm timestamp và chữ ký HMAC-SHA256 (bằng `deviceId`) để chống Replay Attack và Spoofing.
- **mDNS/NSD**: Quảng bá dịch vụ dưới dạng `_nexshare._tcp` trong mạng nội bộ. Thiết bị khác sẽ lắng nghe các dịch vụ này để lấy được IP và port nội bộ.
- **Bluetooth LE (`NexShareBleDiscovery.kt`)**: Dùng cho cự ly gần (NexDrop), thiết bị phát ra các gói BLE Advertisements mang theo metadata cơ bản để định danh mà không cần chung mạng WiFi.

---

## 5. Cơ Chế Kết Nối Thiết Bị
- **Handshake (Bắt tay):** Sau khi tìm thấy nhau (hoặc quét mã QR có chứa WebRTC Signaling ID), thiết bị khởi tạo kết nối (TCP WebSocket hoặc WebRTC).
- **Đàm phán danh tính:** Gói `HELLO` hoặc `ECDH_HELLO` được trao đổi để gửi `deviceId`, `deviceName`.
- **Duyệt Session:** Trong trường hợp kết nối P2P (như Wifi Direct), sẽ có thông báo xác nhận ghép nối (pairing). Các gói `SESSION_JOIN`, `SESSION_DECISION` được gọi để người dùng Accept/Reject.

---

## 6. Cơ Chế Giữ Kết Nối & Kết Nối Lại (Keep-Alive & Reconnect)
Duy trì kết nối ổn định là yếu tố tiên quyết của NexShare:
- **Reconnect Manager (`NexShareReconnectManager.kt`)**: 
  - Nếu kết nối đột ngột bị ngắt, Manager kích hoạt một vòng lặp reconnect 2 giây/lần.
  - Tối đa thử lại **12 lần** trước khi bỏ cuộc.
  - Sử dụng thông tin của thiết bị gần nhất (`lastPeerHint`) được lưu trong `NexShareLastPeerStore` (gồm IP và Port cũ).
- **Discovery Triggered Reconnect**: Nếu `LanDiscovery` đột nhiên bắt được sóng UDP từ thiết bị cũ đang bị mất kết nối, hệ thống sẽ chèn ngang và lập tức gọi `connectToDevice` để tái thiết lập kết nối ngay lập tức mà không chờ vòng lặp timer.

---

## 7. Cơ Chế Bảo Mật & Mã Hoá
Dữ liệu của người dùng được bảo vệ ở nhiều cấp độ:
- **Xác thực Discovery**: Các gói tin dò tìm LAN luôn đi kèm mã băm HMAC-SHA256. Nếu quá hạn 30 giây hoặc sai chữ ký, gói bị loại bỏ.
- **Mã hoá đầu cuối (E2E) bằng AES-GCM (`NexShareCrypto.kt`)**:
  - Dữ liệu chat (gói `TEXT`) và các nội dung nhạy cảm được mã hóa theo chuẩn `AES/GCM/NoPadding`.
  - Mỗi tin nhắn đi kèm một `nonce` 12-byte ngẫu nhiên.
  - Sử dụng Additional Authenticated Data (AAD) chứa `peerId` để chống tráo đổi tin nhắn giữa các phiên.
- **Sinh khoá phiên (Session Key Derivation)**: 
  - Khoá E2E được tạo ra thông qua thuật toán HKDF (HMAC-based Extract-and-Expand Key Derivation Function) với SHA-256 từ mã bí mật chia sẻ (shared secret/token).
- **Bảo mật Wifi Direct (SAS)**: 
  - Ứng dụng mô hình Trao đổi khoá Elliptic-curve Diffie–Hellman (ECDH). Từ Public Key và Nonce của 2 bên, tạo ra một mã xác nhận ngắn (SAS - Short Authentication String) 6 chữ số bằng hàm băm SHA-256, yêu cầu người dùng so sánh 2 màn hình để tránh tấn công Người đứng giữa (Man-In-The-Middle).
