# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

---

## Build & Run / Biên dịch & Chạy

```bash
# Build / Biên dịch
dotnet build RR2Bot/RR2Bot.csproj

# Run (WinForms — requires Windows / cần Windows)
dotnet run --project RR2Bot/RR2Bot.csproj
```

No tests exist. Debug in VS Code via `.vscode/launch.json` (already configured).
_Không có test. Debug trong VS Code qua `.vscode/launch.json` (đã cấu hình sẵn)._

---

## Architecture / Kiến trúc

### Entry point / Điểm khởi động
`Program.cs` → `MainForm` (WinForms). Quản lý kết nối ADB, chọn mode, spawn các manager task.

### Core layer (`Core/`)

| File | Role / Vai trò |
|------|------|
| `AdbController.cs` | Wraps SharpAdbClient. Mọi lệnh tap/swipe đi qua đây. Tọa độ dùng ratio (0.0–1.0) qua `TapRatio`/`SwipeRatio` — scale theo resolution Android đọc từ `wm size`. |
| `ScreenCapture.cs` | Chụp màn hình BlueStacks bằng Win32 BitBlt. Tự động scale theo DPI monitor (`GetDpiForMonitor`) — trả về physical pixels. |
| `ImageMatcher.cs` | Template matching bằng EmguCV (`CcoeffNormed`). `FindTemplate` thử 5 mức scale. `FindAllTemplates` cho multi-match. Template load từ thư mục `Templates/` (copy vào output lúc build). |

### Module layer (`Modules/`)

- **`BattleManager.cs`** — State machine: `AtBase → EnteringBattle → InBattle`. Các method chính:
  - `IsBattleHudVisible()` — pixel-based: đếm pixel xanh dương ở thanh mana (y=0.94) và kiểm tra màu nút Pause
  - `HasEnemyHpBar()` — quét dải đỏ ngang ≥8px (R>170, G<90, B<90) trong vùng y=10–55%
  - `IsHpLow()` — quét y=18–55% tìm pixel xanh lá (G>70, G>R+25, G>B+25); không tìm thấy = giả sử HP ổn
  - Logic chiến đấu: có địch → đứng đánh + xả chiêu; đường trống → `MoveHeroByWaypoint()` tap (0.82, 0.18)
- **`BaseManager.cs`** — Template matching thu vàng và upgrade tòa nhà. Phần lớn là stub; templates cho upgrade chưa chụp.

### Config (`Models/BotConfig.cs`)
Một object config duy nhất khởi tạo trong `MainForm`, truyền vào cả hai manager. Toàn bộ tọa độ là ratio. Giá trị calibrate cho BlueStacks 1600×900.

### Detection strategy / Chiến lược nhận diện

**Pixel color** (không dùng OpenCV) ưu tiên cho HUD in-battle — nhanh hơn và ổn định hơn template matching với color bar. Template matching (`ImageMatcher`) chỉ dùng cho UI tĩnh (community icon, nút OPEN, favorites card, thu vàng).

---

## Templates folder / Thư mục Templates

`RR2Bot/Templates/` — file PNG copy vào output lúc build. Template thiếu sẽ fallback về tọa độ hardcoded. **Hiện còn thiếu:** `defeat.png`, `food_collect.png`, `builder_idle.png`, `upgrade_btn.png`, `confirm_upgrade.png`.

---

## Key coordinates / Tọa độ quan trọng (BotConfig defaults, 1600×900)

| Mục tiêu | Ratio (x, y) |
|----------|-------------|
| Community icon | (0.957, 0.554) |
| Favorites OPEN fallback | (0.875, 0.780) |
| Tap attack player | (0.875, 0.280) |
| ATTACK! button | (0.79, 0.82) |
| Hero tap-to-move (upper-right = forward) | (0.82, 0.18) |
| Spell buttons | (0.937, 0.166) và (0.923, 0.844) |
| Troop buttons | (0.069, 0.828) và (0.069, 0.166) |
