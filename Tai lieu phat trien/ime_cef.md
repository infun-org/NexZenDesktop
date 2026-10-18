# Goal Description

Kế hoạch này nhằm giải quyết triệt để lỗi không gõ được tiếng Việt (và các ngôn ngữ có dấu khác) trong trình duyệt nhúng CefGlue (Chromium) trên Linux. Bằng cách can thiệp sâu vào tầng giao tiếp IME (Input Method Editor) giữa Avalonia và Chromium ở chế độ Off-Screen Rendering (OSR).

## User Review Required

> [!WARNING]
> Phương pháp này đòi hỏi phải viết thêm một module `TextInputMethodClient` riêng cho CefGlue. Việc này khá phức tạp vì đòi hỏi phải đồng bộ trạng thái gõ chữ (composition) giữa hệ điều hành Linux (thông qua Avalonia) và engine Chromium. Quá trình này có thể gây ra một số lỗi rủi ro như: popup bộ gõ hiện sai vị trí, chữ bị lặp đúp nếu gõ quá nhanh, hoặc app bị crash nếu Chromium không nhận đúng con trỏ. Bạn hãy xem kỹ phần phân tích độ phức tạp dưới đây trước khi chốt nhé!

## Open Questions

> [!IMPORTANT]
> Tính năng này chỉ thực sự quan trọng trên Linux. Trên Windows/macOS, kiến trúc IME hoạt động khác. Chúng ta sẽ chỉ kích hoạt module CefTextInputMethodClient này khi ứng dụng chạy trên Linux để tránh ảnh hưởng rủi ro (regression) tới các HĐH khác.

## Lộ trình triển khai (Implementation Phases)

Để đảm bảo tính năng không phá vỡ kiến trúc hiện tại, chúng ta sẽ thực thi theo 7 giai đoạn (Phases) sau:

### Phase 0 — Audit hiện trạng (Khảo sát hệ thống)
*Không code IME ngay.*
- **Kiểm tra Upstream (Research):** Trước khi viết dòng code nào, cần lướt qua GitHub của Xilium.CefGlue và các bản fork nổi bật (như của OutSystems) để xem đã có ai làm IME OSR cho Avalonia chưa.
- **Quyết định bản lề - Thang đo can thiệp OnImeCompositionRangeChanged:**
    - Sự kiện này nằm sâu trong `CefRenderHandler` (thuộc core). Việc expose nó ra ngoài cần tuân thủ thang đo từ rẻ đến đắt, tránh mang vác **Nợ Kỹ Thuật (Technical Debt)**:
    - *Bậc 1 (Lý tưởng nhất):* Đọc mã nguồn xem `BaseCefBrowser` có cung cấp hàm `protected virtual` nào để tạo `CefClient` / `CefRenderHandler` hay không. Nếu có, ta chỉ cần Subclass cái RenderHandler ngay trong code của project mình. **Không cần Fork bất cứ thứ gì.**
    - *Bậc 2 (Fork Wrapper):* Nếu không thể Subclass, ta chỉ fork dự án con `Xilium.CefGlue.Avalonia` (lớp vỏ). Đây là code thuần C#, nhỏ gọn, dễ rebase.
    - *Bậc 3 (Bất đắc dĩ):* Chỉ fork toàn bộ core `Xilium.CefGlue` nếu phát hiện core chưa implement hàm này. Việc fork Core đồng nghĩa với việc ta phải tự ôm trách nhiệm update version CEF và PInvoke bindings sau này.
    - *Kỷ luật thép khi Fork:* Nếu phải fork, giữ diff ở mức **Tối thiểu tuyệt đối** (chỉ sửa 1 file, bộc lộ 1 event duy nhất). Đẩy Pull Request (PR) ngược lên upstream sớm nhất có thể để tìm cơ hội gỡ bỏ bản fork.
