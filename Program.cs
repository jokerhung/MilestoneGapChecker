using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using VideoOS.Platform;
using VideoOS.Platform.Data;

namespace MilestoneGapChecker
{
    // Model để map dữ liệu từ config.json
    public class AppConfig
    {
        public string ServerUrl { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string AuthenticationType { get; set; } = "Basic";
        public int CheckLastHours { get; set; } = 24;
        public string CameraGuid { get; set; }
        // Nếu có StartTime và EndTime thì ưu tiên dùng, bỏ qua CheckLastHours
        public string StartTime { get; set; }
        public string EndTime { get; set; }
    }

    class Program
    {
        static void Main(string[] args)
        {
            VideoOS.Platform.SDK.Environment.Initialize();
            VideoOS.Platform.SDK.Export.Environment.Initialize();
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.WriteLine("=== MILESTONE GAP CHECKER (CONFIG MODE) ===");

            // 1. Đọc file config.json
            AppConfig config = LoadConfiguration();
            if (config == null) return;

            try
            {
                // 2. Thiết lập đăng nhập
                Uri uri = new Uri(config.ServerUrl);
                // SDK 26.x: GetCredentialCache đã đổi thành Util.BuildCredentialCache
                CredentialCache cc = VideoOS.Platform.Login.Util.BuildCredentialCache(
                    uri, config.Username, config.Password, config.AuthenticationType);

                Console.WriteLine($"Đang kết nối tới: {config.ServerUrl}...");
                // SDK 26.x: AddServer và Login yêu cầu thêm tham số masterOnly
                VideoOS.Platform.SDK.Environment.AddServer(uri, cc, false);
                VideoOS.Platform.SDK.Environment.Login(uri, false);
                Console.WriteLine("Đăng nhập thành công!");

                // 3. Lấy camera theo GUID
                if (string.IsNullOrEmpty(config.CameraGuid))
                {
                    Console.WriteLine("LỖI: Chưa cấu hình CameraGuid trong config.json.");
                    return;
                }

                var cameraGuid = new Guid(config.CameraGuid);
                var cam = Configuration.Instance.GetItem(cameraGuid, Kind.Camera);
                if (cam == null)
                {
                    Console.WriteLine($"LỖI: Không tìm thấy camera với GUID {config.CameraGuid}.");
                    return;
                }

                // 4. Xác định khoảng thời gian quét
                DateTime startTime, endTime;
                if (!string.IsNullOrEmpty(config.StartTime) && !string.IsNullOrEmpty(config.EndTime))
                {
                    if (!DateTime.TryParse(config.StartTime, out startTime) ||
                        !DateTime.TryParse(config.EndTime, out endTime))
                    {
                        Console.WriteLine("LỖI: StartTime/EndTime không đúng định dạng. Dùng: yyyy-MM-dd HH:mm:ss");
                        return;
                    }
                    Console.WriteLine($"Bắt đầu quét camera: {cam.Name} (Từ {startTime:dd/MM/yyyy HH:mm} đến {endTime:dd/MM/yyyy HH:mm})...");
                }
                else
                {
                    endTime = DateTime.Now;
                    startTime = endTime.AddHours(-config.CheckLastHours);
                    Console.WriteLine($"Bắt đầu quét camera: {cam.Name} (Dữ liệu {config.CheckLastHours}h qua)...");
                }

                CheckVideoGaps(cam, startTime, endTime);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi hệ thống: {ex.Message}");
            }
            finally
            {
                Console.WriteLine("\nHoàn tất kiểm tra. Nhấn phím bất kỳ để thoát...");
                Console.ReadKey();
            }
        }

        static AppConfig LoadConfiguration()
        {
            string configPath = "config.json";
            if (!File.Exists(configPath))
            {
                Console.WriteLine("LỖI: Không tìm thấy file config.json!");
                return null;
            }

            try
            {
                string jsonString = File.ReadAllText(configPath);
                return JsonSerializer.Deserialize<AppConfig>(jsonString);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LỖI: Không thể đọc file cấu hình. Chi tiết: {ex.Message}");
                return null;
            }
        }

