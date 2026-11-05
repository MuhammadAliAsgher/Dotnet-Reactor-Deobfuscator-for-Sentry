using System.Diagnostics;
using System.IO.Compression;
using System.Text;

// Load test for the deobfuscation proxy. See README.md in this folder.
//   LoadTest makemap <mapRoot>
//   LoadTest stub <delayMs> [port]
//   LoadTest load <total> <systems> <concurrency> <gzip:0|1> <proxyUrl> <projectId>
// The client key is read from the LOADTEST_CLIENT_KEY environment variable, never from the command line.

const string Product = "LoadProbe";
const string FrameType = "Xk9Qz2Wp";
const string FrameMethod = "a1b2c3d4";
// 1.0.0 and 2.0.0 have a map (makemap), 9.9.9 has none and must be forwarded raw.
string[] releases = { "1.0.0", "1.0.0", "1.0.0", "2.0.0", "9.9.9" };

if (args.Length == 0) { Usage(); return 1; }

switch (args[0])
{
    case "makemap": return MakeMap(args);
    case "stub": return Stub(args);
    case "load": return await Load(args);
    default: Usage(); return 1;
}

static void Usage() => Console.Error.WriteLine(
    "usage:\n  LoadTest makemap <mapRoot>\n  LoadTest stub <delayMs> [port]\n" +
    "  LoadTest load <total> <systems> <concurrency> <gzip:0|1> <proxyUrl> <projectId>\n" +
    "env: LOADTEST_CLIENT_KEY (the proxy's Authentication:ClientKey)");

// Writes synthetic maps (no real obfuscation data) for the product used by the load generator.
int MakeMap(string[] a)
{
    if (a.Length < 2) { Usage(); return 1; }
    var dir = Path.Combine(a[1], Product);
    Directory.CreateDirectory(dir);
    foreach (var rel in new[] { "1.0.0", "2.0.0" })
    {
        File.WriteAllLines(Path.Combine(dir, rel + ".nrmap"), new[]
        {
            "<<type>>", FrameType, $"Acme.Load.Release{rel.Replace('.', '_')}Class", "Wp", "PADDING", FrameMethod, "Run"
        });
    }
    Console.WriteLine($"wrote synthetic maps to {dir}");
    return 0;
}

// Stand-in for Sentry: answers every POST after a fixed delay and counts what it received (GET /count).
int Stub(string[] a)
{
    int delay = a.Length > 1 ? int.Parse(a[1]) : 50;
    int port = a.Length > 2 ? int.Parse(a[2]) : 5099;
    long received = 0;
    var b = WebApplication.CreateBuilder();
    b.Logging.ClearProviders();
    var app = b.Build();
    app.MapPost("/{**p}", async (HttpContext c) =>
    {
        await c.Request.Body.CopyToAsync(Stream.Null);
        await Task.Delay(delay);
        Interlocked.Increment(ref received);
        return Results.Json(new { id = "x" });
    });
    app.MapGet("/count", () => Interlocked.Read(ref received));
    app.Run($"http://localhost:{port}");
    return 0;
}

async Task<int> Load(string[] a)
{
    if (a.Length < 7) { Usage(); return 1; }
    var key = Environment.GetEnvironmentVariable("LOADTEST_CLIENT_KEY");
    if (string.IsNullOrEmpty(key)) { Console.Error.WriteLine("LOADTEST_CLIENT_KEY is not set"); return 1; }

    int total = int.Parse(a[1]), systems = int.Parse(a[2]), conc = int.Parse(a[3]);
    bool gz = a[4] == "1";
    string url = a[5].TrimEnd('/') + $"/api/{a[6]}/envelope/";

    var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 2000 }) { Timeout = TimeSpan.FromSeconds(60) };
    var lat = new List<double>();
    var codes = new Dictionary<int, int>();
    var gate = new SemaphoreSlim(conc);
    var pkg = "Acme, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    long bodyBytes = 0;
    var sw = Stopwatch.StartNew();

    var tasks = Enumerable.Range(0, total).Select(async i =>
    {
        await gate.WaitAsync();
        try
        {
            int sys = i % systems;
            var rel = releases[sys % releases.Length];
            var id = Guid.NewGuid().ToString("N");
            var ev = $"{{\"event_id\":\"{id}\",\"release\":\"{rel}\",\"platform\":\"csharp\",\"tags\":{{\"product\":\"{Product}\"}}," +
                     $"\"user\":{{\"id\":\"sys-{sys}\"}},\"exception\":{{\"values\":[{{\"type\":\"T\",\"value\":\"load\",\"stacktrace\":{{\"frames\":[" +
                     $"{{\"function\":\"void {FrameType}.{FrameMethod}()\",\"package\":\"{pkg}\"}}]}}}}]}}}}";
            var body = $"{{\"event_id\":\"{id}\"}}\n{{\"type\":\"event\",\"length\":{Encoding.UTF8.GetByteCount(ev)}}}\n{ev}\n";
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            if (gz)
            {
                using var ms = new MemoryStream();
                using (var g = new GZipStream(ms, CompressionLevel.Fastest)) g.Write(bytes);
                bytes = ms.ToArray();
            }
            Interlocked.Add(ref bodyBytes, bytes.Length);

            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(bytes) };
            req.Content.Headers.ContentType = new("application/x-sentry-envelope");
            if (gz) req.Content.Headers.ContentEncoding.Add("gzip");
            req.Headers.TryAddWithoutValidation("X-Sentry-Auth", $"Sentry sentry_version=7, sentry_key={key}");

            var t0 = Stopwatch.GetTimestamp();
            int code;
            try { code = (int)(await http.SendAsync(req)).StatusCode; } catch { code = -1; }
            var ms2 = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            lock (lat) { lat.Add(ms2); codes[code] = codes.GetValueOrDefault(code) + 1; }
        }
        finally { gate.Release(); }
    }).ToArray();

    await Task.WhenAll(tasks);
    sw.Stop();
    lat.Sort();
    double P(double q) => lat[(int)Math.Min(lat.Count - 1, q * lat.Count)];

    Console.WriteLine($"total={total} systems={systems} conc={conc} gzip={gz} wall={sw.Elapsed.TotalSeconds:F2}s rps={total / sw.Elapsed.TotalSeconds:F0} avgBodyBytes={bodyBytes / Math.Max(1, total)}");
    Console.WriteLine($"status: {string.Join(", ", codes.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))}");
    Console.WriteLine($"latency ms p50={P(.5):F0} p95={P(.95):F0} p99={P(.99):F0} max={lat[^1]:F0}");
    return 0;
}
