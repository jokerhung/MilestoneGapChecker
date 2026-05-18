using System;
using System.IO;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
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
        public string[] CameraGuids { get; set; }
        // Nếu không truyền --start-time/--end-time thì dùng CheckLastHours
    }

    public class GapRange
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
    }

    public class CameraGapSummary
    {
        public string CameraName { get; set; }
        public Guid CameraGuid { get; set; }
        public System.Collections.Generic.List<GapRange> Gaps { get; set; } = new System.Collections.Generic.List<GapRange>();
    }

    class Program
    {
        static void Main(string[] args)
        {
            VideoOS.Platform.SDK.Environment.Initialize();
            VideoOS.Platform.SDK.Export.Environment.Initialize();
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            Console.WriteLine("=== MILESTONE GAP CHECKER ===");

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

                bool listAllCameras = args.Any(a =>
                    string.Equals(a, "--list-cameras", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-l", StringComparison.OrdinalIgnoreCase));

                if (listAllCameras)
                {
                    ListAllCameras();
                    return;
                }

                // 3. Lấy danh sách camera theo GUID
                if (config.CameraGuids == null || config.CameraGuids.Length == 0)
                {
                    Console.WriteLine("LỖI: Chưa cấu hình CameraGuids trong config.json.");
                    return;
                }

                var cameras = config.CameraGuids
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Select(g => g.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(g => new { RawGuid = g, Parsed = Guid.TryParse(g, out var id), Id = Guid.TryParse(g, out var id2) ? id2 : Guid.Empty })
                    .ToList();

                var invalidGuids = cameras.Where(x => !x.Parsed).Select(x => x.RawGuid).ToList();
                if (invalidGuids.Any())
                {
                    Console.WriteLine("LỖI: Các GUID không hợp lệ trong CameraGuids:");
                    foreach (var bad in invalidGuids)
                        Console.WriteLine($"- {bad}");
                    return;
                }

                var cameraItems = cameras
                    .Select(x => Configuration.Instance.GetItem(x.Id, Kind.Camera))
                    .ToList();

                var missing = cameraItems
                    .Select((item, index) => new { item, guid = cameras[index].RawGuid })
                    .Where(x => x.item == null)
                    .Select(x => x.guid)
                    .ToList();

                if (missing.Any())
                {
                    Console.WriteLine("LỖI: Không tìm thấy camera với các GUID:");
                    foreach (var guid in missing)
                        Console.WriteLine($"- {guid}");
                    return;
                }

                // 4. Xác định khoảng thời gian quét
                string startArg = GetArgValue(args, "--start-time");
                string endArg = GetArgValue(args, "--end-time");
                DateTime startTime, endTime;

                if (!string.IsNullOrWhiteSpace(startArg) || !string.IsNullOrWhiteSpace(endArg))
                {
                    if (string.IsNullOrWhiteSpace(startArg) || string.IsNullOrWhiteSpace(endArg))
                    {
                        Console.WriteLine("LỖI: Phải truyền đủ cả --start-time và --end-time.");
                        return;
                    }

                    if (!DateTime.TryParse(startArg, out startTime) || !DateTime.TryParse(endArg, out endTime))
                    {
                        Console.WriteLine("LỖI: --start-time/--end-time không đúng định dạng. Dùng: yyyy-MM-dd HH:mm:ss");
                        return;
                    }

                    Console.WriteLine($"Bắt đầu quét {cameraItems.Count} camera (Từ {startTime:dd/MM/yyyy HH:mm} đến {endTime:dd/MM/yyyy HH:mm})...");
                }
                else
                {
                    endTime = DateTime.Now;
                    startTime = endTime.AddHours(-config.CheckLastHours);
                    Console.WriteLine($"Bắt đầu quét {cameraItems.Count} camera (Dữ liệu {config.CheckLastHours}h qua)...");
                }

                var summaries = new System.Collections.Generic.List<CameraGapSummary>();
                foreach (var camera in cameraItems)
                {
                    summaries.Add(CheckVideoGaps(camera, startTime, endTime));
                }

                PrintOverallSummary(summaries);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi hệ thống: {ex.Message}");
            }
            finally
            {
                Console.WriteLine("\nHoàn tất kiểm tra.");
            }
        }

        static string GetArgValue(string[] args, string key)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
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
                return JsonConvert.DeserializeObject<AppConfig>(jsonString);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LỖI: Không thể đọc file cấu hình. Chi tiết: {ex.Message}");
                return null;
            }
        }

        static void ListAllCameras()
        {
            var rootItems = Configuration.Instance.GetItems();
            var allCameras = rootItems
                .SelectMany(GetCamerasRecursively)
                .Where(c => c != null)
                .GroupBy(c => c.FQID.ObjectId)
                .Select(g => g.First())
                .OrderBy(c => c.Name)
                .ToList();

            Console.WriteLine($"Tìm thấy {allCameras.Count} camera:");
            foreach (var camera in allCameras)
                Console.WriteLine($"- {camera.Name} | GUID: {camera.FQID.ObjectId}");
        }

        static System.Collections.Generic.IEnumerable<Item> GetCamerasRecursively(Item item)
        {
            if (item == null)
                yield break;

            if (item.FQID.Kind == Kind.Camera)
                yield return item;

            var children = item.GetChildren();
            if (children == null)
                yield break;

            foreach (var child in children)
            {
                foreach (var camera in GetCamerasRecursively(child))
                    yield return camera;
            }
        }

        static CameraGapSummary CheckVideoGaps(Item cameraItem, DateTime start, DateTime end)
        {
            var summary = new CameraGapSummary
            {
                CameraName = cameraItem.Name,
                CameraGuid = cameraItem.FQID.ObjectId
            };

            DateTime startUtc = start.ToUniversalTime();
            DateTime endUtc = end.ToUniversalTime();
            TimeSpan span = endUtc - startUtc;

            SequenceDataSource source = new SequenceDataSource(cameraItem);

            var supportedTypes = source.GetTypes();
            Console.WriteLine($"   [DEBUG] Supported types: {string.Join(", ", supportedTypes.Select(t => t.Name + "=" + t.Id))}");

            var recGuid = DataType.SequenceTypeGuids.RecordingSequence;
            var rawData = source.GetData(startUtc, span, 10000, span, 10000, recGuid);

            Console.WriteLine($"   [DEBUG] rawData count={rawData.Count}, types: {string.Join(", ", rawData.Select(o => o?.GetType().Name).Distinct())}");

            var sequences = rawData
                .OfType<SequenceData>()
                .Select(sd => sd.EventSequence)
                .Where(es => es != null && es.StartDateTime < endUtc && es.EndDateTime > startUtc)
                .OrderBy(es => es.StartDateTime)
                .ToList();

            Console.WriteLine($"\nCamera: {cameraItem.Name}");
            Console.WriteLine($"   Khoảng thời gian: {start:dd/MM/yyyy HH:mm:ss} -> {end:dd/MM/yyyy HH:mm:ss}");
            Console.WriteLine($"   Tìm thấy {sequences.Count} đoạn recording.");

            if (sequences.Count == 0)
            {
                summary.Gaps.Add(new GapRange { Start = start, End = end });
                Console.WriteLine($"   [!] CẢNH BÁO: Không có dữ liệu recording nào trong khoảng thời gian này!");
                return summary;
            }

            foreach (var seq in sequences)
                Console.WriteLine($"   [DEBUG] seg: Start={seq.StartDateTime.ToLocalTime():dd/MM HH:mm:ss} | End={seq.EndDateTime.ToLocalTime():dd/MM HH:mm:ss}");

            Console.WriteLine($"   Recording đầu tiên: {sequences.First().StartDateTime.ToLocalTime():dd/MM HH:mm:ss}");
            Console.WriteLine($"   Recording cuối:     {sequences.Last().EndDateTime.ToLocalTime():dd/MM HH:mm:ss}");

            DateTime currentPointer = startUtc;

            foreach (var seq in sequences)
            {
                if ((seq.StartDateTime - currentPointer).TotalMinutes > 5)
                {
                    var gapStart = currentPointer.ToLocalTime();
                    var gapEnd = seq.StartDateTime.ToLocalTime();
                    var gapDuration = seq.StartDateTime - currentPointer;
                    summary.Gaps.Add(new GapRange { Start = gapStart, End = gapEnd });
                    Console.WriteLine($"   - TRỐNG {gapDuration.TotalMinutes:F0} phút: {gapStart:dd/MM/yyyy HH:mm:ss} -> {gapEnd:dd/MM/yyyy HH:mm:ss}");
                }
                if (seq.EndDateTime > currentPointer)
                    currentPointer = seq.EndDateTime;
            }

            if ((endUtc - currentPointer).TotalMinutes > 5)
            {
                var gapStart = currentPointer.ToLocalTime();
                var gapEnd = endUtc.ToLocalTime();
                var gapDuration = endUtc - currentPointer;
                summary.Gaps.Add(new GapRange { Start = gapStart, End = gapEnd });
                Console.WriteLine($"   - TRỐNG {gapDuration.TotalMinutes:F0} phút: {gapStart:dd/MM/yyyy HH:mm:ss} -> {gapEnd:dd/MM/yyyy HH:mm:ss}");
            }

            if (!summary.Gaps.Any())
                Console.WriteLine("   => Không phát hiện gap nào (ngưỡng > 5 phút).");

            return summary;
        }

        static void PrintOverallSummary(System.Collections.Generic.List<CameraGapSummary> summaries)
        {
            var camerasWithGap = summaries.Where(s => s.Gaps.Any()).ToList();

            Console.WriteLine("\n=== SUMMARY ===");
            if (!camerasWithGap.Any())
            {
                Console.WriteLine("Không phát hiện gap ở tất cả camera đã quét.");
                return;
            }

            Console.WriteLine($"Phát hiện gap ở {camerasWithGap.Count}/{summaries.Count} camera:");
            foreach (var camera in camerasWithGap)
            {
                Console.WriteLine($"- {camera.CameraName} | GUID: {camera.CameraGuid}");
                foreach (var gap in camera.Gaps)
                    Console.WriteLine($"  + {gap.Start:dd/MM/yyyy HH:mm:ss} -> {gap.End:dd/MM/yyyy HH:mm:ss}");
            }
        }
    }
}