- Xác định chính xác các phiên bản đang dùng: Avalonia, CEF, CefGlue, Xilium.CefGlue.Avalonia, OSR mode.
- Đặt Log (Trace) để theo dõi toàn bộ pipeline Keyboard hiện tại: `KeyDown`, `KeyUp`, `TextInput`, `TextInputMethodClientRequested`, `Focus`, `LostFocus`, `CEF SendKeyEvent`, và đặc biệt là **`CefBrowserHost.SetFocus`** (Focus phía Avalonia và CEF là 2 khái niệm khác biệt).
- **Rủi ro DBus Bất đồng bộ (Quan trọng nhất):** Avalonia gọi IBus qua DBus. Nếu DBus phản hồi chậm, Avalonia có thể dispatch `KeyDown` lên control *trước* khi nhận được tín hiệu "tôi ăn phím này rồi" từ IBus. Lúc đó `e.Handled` sẽ là `false` một cách giả mạo (gây ra bug bấm Backspace xoá cả preedit lẫn chữ thật).
- **Thử nghiệm độ trễ DBus:** Ở Phase 0, phải gõ cực nhanh một chuỗi tiếng Việt và log lại timestamp của chuỗi: `KeyDown(Handled=?) ➔ SetPreeditText ➔ TextInput`. Xác định xem `Handled` được set đồng bộ hay đến sau. Nếu đến sau, ghi lại độ trễ max.
- **Đo lường Baseline Độ trễ gõ (Typing Latency):** OSR + IPC + IME là 3 tầng chồng lên nhau. Cần có một con số đo lường độ trễ gõ phím hiện tại (trước khi lắp IME). Khi user báo "gõ hơi khựng", ta mới có cơ sở để biết đó là do code IME hay do cảm giác.
- **Chuẩn bị Plan B:** Nếu phát hiện DBus bị bất đồng bộ, Phase 2 sẽ phải thiết kế một **Event Buffer** (Giữ key event lại vài mili-giây, hoặc chờ tín hiệu từ preedit/commit rồi mới quyết định forward hay không).
- **Xác thực khả năng giao tiếp (DBus/Fcitx5):** Khảo sát xem phiên bản Avalonia hiện tại có giao tiếp DBus thành công với Fcitx5 hay không. Nếu bản Avalonia này chỉ hỗ trợ IBus, các test case Fcitx5 sẽ thất bại ngay từ đầu (không phải lỗi do code chúng ta). 
- **Lưu ý nền tảng Wayland:** Hiện tại Avalonia 11 chạy trên Wayland thông qua XWayland (sử dụng backend X11). Do đó, hành vi hiển thị popup trên Wayland có thể không chuẩn xác như native Wayland (ghi chú lại để không debug nhầm).
- **Mục tiêu:** Nắm rõ đường đi hiện tại của các ký tự vào Chromium, đo được baseline hiệu năng, và chốt phương án bắt tọa độ trước khi can thiệp.

### Phase 1 — Avalonia IME client tối thiểu (Bộ khung rỗng)
- **Cách ly hệ điều hành (OS Isolation):** Ngay từ khi đặt nền móng, tuyệt đối không dùng `if (RuntimeInformation.IsOSPlatform)` rải rác. Áp dụng Strategy Pattern / Null Object Pattern: tạo `NullTextInputMethodClient` cho Win/Mac. Đảm bảo code flow của Windows và macOS không bao giờ phải load class của Linux, tránh rò rỉ giả định platform (platform leakage) ngay từ đầu.
- **Kiểm soát sinh tử (IME Toggle):** Bắt buộc phải phát hiện DOM element hiện tại có editable hay không (đang ở ô `<input>` hay đang ở `<body>`). Giải pháp chính: Lắng nghe `CefRenderHandler.OnVirtualKeyboardRequested` để gọi `InputMethod.SetIsInputMethodEnabled(control, bool)`.
- **Plan B cho IME Toggle:** Cần verify xem việc gọi `SetIsInputMethodEnabled` khi control đang có sẵn focus có thực sự ép Avalonia query lại `TextInputMethodClientRequested` hay không. Nếu KHÔNG, ta phải chuyển sang Plan B: Giữ IME Client gắn vĩnh viễn vào control, nhưng làm cho nó trở nên "trơ" (inert) khi DOM không editable (Từ chối Preedit, trả về nguyên bản Key Event thô).
- Tạo class `CefTextInputMethodClient` kế thừa class trừu tượng `Avalonia.Input.TextInput.TextInputMethodClient`.
- Implement các property cơ bản: `SupportsPreedit = true`, `CursorRectangle`, `Selection`.
- **Đặc biệt lưu ý:** Bắt buộc thiết lập `SupportsSurroundingText = false`. Việc hỗ trợ Surrounding Text đòi hỏi query liên tục qua lại IPC, là "hố đen" hiệu năng. (Ghi chú: Việc tắt tính năng này sẽ đẩy một số bộ gõ như `ibus-bamboo` vào chế độ fallback dùng phím Backspace thay vì preedit. Phải đặc biệt chú ý test ở Phase 6).
- Cài đặt (override) hàm `SetPreeditText()` của lớp cha (chú ý: đây là method override, không phải event subscribe).
- (Chưa cần làm toạ độ hoàn hảo).
- **Mục tiêu:** Chạy thử để đảm bảo luồng `Linux IME ➔ Avalonia ➔ Client` thông suốt và bộ gõ chỉ hoạt động/có tác dụng khi bấm vào đúng ô nhập liệu.

