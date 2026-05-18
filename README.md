# MilestoneGapChecker

Tool kiểm tra gap (khoảng trống) trong dữ liệu recording của camera Milestone XProtect.

## Yêu cầu

- .NET Framework 4.7.2
- Milestone XProtect server (đã test với SDK 26.1.2)

## Cấu hình

Chỉnh file `config.json` trước khi chạy:

```json
{
  "ServerUrl": "http://10.10.92.143",
  "Username": "admin",
  "Password": "your_password",
  "AuthenticationType": "Basic",
  "CameraGuid": "79fc9949-757b-46d3-afb1-8d3c62c03faa",

  "CheckLastHours": 24,

  "StartTime": "2026-05-16 00:00:00",
  "EndTime":   "2026-05-18 00:00:00"
}
```

- **CameraGuid**: GUID của camera cần kiểm tra (lấy từ Milestone Management Client)
- **StartTime / EndTime**: quét theo khoảng thời gian cố định. Nếu để trống thì dùng `CheckLastHours`
- **CheckLastHours**: số giờ tính từ thời điểm hiện tại về trước (dùng khi không có StartTime/EndTime)

## Chạy

```
MilestoneGapChecker.exe
```

## Output mẫu

```
Bắt đầu quét camera: AXIS P1364 - Camera 1 (Từ 16/05/2026 00:00 đến 18/05/2026 00:00)...
   Khoảng thời gian: 16/05/2026 00:00:00 -> 18/05/2026 00:00:00
   Tìm thấy 7 đoạn recording.

[!] CAMERA: AXIS P1364 - Camera 1 - Phát hiện gap:
   - TRỐNG 203 phút: 01/05/2026 10:58:21 -> 01/05/2026 14:21:13
   - TRỐNG 54 phút: 09/05/2026 13:13:08 -> 09/05/2026 14:06:58
```

Gap được báo khi khoảng trống **> 5 phút**.