        static void CheckVideoGaps(Item cameraItem, DateTime start, DateTime end)
        {
            DateTime startUtc = start.ToUniversalTime();
            DateTime endUtc = end.ToUniversalTime();
            TimeSpan span = endUtc - startUtc;

            SequenceDataSource source = new SequenceDataSource(cameraItem);

            // In các loại sequence mà camera hỗ trợ
            var supportedTypes = source.GetTypes();
            Console.WriteLine($"   [DEBUG] Supported types: {string.Join(", ", supportedTypes.Select(t => t.Name + "=" + t.Id))}");

            // Dùng RecordingSequence GUID để lấy từng đoạn recording riêng lẻ
            var recGuid = DataType.SequenceTypeGuids.RecordingSequence;
            var rawData = source.GetData(startUtc, span, 10000, span, 10000, recGuid);

            Console.WriteLine($"   [DEBUG] rawData count={rawData.Count}, types: {string.Join(", ", rawData.Select(o => o?.GetType().Name).Distinct())}");

            var sequences = rawData
                .OfType<SequenceData>()
                .Select(sd => sd.EventSequence)
                .Where(es => es != null && es.StartDateTime < endUtc && es.EndDateTime > startUtc)
                .OrderBy(es => es.StartDateTime)
                .ToList();

            Console.WriteLine($"   Khoảng thời gian: {start:dd/MM/yyyy HH:mm:ss} -> {end:dd/MM/yyyy HH:mm:ss}");
            Console.WriteLine($"   Tìm thấy {sequences.Count} đoạn recording.");

            if (sequences.Count == 0)
            {
                Console.WriteLine($"   [!] CẢNH BÁO: Không có dữ liệu recording nào trong khoảng thời gian này!");
                return;
            }

            foreach (var seq in sequences)
                Console.WriteLine($"   [DEBUG] seg: Start={seq.StartDateTime.ToLocalTime():dd/MM HH:mm:ss} | End={seq.EndDateTime.ToLocalTime():dd/MM HH:mm:ss}");

            Console.WriteLine($"   Recording đầu tiên: {sequences.First().StartDateTime.ToLocalTime():dd/MM HH:mm:ss}");
            Console.WriteLine($"   Recording cuối:     {sequences.Last().EndDateTime.ToLocalTime():dd/MM HH:mm:ss}");

            DateTime currentPointer = startUtc;
            bool hasGap = false;

            foreach (var seq in sequences)
            {
                if ((seq.StartDateTime - currentPointer).TotalMinutes > 5)
                {
                    if (!hasGap) Console.WriteLine($"\n[!] CAMERA: {cameraItem.Name} - Phát hiện gap:");
                    hasGap = true;
                    var gapDuration = seq.StartDateTime - currentPointer;
                    Console.WriteLine($"   - TRỐNG {gapDuration.TotalMinutes:F0} phút: {currentPointer.ToLocalTime():dd/MM/yyyy HH:mm:ss} -> {seq.StartDateTime.ToLocalTime():dd/MM/yyyy HH:mm:ss}");
                }
                if (seq.EndDateTime > currentPointer)
                    currentPointer = seq.EndDateTime;
            }

            // Kiểm tra đoạn cuối
            if ((endUtc - currentPointer).TotalMinutes > 5)
            {
                if (!hasGap) Console.WriteLine($"\n[!] CAMERA: {cameraItem.Name} - Phát hiện gap:");
                hasGap = true;
                var gapDuration = endUtc - currentPointer;
                Console.WriteLine($"   - TRỐNG {gapDuration.TotalMinutes:F0} phút: {currentPointer.ToLocalTime():dd/MM/yyyy HH:mm:ss} -> {endUtc.ToLocalTime():dd/MM/yyyy HH:mm:ss}");
            }

            if (!hasGap)
                Console.WriteLine("   => Không phát hiện gap nào (ngưỡng > 5 phút).");
        }
    }
}