### Phase 2 — CEF Composition (Kết nối lõi & Chống lặp chữ)
- Kết nối `SetPreeditText` ➔ `ImeSetComposition`.
- **Chi tiết Underline (Gạch chân):** Hàm `ImeSetComposition` yêu cầu mảng `CefCompositionUnderline`. Nếu truyền mảng rỗng, chữ gõ dở (preedit) sẽ không được gạch chân, gây bối rối cho user. Bắt buộc phải tạo 1 object underline phủ toàn bộ chiều dài preedit, và set `selectionRange` sao cho con trỏ (caret) nằm ở cuối chữ.
- Kết nối `TextInput` ➔ `ImeCommitText`.
- **IME State Machine (Idle / Composing / PendingCommit):** Xây dựng hệ thống trạng thái ngay từ Phase này để làm nền tảng rẽ nhánh. Khi IME bắn signal commit, state chuyển sang `PendingCommit` và đợi CEF xác nhận đã nhận text rồi mới quay về `Idle` (chống lật state khi gõ tốc độ cao).
- **Chốt quy tắc chặn Key Event (Hướng An Toàn - Safe Approach):**
    - Thay vì tự lọc hay tự bịa sự kiện (synthesize `keyCode = 229`), chúng ta sẽ **tin tưởng tuyệt đối vào Avalonia**. Khi bộ gõ IBus/Fcitx can thiệp vào một phím, Avalonia sẽ set cờ `e.Handled = true`.
    - Hàm forward key của AvaloniaCefBrowser sẽ chỉ cho phép gửi phím có **`e.Handled == false`**. (Và dĩ nhiên, **TUYỆT ĐỐI KHÔNG BAO GIỜ GỬI** sự kiện `KEYEVENT_CHAR` khi đang trong trạng thái `Composing` **hoặc `PendingCommit`**). Nếu không có điều kiện `PendingCommit`, phím bấm kết thúc từ (VD: Space) sẽ lọt qua lưới, gây ra lỗi lặp chữ ngay tại thời khắc Commit.
    - **Sai lệch có chủ ý:** Lựa chọn này đồng nghĩa với việc trong suốt quá trình `Composing`, CEF sẽ không nhận được bất kỳ sự kiện `keydown` nào (Chromium thật thì vẫn bắn `keydown` với `keyCode = 229` liên tục). Chúng ta chấp nhận sự sai lệch này ở giai đoạn đầu để đảm bảo luồng code sạch và ít bug lặp chữ nhất. Chỉ khi nào gặp các Web App quá phức tạp bị vỡ logic đếm phím, ta mới tính tới chuyện tự tổng hợp (synthesize) sự kiện 229.
- **Phân nhánh `TextInput`:** Sự kiện `TextInput` xảy ra cả khi gõ IME lẫn gõ tiếng Anh bình thường (ASCII). Do đó, chỉ map `TextInput` ➔ `ImeCommitText` **NẾU** trước đó đang có trạng thái composition (sử dụng cờ `PendingCommit` / `HasComposition` để làm nhánh rẽ). Nếu gõ ASCII bình thường, vẫn cho phép gửi qua đường Char event của CEF để bảo toàn tính chân thực (fidelity) của phím.
- **Smoke Test Sinh tử (IBus+Unikey & IBus+Bamboo):** Cuối Phase 2 bắt buộc phải test ngay 2 bộ gõ này. Bamboo cực kỳ đặc thù: khi app từ chối cấp Surrounding Text (quyết định ở Phase 1), Bamboo chuyển sang giả lập phím Backspace. Nếu chế độ này nát, ta phải đập đi xây lại toàn bộ kiến trúc. Phải test Bamboo ngay bây giờ, không đợi đến Phase 6.
- **Mục tiêu:** Tiêu diệt hoàn toàn mọi biến thể của lỗi **Double Input** và **Nuốt phím**; vượt qua Smoke Test với Bamboo. (QUAN TRỌNG NHẤT) Phải pass bài Regression Test cho người dùng gõ tiếng Anh (Tắt IME) - luồng phím thô phải đi qua mượt mà không suy suyển.

