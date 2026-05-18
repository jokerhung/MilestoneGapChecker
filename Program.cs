using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Web.Http;
using Microsoft.Owin.Hosting;
using Newtonsoft.Json;
using Owin;
using VideoOS.Platform;
using VideoOS.Platform.Data;

namespace MilestoneGapChecker
{
    public class AppConfig
    {
        public string ServerUrl { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string AuthenticationType { get; set; } = "Basic";
        public int CheckLastHours { get; set; } = 24;
        public string[] CameraGuids { get; set; }
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
        public List<GapRange> Gaps { get; set; } = new List<GapRange>();
    }

    public class GapCheckResponse
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public bool HasGap { get; set; }
        public List<CameraGapSummary> Cameras { get; set; }
    }

    public class GapRunResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public List<CameraGapSummary> Summaries { get; set; } = new List<CameraGapSummary>();
    }

    public class ResolveCamerasResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public List<Item> Cameras { get; set; } = new List<Item>();
    }

    public class ResolveTimeResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    public static class AppState
    {
        public static AppConfig Config { get; set; }
        public static Uri ServerUri { get; set; }
        public static CredentialCache Credentials { get; set; }
    }

    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            var config = new HttpConfiguration();
            config.MapHttpAttributeRoutes();
            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{action}",
                defaults: new { action = RouteParameter.Optional }
            );
            app.UseWebApi(config);
        }
    }

    [RoutePrefix("api")]
    public class GapController : ApiController
    {
        [HttpGet]
        [Route("health")]
        public IHttpActionResult Health() => Ok(new { message = "ok" });

        [AcceptVerbs("GET")]
        [Route("gaps/check")]
        public IHttpActionResult CheckGet([FromUri] string cameraGuids = null, [FromUri] string startTime = null, [FromUri] string endTime = null)
        {
            var guidList = string.IsNullOrWhiteSpace(cameraGuids)
                ? null
                : cameraGuids.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();

            var run = Program.RunGapCheck(guidList, startTime, endTime, false);
            if (!run.Success)
            {
                return Content(HttpStatusCode.InternalServerError, new GapCheckResponse
                {
                    Status = "error",
                    Message = run.ErrorMessage,
                    HasGap = false,
                    Cameras = new List<CameraGapSummary>()
                });
            }

            var hasGap = run.Summaries.Any(s => s.Gaps.Any());
            return Ok(new GapCheckResponse
            {
                Status = hasGap ? "error" : "success",
                Message = hasGap ? "Có gap" : "Không có gap",
                HasGap = hasGap,
                Cameras = run.Summaries
            });
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            VideoOS.Platform.SDK.Environment.Initialize();
            VideoOS.Platform.SDK.Export.Environment.Initialize();
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== MILESTONE GAP CHECKER ===");

            var appConfig = LoadConfiguration();
            if (appConfig == null) return;
            AppState.Config = appConfig;

            try
            {
                Console.WriteLine($"Đang kết nối tới: {appConfig.ServerUrl}...");
                LoginToMilestone(appConfig);
                Console.WriteLine("Đăng nhập thành công!");

                if (args.Any(a => string.Equals(a, "--start-server", StringComparison.OrdinalIgnoreCase)))
                {
                    StartServer(args);
                    return;
                }

                if (args.Any(a => string.Equals(a, "--list-cameras", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-l", StringComparison.OrdinalIgnoreCase)))
                {
                    ListAllCameras();
                    return;
                }

                var run = RunGapCheck(null, GetArgValue(args, "--start-time"), GetArgValue(args, "--end-time"), true);
                if (!run.Success)
                {
                    Console.WriteLine($"LỖI: {run.ErrorMessage}");
                    return;
                }

                PrintOverallSummary(run.Summaries);
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

        static void StartServer(string[] args)
        {
            var baseUrl = GetArgValue(args, "--base-url") ?? "http://localhost:8080";
            using (WebApp.Start<Startup>(url: baseUrl))
            {
                Console.WriteLine($"REST API đang chạy tại: {baseUrl}");
                Console.WriteLine($"GET  {baseUrl}/api/health");
                Console.WriteLine($"GET  {baseUrl}/api/gaps/check?cameraGuids=<guid1,guid2>&startTime=yyyy-MM-dd HH:mm:ss&endTime=yyyy-MM-dd HH:mm:ss");
                Console.WriteLine("(Bỏ qua query để dùng CameraGuids + CheckLastHours từ config.json)");
                Console.WriteLine("Nhấn Enter để dừng server...");
                Console.ReadLine();
            }
        }

        public static GapRunResult RunGapCheck(IEnumerable<string> cameraGuidsOverride, string startArg, string endArg, bool verbose)
        {
            return RunGapCheckInternal(cameraGuidsOverride, startArg, endArg, verbose, true);
        }

        static GapRunResult RunGapCheckInternal(IEnumerable<string> cameraGuidsOverride, string startArg, string endArg, bool verbose, bool allowReloginRetry)
        {
            var cfg = AppState.Config;
            try
            {
                var camResult = ResolveCameras(cameraGuidsOverride ?? cfg.CameraGuids);
                if (!camResult.Success) return new GapRunResult { Success = false, ErrorMessage = camResult.ErrorMessage };

                var timeResult = ResolveTimeRange(startArg, endArg, cfg.CheckLastHours);
                if (!timeResult.Success) return new GapRunResult { Success = false, ErrorMessage = timeResult.ErrorMessage };

                if (verbose)
                    Console.WriteLine($"Bắt đầu quét {camResult.Cameras.Count} camera (Từ {timeResult.StartTime:dd/MM/yyyy HH:mm} đến {timeResult.EndTime:dd/MM/yyyy HH:mm})...");

                var summaries = new List<CameraGapSummary>();
                foreach (var camera in camResult.Cameras)
                    summaries.Add(CheckVideoGaps(camera, timeResult.StartTime, timeResult.EndTime, verbose));

                return new GapRunResult { Success = true, Summaries = summaries };
            }
            catch (Exception ex)
            {
                if (allowReloginRetry && IsSessionExpiredError(ex))
                {
                    if (verbose)
                        Console.WriteLine("Phiên đăng nhập đã hết hạn, đang đăng nhập lại...");
                    ReLoginToMilestone();
                    return RunGapCheckInternal(cameraGuidsOverride, startArg, endArg, verbose, false);
                }

                return new GapRunResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        static bool IsSessionExpiredError(Exception ex)
        {
            var message = ex?.Message ?? string.Empty;
            return message.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("session", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void LoginToMilestone(AppConfig appConfig)
        {
            var uri = new Uri(appConfig.ServerUrl);
            var cc = VideoOS.Platform.Login.Util.BuildCredentialCache(uri, appConfig.Username, appConfig.Password, appConfig.AuthenticationType);
            VideoOS.Platform.SDK.Environment.AddServer(uri, cc, false);
            VideoOS.Platform.SDK.Environment.Login(uri, false);
            AppState.ServerUri = uri;
            AppState.Credentials = cc;
        }

        static void ReLoginToMilestone()
        {
            if (AppState.ServerUri == null || AppState.Credentials == null)
                throw new InvalidOperationException("Không có thông tin đăng nhập để re-login.");

            VideoOS.Platform.SDK.Environment.Login(AppState.ServerUri, false);
        }

        static ResolveCamerasResult ResolveCameras(IEnumerable<string> cameraGuids)
        {
            var list = (cameraGuids ?? Array.Empty<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!list.Any()) return new ResolveCamerasResult { Success = false, ErrorMessage = "Chưa cấu hình CameraGuids hoặc request không có cameraGuids." };

            var invalid = list.Where(g => !Guid.TryParse(g, out _)).ToList();
            if (invalid.Any()) return new ResolveCamerasResult { Success = false, ErrorMessage = "GUID không hợp lệ: " + string.Join(", ", invalid) };

            var items = list.Select(g => Configuration.Instance.GetItem(Guid.Parse(g), Kind.Camera)).ToList();
            var missing = items.Select((item, i) => new { item, guid = list[i] }).Where(x => x.item == null).Select(x => x.guid).ToList();
            if (missing.Any()) return new ResolveCamerasResult { Success = false, ErrorMessage = "Không tìm thấy camera với GUID: " + string.Join(", ", missing) };

            return new ResolveCamerasResult { Success = true, Cameras = items };
        }

        static ResolveTimeResult ResolveTimeRange(string startArg, string endArg, int checkLastHours)
        {
            if (!string.IsNullOrWhiteSpace(startArg) || !string.IsNullOrWhiteSpace(endArg))
            {
                if (string.IsNullOrWhiteSpace(startArg) || string.IsNullOrWhiteSpace(endArg))
                    return new ResolveTimeResult { Success = false, ErrorMessage = "Phải truyền đủ cả startTime và endTime." };

                if (!DateTime.TryParse(startArg, out var startTime) || !DateTime.TryParse(endArg, out var endTime))
                    return new ResolveTimeResult { Success = false, ErrorMessage = "startTime/endTime không đúng định dạng yyyy-MM-dd HH:mm:ss." };

                return new ResolveTimeResult { Success = true, StartTime = startTime, EndTime = endTime };
            }

            var end = DateTime.Now;
            return new ResolveTimeResult { Success = true, StartTime = end.AddHours(-checkLastHours), EndTime = end };
        }

        static string GetArgValue(string[] args, string key)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        static AppConfig LoadConfiguration()
        {
            if (!File.Exists("config.json")) return null;
            return JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText("config.json"));
        }

        static void ListAllCameras()
        {
            var allCameras = Configuration.Instance.GetItems().SelectMany(GetCamerasRecursively).GroupBy(c => c.FQID.ObjectId).Select(g => g.First()).OrderBy(c => c.Name).ToList();
            Console.WriteLine($"Tìm thấy {allCameras.Count} camera:");
            foreach (var camera in allCameras) Console.WriteLine($"- {camera.Name} | GUID: {camera.FQID.ObjectId}");
        }

        static IEnumerable<Item> GetCamerasRecursively(Item item)
        {
            if (item == null) yield break;
            if (item.FQID.Kind == Kind.Camera) yield return item;
            var children = item.GetChildren();
            if (children == null) yield break;
            foreach (var child in children)
                foreach (var cam in GetCamerasRecursively(child))
                    yield return cam;
        }

        static CameraGapSummary CheckVideoGaps(Item cameraItem, DateTime start, DateTime end, bool verbose)
        {
            var summary = new CameraGapSummary { CameraName = cameraItem.Name, CameraGuid = cameraItem.FQID.ObjectId };
            var startUtc = start.ToUniversalTime();
            var endUtc = end.ToUniversalTime();
            var span = endUtc - startUtc;
            var source = new SequenceDataSource(cameraItem);
            var rawData = source.GetData(startUtc, span, 10000, span, 10000, DataType.SequenceTypeGuids.RecordingSequence);
            var sequences = rawData.OfType<SequenceData>().Select(sd => sd.EventSequence).Where(es => es != null && es.StartDateTime < endUtc && es.EndDateTime > startUtc).OrderBy(es => es.StartDateTime).ToList();

            if (verbose)
            {
                Console.WriteLine($"\nCamera: {cameraItem.Name}");
                Console.WriteLine($"   Khoảng thời gian: {start:dd/MM/yyyy HH:mm:ss} -> {end:dd/MM/yyyy HH:mm:ss}");
                Console.WriteLine($"   Tìm thấy {sequences.Count} đoạn recording.");
            }

            if (!sequences.Any())
            {
                summary.Gaps.Add(new GapRange { Start = start, End = end });
                return summary;
            }

            var currentPointer = startUtc;
            foreach (var seq in sequences)
            {
                if ((seq.StartDateTime - currentPointer).TotalMinutes > 5)
                    summary.Gaps.Add(new GapRange { Start = currentPointer.ToLocalTime(), End = seq.StartDateTime.ToLocalTime() });
                if (seq.EndDateTime > currentPointer) currentPointer = seq.EndDateTime;
            }

            if ((endUtc - currentPointer).TotalMinutes > 5)
                summary.Gaps.Add(new GapRange { Start = currentPointer.ToLocalTime(), End = endUtc.ToLocalTime() });

            return summary;
        }

        static void PrintOverallSummary(List<CameraGapSummary> summaries)
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
