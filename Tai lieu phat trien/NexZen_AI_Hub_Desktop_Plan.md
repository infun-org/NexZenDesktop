# NexZen Desktop — AI Hub Implementation Plan

**Phiên bản:** 0.1 — bản kế hoạch để review  
**Ngày:** 17/10/2026  
**Phạm vi:** NexZen Desktop (C#/.NET, Avalonia, CEF)  
**Mục tiêu:** Cho phép người dùng tự cấu hình API key của nhà cung cấp AI để tóm tắt trang web và hỏi đáp dựa trên nội dung trang, không yêu cầu NexZen vận hành máy chủ AI trung gian.

---

## 1. Mục tiêu và nguyên tắc

### Mục tiêu bản đầu tiên (MVP)

- Tóm tắt nội dung chính của trang đang mở.
- Hỏi đáp về trang hiện tại và yêu cầu AI trích dẫn đoạn liên quan.
- Cho người dùng tự nhập API key, chọn provider và model.
- Hỗ trợ streaming phản hồi, hủy yêu cầu và hiển thị lỗi dễ hiểu.
- Giữ API key trong kho bảo mật của hệ điều hành khi khả dụng.
- Không gửi dữ liệu đến AI cho đến khi người dùng chủ động yêu cầu.
- Không cần tài khoản NexZen hoặc backend proxy của NexZen.

### Nguyên tắc thiết kế

1. **Provider-agnostic:** tính năng UI và xử lý nội dung không phụ thuộc một nhà cung cấp cụ thể.
2. **Privacy-first:** hiển thị rõ dữ liệu nào sẽ được gửi ra ngoài máy và đến provider nào.
3. **Content-first:** trích xuất, làm sạch và giới hạn nội dung trước khi gọi model.
4. **Secure by default:** không đưa API key vào JavaScript của trang, URL, log hoặc thông báo lỗi.
5. **Phát triển theo lát cắt dọc:** hoàn thành một luồng chạy được từ trang web đến câu trả lời trước khi mở rộng nhiều provider.

## 2. Phạm vi tính năng

| Tính năng | MVP | Ghi chú |
|---|---|---|
| Cấu hình provider, API endpoint, API key và model | Có | Cho phép kiểm tra kết nối trước khi lưu |
| OpenAI-compatible API | Có | Adapter đầu tiên; không giả định mọi dịch vụ tương thích hoàn toàn |
| OpenAI API adapter riêng | Có hoặc ngay sau MVP | Thêm khi cần tính năng/định dạng riêng của OpenAI |
| Gemini API adapter | Sau MVP | Adapter riêng do API và xác thực khác biệt |
| OpenRouter adapter | Sau MVP | Provider tùy chọn; hỗ trợ chọn model qua một API |
| Tóm tắt trang | Có | Tóm tắt, ý chính, kết luận; chọn ngôn ngữ đầu ra |
| Hỏi đáp theo nội dung trang | Có | Trả lời dựa trên nguồn đã trích xuất; nêu khi không tìm thấy bằng chứng |
| Side Panel trong Avalonia | Có | Không nhúng giao diện AI vào DOM của trang web |
| Streaming, hủy yêu cầu, timeout | Có | Cập nhật phản hồi từng phần nếu provider hỗ trợ |
| Lịch sử hội thoại | Tối giản | Theo tab/phiên; mặc định không lưu nội dung nhạy cảm lâu dài |
| Phân tích bình luận diễn đàn | Chưa | Giai đoạn sau: phân loại quan điểm và thống kê có dẫn chứng |
| Tự động gửi trang cho AI | Không | Chỉ gửi sau thao tác rõ ràng của người dùng |
| Local model/offline | Không thuộc MVP | Có thể bổ sung provider local về sau |

## 3. Kiến trúc đề xuất

```text
NexZen.Desktop (Avalonia)
├── Browser / CEF
│   └── Page Content Bridge
├── AI Side Panel
└── NexZen.AI
    ├── Core
    │   ├── IAIProvider
    │   ├── ChatRequest / ChatResponse
    │   ├── Streaming events / cancellation
    │   └── Provider capabilities
    ├── Providers
    │   ├── OpenAI-compatible
    │   ├── OpenAI
    │   ├── Gemini (later)
    │   └── OpenRouter (later)
    ├── Content
    │   ├── PageContentExtractor
    │   ├── ContentCleaner
    │   ├── ContentChunker
    │   └── SourceSegment / citation mapping
    ├── Security
    │   ├── ISecretStore
    │   └── OS-backed secret storage adapters
    └── Settings / Usage
        ├── Provider configuration (without plaintext secrets)
        ├── Request limits
        └── Token/cost estimates where possible
```

### Ranh giới giữa các thành phần

- **CEF bridge:** chỉ lấy nội dung trang theo yêu cầu của ứng dụng; không bao giờ nhận API key.
- **Content extractor:** trả về title, URL, main text và các đoạn có nguồn gốc rõ ràng. Ưu tiên trích xuất bài viết chính; có fallback sang `document.body.innerText` đã giới hạn khi cần.
- **AI Core:** cung cấp hợp đồng chung, không chứa logic UI hoặc mã riêng của provider.
- **Provider adapter:** xử lý endpoint, xác thực, request/response, streaming, timeout và lỗi riêng của dịch vụ.
- **Secret store:** chỉ lưu bí mật; file cấu hình thông thường chỉ lưu provider, model, endpoint và tham chiếu secret.
- **UI:** hiển thị trạng thái gửi dữ liệu, provider/model hiện tại, tiến trình, lỗi và nội dung trả lời.

## 4. Kế hoạch theo giai đoạn

Ước lượng dành cho một người phát triển đã quen codebase. Đây là ước lượng ban đầu, cần hiệu chỉnh sau khi kiểm tra cấu trúc dự án hiện tại.

| Giai đoạn | Công việc | Kết quả bàn giao | Ước lượng |
|---|---|---|---:|
| 0. Khảo sát | Kiểm tra cấu trúc NexZen Desktop, CEF bridge, settings, logging và cách đóng gói | Ghi chú điểm tích hợp và quyết định kỹ thuật | 0,5–1 ngày |
| 1. AI Core | Định nghĩa interface provider, DTO, streaming, cancellation, lỗi chuẩn hóa | Unit-test cho hợp đồng và luồng lỗi | 1–2 ngày |
| 2. Provider đầu tiên | Gọi OpenAI-compatible endpoint; cấu hình endpoint/model/key; test kết nối | Có thể gửi prompt thử và nhận câu trả lời | 1–2 ngày |
| 3. Quản lý key | Tích hợp secret store của hệ điều hành; xử lý trường hợp không có kho bảo mật | Key không lưu plaintext trong settings/log | 1–2 ngày |
| 4. Trích xuất trang | Lấy title/URL/text từ CEF; làm sạch, giới hạn kích thước và giữ nguồn đoạn | Extractor có test với nhiều kiểu trang | 2–3 ngày |
| 5. Tóm tắt trang | Prompt tóm tắt có cấu trúc; streaming; chọn ngôn ngữ; giới hạn đầu vào | Luồng end-to-end “Summarize page” | 1–2 ngày |
| 6. Hỏi đáp trang | Gửi nội dung liên quan, quản lý lượt hội thoại, yêu cầu trích dẫn nguồn | Luồng “Ask this page” | 2–3 ngày |
| 7. UI/UX | AI Side Panel, cấu hình provider, loading, stop, retry và lỗi thân thiện | UI hoàn chỉnh cho MVP | 2–3 ngày |
| 8. An toàn & chi phí | Cảnh báo dữ liệu gửi ra ngoài, giới hạn nội dung/token, ước tính chi phí, chống prompt injection | Kiểm thử bảo mật và UX | 1–2 ngày |
| 9. Kiểm thử & đóng gói | Kiểm thử Linux/Windows trước; kiểm tra key, streaming, lỗi mạng và build release | Bản MVP có thể phát hành thử | 2–3 ngày |

**Tổng sơ bộ:** khoảng 13–21 ngày làm việc, tùy mức độ hoàn thiện hiện tại của CEF bridge và UI. Chưa bao gồm kiểm thử macOS chuyên sâu hoặc phân tích bình luận quy mô lớn.

## 5. Thiết kế trải nghiệm người dùng

### AI Side Panel

- Nút mở AI ở toolbar hoặc menu tab.
- Header hiển thị provider và model đang chọn.
- Các hành động nhanh: **Tóm tắt trang**, **Hỏi về trang**, **Dịch nội dung**.
- Ô chat hỗ trợ câu hỏi tiếp nối.
- Hiển thị trạng thái: đang trích xuất, đang gửi, đang tạo câu trả lời.
- Có nút dừng, thử lại và sao chép kết quả.
- Các trích dẫn liên kết về đoạn nguồn nếu có thể ánh xạ đáng tin cậy.

### Cấu hình AI

Các trường tối thiểu:

- Provider.
- API base URL (chỉ hiện khi provider cho phép tùy chỉnh).
- API key.
- Model ID.
- Nút **Test connection**.
- Giới hạn nội dung gửi mỗi yêu cầu.
- Ngôn ngữ đầu ra mặc định.
- Tùy chọn xóa key và lịch sử AI.

Không hiển thị key đầy đủ sau khi lưu; chỉ cho phép thay thế hoặc xóa.

## 6. Quy tắc xử lý nội dung

1. Chỉ trích xuất khi người dùng bấm một hành động AI.
2. Lấy title, URL và nội dung chính; loại menu, footer, script, quảng cáo và phần lặp khi có thể.
3. Chuẩn hóa khoảng trắng và loại nội dung trùng.
4. Đặt giới hạn ký tự/token theo model và theo thiết lập người dùng.
5. Với trang dài, chia đoạn và tổng hợp theo nhiều bước; không cắt âm thầm phần cuối mà không thông báo.
6. Hỏi đáp cần chỉ rõ câu trả lời dựa trên nội dung đã cung cấp; nếu không có bằng chứng, nói rõ không tìm thấy trong trang.
7. Nội dung trang được xem là dữ liệu không đáng tin cậy. Không làm theo chỉ dẫn nằm trong trang yêu cầu tiết lộ key, thay đổi cấu hình hoặc thực hiện hành động ngoài tác vụ người dùng yêu cầu.
8. Không coi số lượt thích hoặc một tập bình luận nhỏ là bằng chứng đại diện cho toàn bộ ý kiến cộng đồng.

## 7. Bảo mật, quyền riêng tư và chi phí

- Ưu tiên Windows Credential Manager/DPAPI, macOS Keychain và Secret Service/KWallet trên Linux thông qua lớp trừu tượng phù hợp. Cần kiểm tra khả năng truy cập và hành vi khi desktop session không có keyring.
- Nếu kho bảo mật không khả dụng, mặc định yêu cầu người dùng nhập lại key trong phiên; chỉ cung cấp fallback lưu lâu dài khi đã có thiết kế bảo vệ rõ ràng và cảnh báo minh bạch.
- Không ghi API key, request header nhạy cảm hoặc toàn bộ nội dung trang vào log.
- Không gửi request tự động khi điều hướng trang, refresh hoặc mở tab.
- Hiển thị provider nhận dữ liệu và nhắc người dùng không gửi nội dung riêng tư nếu không muốn chia sẻ với provider đó.
- Có timeout, cancellation, giới hạn kích thước request và xử lý rate limit.
- Ước tính token/chi phí chỉ mang tính tham khảo; giá và cách tính phụ thuộc provider/model, vì vậy không hứa một mức giá chung.
- Không dùng telemetry để thu thập API key, prompt hoặc nội dung trang.
- Chỉ cho phép endpoint tùy chỉnh theo lựa chọn rõ ràng; cân nhắc cảnh báo với HTTP không mã hóa và endpoint localhost/LAN.

## 8. Kiểm thử và tiêu chí nghiệm thu

### Chức năng

- [ ] Người dùng thêm, kiểm tra, thay thế và xóa cấu hình provider.
- [ ] API key không xuất hiện trong file settings, log, URL hoặc JavaScript của trang.
- [ ] Tóm tắt được bài viết tiếng Việt và tiếng Anh.
- [ ] Hỏi đáp trả lời theo nội dung trang và không bịa trích dẫn.
- [ ] Streaming, dừng, timeout, retry và rate limit hoạt động đúng.
- [ ] Trang không có bài viết chính vẫn có thông báo/fallback hợp lý.
- [ ] Trang rất dài được giới hạn/chia đoạn minh bạch.
- [ ] Người dùng biết khi nội dung sắp được gửi đến nhà cung cấp bên ngoài.

### Chất lượng và hiệu năng

- [ ] Tạo bộ test tối thiểu gồm tin tức, blog, tài liệu kỹ thuật, trang nhiều menu và trang nhiều bình luận.
- [ ] Đánh giá độ đúng, độ bao phủ ý chính, ngôn ngữ đầu ra và tính trung thực của trích dẫn.
- [ ] Đo thời gian trích xuất, thời gian nhận token đầu tiên và thời gian hoàn tất.
- [ ] Kiểm tra hủy yêu cầu không làm treo UI hoặc để request chạy ngoài ý muốn.
- [ ] Xác minh app vẫn dùng được khi không có API key hoặc provider đang lỗi.

### Nền tảng

- [ ] Linux: kiểm thử cấu hình desktop hiện tại, keyring và đóng gói.
- [ ] Windows: kiểm thử secure storage, streaming và release build.
- [ ] macOS: đưa vào vòng xác nhận trước khi tuyên bố hỗ trợ chính thức.

## 9. Ngoài phạm vi MVP

- Phân tích hàng nghìn bình luận với thống kê đa số/thiểu số.
- Tự động đọc nhiều tab hoặc gửi hàng loạt trang cho AI.
- Đồng bộ API key giữa thiết bị.
- Tài khoản NexZen, backend proxy hoặc hệ thống thanh toán AI.
- Local LLM và tải model nhiều GB.
- Extension/agent tự thao tác website.

## 10. Quyết định cần chốt trước khi bắt đầu code

| Quyết định | Đề xuất ban đầu |
|---|---|
| Provider đầu tiên | OpenAI-compatible API |
| Giao diện | Avalonia Side Panel |
| Trích xuất | CEF bridge theo yêu cầu người dùng |
| Lưu key | Kho bảo mật hệ điều hành qua `ISecretStore` |
| Lưu lịch sử | Tắt lưu bền vững mặc định hoặc chỉ lưu tối thiểu, có tùy chọn rõ ràng |
| Gọi AI | Chỉ theo thao tác người dùng |
| Phạm vi nền tảng đầu tiên | Linux và Windows; macOS xác nhận sau |
| Phân tích bình luận | Để giai đoạn 2 sau khi MVP tóm tắt/hỏi đáp ổn định |

## 11. Trình tự triển khai đề xuất

1. Khảo sát codebase hiện tại và tìm điểm tích hợp CEF/toolbar/side panel.
2. Tạo `NexZen.AI.Core` và mock provider để phát triển UI không phụ thuộc mạng.
3. Hoàn thiện một provider thật cùng lưu key an toàn.
4. Làm luồng tóm tắt end-to-end trước.
5. Thêm hỏi đáp theo trang và nguồn trích dẫn.
6. Kiểm thử quyền riêng tư, giới hạn chi phí và nhiều kiểu website.
7. Sau MVP mới bổ sung Gemini, OpenRouter và phân tích bình luận.

---

**Trạng thái:** Bản đề xuất để review; chưa phải đặc tả đã được chốt. Các ước lượng cần điều chỉnh sau khi khảo sát codebase NexZen Desktop hiện tại.