### Phase 3 — Exact caret tracking (Bám đuôi con trỏ chuẩn xác)
- **Sửa sai hệ quy chiếu (Coordinate Pipeline):** Thuộc tính `CursorRectangle` của Avalonia không phải toạ độ màn hình (Screen Coordinate), nó là toạ độ cục bộ của `TextViewVisual` (chính là control `AvaloniaCefBrowser`). Avalonia sẽ tự lo phần nhân Scale và bù đắp Offset để gửi cho X11/IBus vẽ popup. Việc tự ý tính Screen Coordinate là cách nhanh nhất để làm lệch popup trên các màn hình Multi-monitor/HiDPI.
- **Quy trình chuẩn xác:** `CEF View Coordinate` (từ `character_bounds`) ➔ truyền thẳng vào `CursorRectangle` (DIP của control). Bỏ hoàn toàn bước tự viết hàm TransformToScreen.
- **Lỗ hổng "Con Gà - Quả Trứng":** Avalonia query `CursorRectangle` *trước* khi composition diễn ra, nhưng CEF chỉ bắn `OnImeCompositionRangeChanged` *sau* khi bắt đầu gõ. Ký tự đầu tiên sẽ không có toạ độ và popup sẽ bay về góc trái trên (0,0). Giải pháp: Cần một cơ chế Fallback (lưu lại Rect lần cuối, hoặc ước lượng) cho ký tự mở màn mỗi từ.
- **Đón sự kiện từ thư viện (Forked):** Lắng nghe sự kiện `OnImeCompositionRangeChanged` đã được chúng ta expose từ việc fork `CefGlue` ở Phase 0.
- **Tuyệt đối tuân thủ Thread Dispatching:** Callback `OnImeCompositionRangeChanged` trả về từ CEF thread. Mọi thao tác cập nhật toạ độ hoặc raise event lên Avalonia **bắt buộc** phải dùng `Dispatcher.UIThread.Post`. (Đừng dùng `InvokeAsync` chờ kết quả ➔ cực kỳ dễ gây Deadlock).
- **Mục tiêu:** Popup của bộ gõ phải dính chặt vào con trỏ ở mọi mức Zoom trang và qua các bài test đa màn hình (Multi-monitor scale khác nhau).

### Phase 4 — Focus & Reset Lifecycle
- Xử lý các tình huống người dùng gián đoạn việc gõ phím:
    - `focus ➔ compose ➔ blur` (Avalonia Blur)
    - `focus ➔ compose ➔ chuyển tab`
    - `focus ➔ compose ➔ reload trang`
    - `focus ➔ compose ➔ đóng trình duyệt`
    - **`focus ➔ compose ➔ JavaScript DOM blur()`** (Cực kỳ nguy hiểm)
- **Rủi ro DOM Race Condition & Mất dữ liệu:** Nếu huỷ bỏ chữ dở dang ở mọi tình huống mất focus, người dùng sẽ bị cảm giác mất dữ liệu. Nhưng nếu nhồi chữ vào lúc đang chuyển tab/navigate, sẽ gây hỏng state web app.
- **FocusGeneration (Chống rò rỉ focus):** Thêm biến `FocusGeneration` để đánh dấu vòng đời của input hiện tại. Nếu event dội về mang generation cũ, vứt bỏ ngay lập tức để tránh ném text vào DOM mới.
- **Giải pháp phân nhánh vòng đời:**
    - **Trường hợp Navigate / Đổi Tab / Đóng trình duyệt:** Gọi `ImeCancelComposition` ngay lập tức để huỷ chữ dở dang, bảo vệ an toàn cho hệ thống.
    - **Quy tắc huỷ Browser (Dispose/Unload):** Bắt buộc phải **Detach IME client TRƯỚC, huỷ browser SAU**. Bất kỳ lệnh gọi `Ime*` nào (như ImeCancelComposition ở trên) cũng phải đi qua guard kiểm tra an toàn: `IsAlive`, `IsCurrentBrowser`, `IsInitialized`. Không có ngoại lệ. Tránh tuyệt đối việc gọi Ime trên một CefBrowserHost đã chết (Crash cứng).
    - **Trường hợp Blur thông thường (User click sang input khác trong cùng trang qua Avalonia):** Gọi `ImeFinishComposingText` để chốt chữ dở dang cho người dùng.
    - **Bẫy Double-commit:** Bản thân IBus cũng sẽ tự động bắn tín hiệu commit khi mất focus. Nhánh gọi `ImeFinishComposingText` bắt buộc phải có guard (kết hợp với `FocusGeneration` và State Machine) chống double-commit, nếu không sẽ lại dính lỗi lặp chữ.
    - **Trường hợp Mất Focus DOM ngầm (JavaScript Blur):** Khi JS gọi `.blur()`, Avalonia control vẫn giữ focus, CEF browser vẫn giữ focus, nhưng ô input bên dưới đã biến mất. Dấu hiệu duy nhất là CEF sẽ bắn `OnVirtualKeyboardRequested` với mode = `None`. Nếu lúc này State đang là `Composing` hoặc `PendingCommit`, bắt buộc phải gọi `ImeCancelComposition`, tăng `FocusGeneration`, và reset State về `Idle` ngay lập tức. Nếu không, bộ gõ sẽ bị "mồ côi" (Orphaned Preedit) và toàn bộ bàn phím sẽ bị liệt.
