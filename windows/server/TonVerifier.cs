using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

sealed class TonError(int status, string detail) : Exception(detail) {
    public int Status { get; } = status;
}

sealed record TonSettings(string PublicOrigin, string Network, string Collection, string RpcEndpoint, string? GiftCollection = null,string? UsernameCollection = null,string? UpgradeCollection = null);

sealed class TonVerifier(string dataRoot) : IDisposable {
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly SemaphoreSlim startup = new(1, 1);
    Worker? worker;
    public TonSettings Settings() {
        var nextToApp = Path.Combine(Environment.GetEnvironmentVariable("CTRL_APP_DIRECTORY") ?? AppContext.BaseDirectory, "ctrl-ton.json");
        var path = Environment.GetEnvironmentVariable("CTRL_TON_CONFIG") ??
            (File.Exists(nextToApp) ? nextToApp : Path.Combine(dataRoot, "ctrl-ton.json"));
        if (!File.Exists(path)) throw new TonError(503, "Вход по NFT-номеру ещё не настроен на этом сервере.");
        try {
            var s = JsonSerializer.Deserialize<TonSettings>(File.ReadAllText(path), Json);
            if (s is null || s.Network is not ("-3" or "-239") || string.IsNullOrWhiteSpace(s.Collection)
                || !Uri.TryCreate(s.PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https"
                || origin.GetLeftPart(UriPartial.Authority) != s.PublicOrigin || origin.UserInfo.Length != 0
                || !Uri.TryCreate(s.RpcEndpoint, UriKind.Absolute, out var rpc) || rpc.UserInfo.Length != 0
                || !(rpc.Scheme == "https" || (rpc.IsLoopback && rpc.Scheme == "http"))) throw new JsonException();
            return s;
        } catch (Exception e) when (e is IOException or JsonException or ArgumentException) {
            throw new TonError(503, "Проверьте настройки входа по NFT-номеру на сервере.");
        }
    }

    static string Unpack(string root) {
        var assembly = typeof(TonVerifier).Assembly;
        var key = assembly.ManifestModule.ModuleVersionId.ToString("N");
        var parent = Path.Combine(root, "ton-runtime");
        Directory.CreateDirectory(parent);
        var dest = Path.Combine(parent, key);
        if (Directory.Exists(dest)) return dest;
        var stage = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".tmp");
        using var zip = assembly.GetManifestResourceStream("Ctrl.TonRuntime")!;
        ZipFile.ExtractToDirectory(zip, stage);
        try { Directory.Move(stage, dest); }
        catch (IOException) when (Directory.Exists(dest)) { Directory.Delete(stage, true); }
        return dest;
    }

    public async Task<JsonElement> Call(string method, object data, CancellationToken cancel = default) {
        Worker instance;
        await startup.WaitAsync(cancel);
        try {
            var settings = Settings();
            if (worker is null || worker.Settings != settings || worker.Stopped) {
                worker?.Dispose();
                var runtime=OperatingSystem.IsWindows()?Unpack(dataRoot):Path.Combine(AppContext.BaseDirectory,"verifier");
                worker = new Worker(runtime, dataRoot, settings);
            }
            instance = worker;
        } finally { startup.Release(); }
        try { return await instance.Call(method, data, cancel); }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException) {
            throw new TonError(503, "Сервис входа временно недоступен. Попробуйте ещё раз.");
        }
    }
    public void Dispose() => worker?.Dispose();

    sealed class Worker : IDisposable {
        readonly Process process;
        readonly ConcurrentDictionary<string,TaskCompletionSource<JsonElement>> pending = new();
        readonly SemaphoreSlim writing = new(1,1);
        public TonSettings Settings { get; }
        public bool Stopped => process.HasExited;
        public Worker(string runtime, string root, TonSettings settings) {
            Settings = settings;
            var info = new ProcessStartInfo(OperatingSystem.IsWindows()?Path.Combine(runtime,"node.exe"):"node") {
                WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
            };
            info.ArgumentList.Add(Path.Combine(runtime, "worker.mjs"));
            info.Environment["CTRL_TON_WORKER_CONFIG"] = JsonSerializer.Serialize(settings, Json);
            info.Environment["CTRL_TON_STORAGE"] = root;
            process = Process.Start(info) ?? throw new IOException("Verifier failed to start");
            _ = ReadReplies();
        }
        async Task ReadReplies() {
            try {
                while (await process.StandardOutput.ReadLineAsync() is { } line) {
                    using var doc = JsonDocument.Parse(line);
                    var reply = doc.RootElement;
                    if (pending.TryRemove(reply.GetProperty("id").GetString()!, out var task)) task.TrySetResult(reply.Clone());
                }
            } catch (Exception e) when (e is IOException or InvalidOperationException or JsonException) { }
            finally { foreach (var request in pending.Values) request.TrySetException(new IOException("Verifier disconnected")); }
        }
        public async Task<JsonElement> Call(string method, object data, CancellationToken cancel) {
            var id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            try {
                await writing.WaitAsync(cancel);
                try {
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {id,method,data}, Json));
                    await process.StandardInput.FlushAsync();
                } finally { writing.Release(); }
                var reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(36), cancel);
                var status = reply.GetProperty("status").GetInt32();
                var result = reply.GetProperty("data");
                if (status != 200) throw new TonError(status, result.GetProperty("error").GetString() ?? "Ошибка проверки номера.");
                return result;
            } finally { pending.TryRemove(id, out _); }
        }
        public void Dispose() {
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
        }
    }
}

// The verifier credential is usable only together with the user's random session token.
static class TonTicket {
    static byte[] Key(string token) => HMACSHA256.HashData(Convert.FromHexString(token), "ctrl-ton-ticket-v1"u8);
    public static string Protect(string token, string jwt) {
        var plain = Encoding.UTF8.GetBytes(jwt);
        var bytes = new byte[28 + plain.Length];
        RandomNumberGenerator.Fill(bytes.AsSpan(0,12));
        using var aes = new AesGcm(Key(token),16);
        aes.Encrypt(bytes.AsSpan(0,12), plain, bytes.AsSpan(28), bytes.AsSpan(12,16));
        return Convert.ToBase64String(bytes);
    }
    public static string Open(string token, string packed) {
        var bytes = Convert.FromBase64String(packed);
        var plain = new byte[bytes.Length-28];
        using var aes = new AesGcm(Key(token),16);
        aes.Decrypt(bytes.AsSpan(0,12), bytes.AsSpan(28), bytes.AsSpan(12,16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}