- **Mục tiêu:** Bấm sang ô input khác chữ đang gõ sẽ tự động chốt xuống an toàn (không duplicate), chuyển tab/reload trang/JS can thiệp thì chữ dở dang biến mất sạch sẽ không để lại hậu quả (treo bàn phím).

### Phase 5 — Hardening (Bọc giáp bảo vệ)
Triển khai các biện pháp bảo vệ kiến trúc:
- **Lối thoát hiểm (Runtime Feature Flag):** Thiết lập một cờ (Environment Variable hoặc cấu hình file) để có thể tắt hoàn toàn kiến trúc `CefTextInputMethodClient` và trả trình duyệt về trạng thái cũ. Trong thế giới Linux, không ai dám vỗ ngực test được 100% các distro. Feature Flag này cung cấp một đường lùi (kill-switch) cho người dùng cuối nếu hệ thống của họ bị crash mà không bắt họ phải chờ bản vá.
- **Bật Trace Logging trên Release (Verbose IME Trace):** Thêm một biến môi trường chung họ với Kill-switch để bật lại toàn bộ hệ thống Log của Phase 0 ngay cả trên bản Release. Khi user gặp bug, chỉ cần bảo họ chạy lại app với cờ `--verbose-ime-trace` để lấy log về debug, thay vì phải build riêng một bản debug gửi cho họ.
- **Mục tiêu:** Có khả năng tắt hoàn toàn kiến trúc bằng một cờ duy nhất (kill-switch), lấy được log chuẩn xác từ máy khách bằng cờ Verbose, và một lần nữa phải Pass bài Regression Test tiếng Anh dưới tải nặng.

### Phase 6 — Cross-Language Test (Test diện rộng CJK)
- Vì kiến trúc IME OSR này áp dụng chuẩn chung, Chromium sẽ hiểu mọi ngôn ngữ. Phải test diện rộng với các ngôn ngữ CJK trên backend Fcitx5:
    - Fcitx5 + Mozc (Tiếng Nhật)
    - Fcitx5 + Pinyin (Tiếng Trung)
    - Fcitx5 + Hangul (Tiếng Hàn)
    - (Smoke test lại Fcitx5 + Bamboo để chốt độ ổn định)

## Verification Plan (Test Matrix)

Vì tính năng OSR IME trên Linux của CEF chưa được kiểm thử rộng rãi trên tất cả các Distro, quá trình kiểm thử cần tuân thủ ma trận kiểm tra (Test Matrix) khắt khe sau:

**1. Các loại trường nhập liệu (Browser Inputs):**
- `<input type="text">`
- `<textarea>`
- `<input type="search">`
- **`<input type="password">`**: Chromium sẽ chủ động vô hiệu hoá Composition ở trường này. Không được kỳ vọng chữ gập/preedit xuất hiện, chỉ cần đảm bảo hệ thống không bị crash và phím không bị nuốt mất.
- Phần tử `contenteditable` thuần.
- Rich text editor nhẹ (CKEditor, Quill).
- IFrame.
- **Google Docs (Ưu tiên thấp nhất):** Công cụ này sử dụng editing surface riêng biệt và cực kỳ phức tạp. Nếu test ban đầu fail ở đây thì chưa chắc đã do code của chúng ta, nên để rành test cuối cùng sau khi mọi case cơ bản đã Pass.

**2. Các tổ hợp bộ gõ (IME Engines):**
- IBus + Unikey
- **IBus + Bamboo:** Bamboo có cơ chế gõ rất đặc thù. Khi app không hỗ trợ Surrounding Text (như thiết lập ở Phase 1), Bamboo có thể rơi vào chế độ giả lập phím Backspace thay vì dùng Preedit. Cần test kỹ mọi chế độ gõ của Bamboo để đảm bảo không dính bug nuốt chữ kinh điển của tiếng Việt.
- Các module Fcitx5 (được phân bổ vào Phase 6).

**3. Các tình huống sử dụng và Edge Cases gây Crash (Scenarios):**
- Gõ chậm từng ký tự / Gõ rất nhanh liên tục (đảm bảo không rớt chữ hay double input).
- Ấn phím Backspace / `Escape` / `Enter` / `Space` khi đang composition.
- Click chuột trái / phải (Context Menu) vào giữa từ đang gõ dở.
- **Drag & Drop** (kéo thả) đoạn text vào ô input đang Composing.
- **JavaScript can thiệp:** Gõ tiếng Việt vào các form auto-format (Ví dụ: ô nhập thẻ tín dụng tự thêm dấu cách, ô nhập số điện thoại). Web JS sẽ tự động `set.value = ""` hoặc `setSelectionRange()` trong lúc user đang gõ dở ➔ CEF sẽ ném `OnImeCompositionRangeChanged` đột ngột, Client bắt buộc phải xử lý mượt mà (không được crash).
- Quét chọn văn bản (Ctrl+A) / Sao chép / Dán / Bôi đen và gõ đè (Selection replacement).
- Đổi tab trình duyệt / Đổi URL trên thanh địa chỉ / Tải lại trang (Reload).
- **Trôi toạ độ khi cuộn trang (Scroll):** CEF **KHÔNG** tự động bắn lại sự kiện `OnImeCompositionRangeChanged` khi trang web bị cuộn, khiến popup bộ gõ trôi nổi (drift) khỏi con trỏ. Giải pháp: Chấp nhận sự thật này (giống nhiều app Native khác), hoặc dùng cờ ép Cancel Composition khi phát hiện Scroll.
- **Phóng to trang (Zoom):** Ảnh hưởng trực tiếp tới hệ số nhân toạ độ (Scale). Bài test sinh tử cho logic toạ độ của Phase 3.
- **Di chuyển / Thay đổi kích thước (Resize)** cửa sổ app khi đang composing.
- **Màn hình độ phân giải cao & Đa màn hình (Multi-monitor):** Kéo cửa sổ qua lại giữa màn hình 150% Scale và màn hình 100% Scale khi đang gõ dở để xem popup có bị văng khỏi hệ quy chiếu hay không.
- **Click chuột vào Candidate Window của IBus** (chọn từ bằng chuột thay vì phím số).
- **Đổi layout bàn phím (Keyboard Layout/Language)** ngay giữa lúc đang Composition.
- **Chuyển Focus nội bộ:** Click sang một ô input khác trên cùng một trang (để test nhánh `ImeFinishComposingText` của Phase 4).
- **Mất Focus hoàn toàn:** Alt+Tab sang app khác hoặc click ra Desktop. Lúc này IBus sẽ deactivate input context, phản ứng rất khác với Focus nội bộ.
- **Chuyển Focus đa trình duyệt:** Mở 2 instance AvaloniaCefBrowser cạnh nhau và nhấp chuột qua lại khi một bên đang composing.
- **Mở popup native của CEF** (nhấn mở thẻ dropdown `<select>`) rồi bắt đầu gõ tiếng Việt.
- **Gõ chữ lúc Page Load:** Gõ ngay lúc trang đang load dở, hoặc trước khi browser kịp Initialize xong.
- **Renderer Process Crash:** Kill thủ công (hoặc chờ crash) Chromium Render Process (`OnRenderProcessTerminated`) khi đang composing dở. State Machine bắt buộc phải được reset về `Idle`, nếu không toàn bộ hệ thống sẽ bị treo state và nuốt sạch mọi phím bấm sau này.
- **(QUAN TRỌNG NHẤT) Regression Test cho User gõ tiếng Anh (Không cài IME):** Bài test tiên quyết. 90% người dùng Linux gõ tiếng Anh. Phải chứng minh rằng khi tắt IME hoặc ở môi trường thuần tiếng Anh, luồng gõ phím cũ vẫn nguyên vẹn 100%, không bị nuốt, không bị lag, không bị duplicate.
