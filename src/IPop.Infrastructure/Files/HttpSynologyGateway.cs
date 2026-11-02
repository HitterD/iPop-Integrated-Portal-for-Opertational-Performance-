using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using IPop.Application.Files;
using Microsoft.Extensions.Configuration;

namespace IPop.Infrastructure.Files;

public class HttpSynologyGateway(HttpClient http, IConfiguration cfg) : ISynologyGateway
{
    private readonly Dictionary<string, string> _bases = BuildBases(cfg);

    private static Dictionary<string, string> BuildBases(IConfiguration cfg)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var single = cfg["Synology:BaseUrl"]?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(single))
            map["spj"] = single;
        foreach (var key in new[] { "spj", "krw", "jtb", "smg", "ict" })
        {
            var v = cfg[$"Synology:{key}"]?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(v)) map[key] = v;
        }
        // Every configured server counts — the service-account NAS ("ict") is not one of the
        // four plant tabs, and a fixed key list silently dropped it (chat/mail attachments 502'd).
        foreach (var s in cfg.GetSection("Synology:Servers").GetChildren())
        {
            var v = s.Value?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(v)) map[s.Key] = v;
        }
        // Fallback per IP — DSM custom port 30001, HTTPS (nginx: plain HTTP -> 400)
        if (!map.ContainsKey("spj")) map["spj"] = "https://192.168.2.16:30001";
        if (!map.ContainsKey("krw")) map["krw"] = "https://10.12.2.6:30001";
        if (!map.ContainsKey("jtb")) map["jtb"] = "https://10.11.2.6:30001";
        if (!map.ContainsKey("smg")) map["smg"] = "https://10.14.2.16:30001";
        if (!map.ContainsKey("ict")) map["ict"] = "https://192.168.2.17:30001";
        return map;
    }

    private string BaseFor(string server)
    {
        if (_bases.TryGetValue(server.ToLowerInvariant(), out var b)) return b;
        throw new SynoException($"Unknown NAS server '{server}'", authFailure: false);
    }

    public async Task<string> AuthAsync(string server, string username, string password, CancellationToken ct)
    {
        var url = $"{BaseFor(server)}/webapi/auth.cgi?api=SYNO.API.Auth&version=3&method=login&account={Uri.EscapeDataString(username)}&passwd={Uri.EscapeDataString(password)}&session=FileStation&format=sid";
        using var res = await GetShortAsync(url, ct);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<AuthResp>(cancellationToken: ct);
        if (body is null) throw new SynoException("Empty auth response");
        if (!body.Success)
            throw new SynoException($"Synology auth failed (code {body.ErrorCode})", body.ErrorCode, authFailure: true);
        return body.Data!.Sid;
    }

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<string>> ListSharesAsync(string server, string sid, CancellationToken ct)
    {
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.List&version=2&method=list_share&_sid={Uri.EscapeDataString(sid)}";
        using var res = await GetShortAsync(url, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}: {json[..Math.Min(300, json.Length)]}", authFailure: false);
        var probe2 = System.Text.Json.JsonSerializer.Deserialize<ErrorProbe>(json, _jsonOpts);
        if (probe2 is not null && !probe2.Success)
        {
            var code = probe2.Error?.Code;
            if (code == 119) throw new SynoException("Session expired", code, sessionExpired: true);
            throw new SynoException($"Synology error {code}", code);
        }
        var body = System.Text.Json.JsonSerializer.Deserialize<ShareListResp>(json, _jsonOpts);
        return body?.Data?.Shares?.Select(s => s.Path).ToList() ?? [];
    }

    public async Task<IReadOnlyList<SynoItem>> ListFolderAsync(string server, string sid, string path, CancellationToken ct)
    {
        if (path == "/" || string.IsNullOrWhiteSpace(path))
        {
            var shares = await ListSharesAsync(server, sid, ct);
            return shares.Select(p => new SynoItem(p.TrimStart('/'), p, true, 0, 0)).ToList();
        }
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.List&version=2&method=list&folder_path={Uri.EscapeDataString(path)}&_sid={Uri.EscapeDataString(sid)}&additional=[\"real_path\",\"size\",\"time\"]";
        using var res = await GetShortAsync(url, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}: {json[..Math.Min(300, json.Length)]}", authFailure: false);
        var probe = System.Text.Json.JsonSerializer.Deserialize<ErrorProbe>(json, _jsonOpts);
        if (probe is not null && !probe.Success)
        {
            var code = probe.Error?.Code;
            if (code == 119) throw new SynoException("Session expired", code, sessionExpired: true);
            throw new SynoException($"Synology error {code}", code);
        }
        var body = System.Text.Json.JsonSerializer.Deserialize<FileListResp>(json, _jsonOpts);
        return body?.Data?.Files?.Select(f => new SynoItem(f.Name, f.Path, f.IsDir, f.Additional?.Size ?? 0, f.Additional?.Mtime ?? 0)).ToList() ?? [];
    }

    /// API calls (list, auth, rename, …) keep HttpClient's former 100 s default.
    internal static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(100);
    /// Uploads and downloads move files of up to 500 MB (mail attachments), also over plant links.
    internal static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(30);
    /// Downloads up to this size stay in memory; bigger ones are spooled to a temp file.
    internal const long InMemoryDownloadBytes = 16L * 1024 * 1024;

    private async Task<HttpResponseMessage> GetShortAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ShortTimeout);
        return await http.GetAsync(url, cts.Token); // content is buffered before this returns
    }

    public async Task<Stream> DownloadAsync(string server, string sid, string path, CancellationToken ct)
    {
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.Download&version=2&method=download&path={Uri.EscapeDataString(path)}&_sid={Uri.EscapeDataString(sid)}&mode=download";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TransferTimeout);
        ct = cts.Token;
        var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        var ct2 = res.Content.Headers.ContentType?.MediaType;
        if (ct2 is not null && ct2.Contains("json"))
        {
            var json = await res.Content.ReadAsStringAsync(ct);
            var probe = System.Text.Json.JsonSerializer.Deserialize<ErrorProbe>(json, _jsonOpts);
            if (probe is not null && !probe.Success)
            {
                var code = probe.Error?.Code;
                if (code == 119) throw new SynoException("Session expired", code, sessionExpired: true);
                throw new SynoException($"Synology error {code}", code);
            }
        }
        if (!res.IsSuccessStatusCode)
        {
            res.Dispose();
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}", authFailure: false);
        }
        using (res)
        {
            var length = res.Content.Headers.ContentLength;
            if (length is { } n && n <= InMemoryDownloadBytes)
                return new MemoryStream(await res.Content.ReadAsByteArrayAsync(ct));
            // Large (or unknown-size) files: a seekable temp file, deleted when the caller disposes it,
            // so a 500 MB attachment never sits in the API's memory.
            var spool = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            try
            {
                await using (var body = await res.Content.ReadAsStreamAsync(ct))
                    await body.CopyToAsync(spool, ct);
                spool.Position = 0;
                return spool;
            }
            catch
            {
                await spool.DisposeAsync();
                throw;
            }
        }
    }

    public async Task UploadAsync(string server, string sid, string dir, string filename, Stream content, CancellationToken ct)
    {
        var boundary = $"----SynoBoundary{Guid.NewGuid():N}";
        using var form = new SynologyUploadContent(boundary, dir, filename, content);
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.Upload&version=2&method=upload&_sid={Uri.EscapeDataString(sid)}";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TransferTimeout);
        ct = cts.Token;
        using var res = await http.PostAsync(url, form, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}: {json[..Math.Min(300, json.Length)]}", authFailure: false);
        var probe = System.Text.Json.JsonSerializer.Deserialize<ErrorProbe>(json, _jsonOpts);
        if (probe is not null && !probe.Success)
        {
            var code = probe.Error?.Code;
            if (code == 119) throw new SynoException("Session expired", code, sessionExpired: true);
            throw new SynoException($"Synology error {code}", code);
        }
    }

    private sealed class SynologyUploadContent : HttpContent
    {
        private readonly string _boundary;
        private readonly string _dir;
        private readonly string _filename;
        private readonly Stream _content;

        public SynologyUploadContent(string boundary, string dir, string filename, Stream content)
        {
            _boundary = boundary;
            _dir = dir;
            _filename = filename;
            _content = content;
            Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            var safeFilename = Path.GetFileName(_filename);
            var sb = new StringBuilder();
            sb.Append($"--{_boundary}\r\n");
            sb.Append("Content-Disposition: form-data; name=\"path\"\r\n\r\n");
            sb.Append($"{_dir}\r\n");

            sb.Append($"--{_boundary}\r\n");
            sb.Append("Content-Disposition: form-data; name=\"create_parents\"\r\n\r\n");
            sb.Append("true\r\n");

            sb.Append($"--{_boundary}\r\n");
            sb.Append("Content-Disposition: form-data; name=\"overwrite\"\r\n\r\n");
            sb.Append("true\r\n");

            sb.Append($"--{_boundary}\r\n");
            sb.Append($"Content-Disposition: form-data; name=\"file\"; filename=\"{safeFilename}\"\r\n");
            sb.Append("Content-Type: application/octet-stream\r\n\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
            await stream.WriteAsync(headerBytes, cancellationToken);

            await _content.CopyToAsync(stream, cancellationToken);

            var footerBytes = Encoding.UTF8.GetBytes($"\r\n--{_boundary}--\r\n");
            await stream.WriteAsync(footerBytes, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_content.CanSeek)
            {
                var safeFilename = Path.GetFileName(_filename);
                var sb = new StringBuilder();
                sb.Append($"--{_boundary}\r\n");
                sb.Append("Content-Disposition: form-data; name=\"path\"\r\n\r\n");
                sb.Append($"{_dir}\r\n");

                sb.Append($"--{_boundary}\r\n");
                sb.Append("Content-Disposition: form-data; name=\"create_parents\"\r\n\r\n");
                sb.Append("true\r\n");

                sb.Append($"--{_boundary}\r\n");
                sb.Append("Content-Disposition: form-data; name=\"overwrite\"\r\n\r\n");
                sb.Append("true\r\n");

                sb.Append($"--{_boundary}\r\n");
                sb.Append($"Content-Disposition: form-data; name=\"file\"; filename=\"{safeFilename}\"\r\n");
                sb.Append("Content-Type: application/octet-stream\r\n\r\n");

                var headerLen = Encoding.UTF8.GetByteCount(sb.ToString());
                var footerLen = Encoding.UTF8.GetByteCount($"\r\n--{_boundary}--\r\n");
                length = headerLen + _content.Length + footerLen;
                return true;
            }

            length = 0;
            return false;
        }
    }

    public async Task DeleteAsync(string server, string sid, string path, CancellationToken ct)
    {
        // Blocking "delete" method: returns once the file is gone, so callers can rely on it.
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.Delete&version=2&method=delete&path={Uri.EscapeDataString(path)}&_sid={Uri.EscapeDataString(sid)}";
        using var res = await GetShortAsync(url, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}: {json[..Math.Min(300, json.Length)]}", authFailure: false);
        var probe = System.Text.Json.JsonSerializer.Deserialize<ErrorProbe>(json, _jsonOpts);
        if (probe is not null && !probe.Success)
        {
            var code = probe.Error?.Code;
            if (code == 119) throw new SynoException("Session expired", code, sessionExpired: true);
            throw new SynoException($"Synology error {code}", code);
        }
    }

    public async Task RenameAsync(string server, string sid, string path, string name, CancellationToken ct)
    {
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.Rename&version=2&method=rename&path={Uri.EscapeDataString(path)}&name={Uri.EscapeDataString(name)}&_sid={Uri.EscapeDataString(sid)}";
        using var res = await GetShortAsync(url, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}: {json[..Math.Min(300, json.Length)]}", authFailure: false);
        var probe = System.Text.Json.JsonSerializer.Deserialize<ErrorProbe>(json, _jsonOpts);
        if (probe is not null && !probe.Success)
        {
            var code = probe.Error?.Code;
            if (code == 119) throw new SynoException("Session expired", code, sessionExpired: true);
            throw new SynoException($"Synology error {code}", code);
        }
    }

    public async Task CreateFolderAsync(string server, string sid, string folderPath, string name, CancellationToken ct)
    {
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.CreateFolder&version=2&method=create&folder_path={Uri.EscapeDataString(folderPath)}&name={Uri.EscapeDataString(name)}&_sid={Uri.EscapeDataString(sid)}";
        using var res = await GetShortAsync(url, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}: {json[..Math.Min(300, json.Length)]}", authFailure: false);
        var probe = System.Text.Json.JsonSerializer.Deserialize<ErrorProbe>(json, _jsonOpts);
        if (probe is not null && !probe.Success)
        {
            var code = probe.Error?.Code;
            if (code == 119) throw new SynoException("Session expired", code, sessionExpired: true);
            throw new SynoException($"Synology error {code}", code);
        }
    }

    public async Task CopyMoveAsync(string server, string sid, IEnumerable<string> paths, string destDir, bool removeSrc, bool overwrite, CancellationToken ct)
    {
        var pathList = string.Join(",", paths);
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.CopyMove&version=3&method=start&path={Uri.EscapeDataString(pathList)}&dest_folder_path={Uri.EscapeDataString(destDir)}&remove_src={removeSrc.ToString().ToLowerInvariant()}&overwrite={overwrite.ToString().ToLowerInvariant()}&_sid={Uri.EscapeDataString(sid)}";
        using var res = await GetShortAsync(url, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}: {json[..Math.Min(300, json.Length)]}", authFailure: false);
        var probe = System.Text.Json.JsonSerializer.Deserialize<ErrorProbe>(json, _jsonOpts);
        if (probe is not null && !probe.Success)
        {
            var code = probe.Error?.Code;
            if (code == 119) throw new SynoException("Session expired", code, sessionExpired: true);
            throw new SynoException($"Synology error {code}", code);
        }
    }

    public async Task<SynoShareLink> CreateSharingLinkAsync(string server, string sid, string path, string? password, int? expiresInDays, CancellationToken ct)
    {
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.Sharing&version=3&method=create&path={Uri.EscapeDataString(path)}&_sid={Uri.EscapeDataString(sid)}";
        if (!string.IsNullOrWhiteSpace(password)) url += $"&password={Uri.EscapeDataString(password)}";
        if (expiresInDays.HasValue) url += $"&date_expired={DateTime.UtcNow.AddDays(expiresInDays.Value):yyyy-MM-dd}";

        using var res = await GetShortAsync(url, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}: {json[..Math.Min(300, json.Length)]}", authFailure: false);
        var probe = System.Text.Json.JsonSerializer.Deserialize<ErrorProbe>(json, _jsonOpts);
        if (probe is not null && !probe.Success)
        {
            var code = probe.Error?.Code;
            if (code == 119) throw new SynoException("Session expired", code, sessionExpired: true);
            throw new SynoException($"Synology error {code}", code);
        }

        var resp = System.Text.Json.JsonSerializer.Deserialize<SharingCreateResp>(json, _jsonOpts);
        var link = resp?.Data?.Links?.FirstOrDefault();
        var id = link?.Id ?? Guid.NewGuid().ToString("N");
        var shareUrl = link?.Url ?? $"https://{server.ToLowerInvariant()}.sja.quickconnect.to/sharing/{id}";
        return new SynoShareLink(id, shareUrl, path, expiresInDays.HasValue ? DateTime.UtcNow.AddDays(expiresInDays.Value) : null, !string.IsNullOrWhiteSpace(password));
    }

    public async Task<IReadOnlyList<SynoShareLink>> ListSharingLinksAsync(string server, string sid, CancellationToken ct)
    {
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.Sharing&version=3&method=list&_sid={Uri.EscapeDataString(sid)}";
        using var res = await GetShortAsync(url, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            return [];
        var probe = System.Text.Json.JsonSerializer.Deserialize<ErrorProbe>(json, _jsonOpts);
        if (probe is not null && !probe.Success)
            return [];

        var resp = System.Text.Json.JsonSerializer.Deserialize<SharingCreateResp>(json, _jsonOpts);
        return resp?.Data?.Links?.Select(l => new SynoShareLink(l.Id, l.Url, l.Path, null, l.HasPassword)).ToList() ?? [];
    }

    public async Task DeleteSharingLinkAsync(string server, string sid, string id, CancellationToken ct)
    {
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.Sharing&version=3&method=delete&id={Uri.EscapeDataString(id)}&_sid={Uri.EscapeDataString(sid)}";
        using var res = await GetShortAsync(url, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}: {json[..Math.Min(300, json.Length)]}", authFailure: false);
    }

    public async Task<string> SearchStartAsync(string server, string sid, IReadOnlyList<string> folderPaths, string pattern, CancellationToken ct)
    {
        // DSM matches the pattern as a glob on the name; wrap it so "invoice" finds "2026 invoice march.pdf".
        var glob = pattern.Contains('*') || pattern.Contains('?') ? pattern : $"*{pattern}*";
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.Search&version=2&method=start&folder_path={Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(folderPaths))}&pattern={Uri.EscapeDataString(glob)}&recursive=true&_sid={Uri.EscapeDataString(sid)}";
        var json = await GetCheckedAsync(url, ct);
        var body = System.Text.Json.JsonSerializer.Deserialize<SearchStartResp>(json, _jsonOpts);
        return body?.Data?.TaskId ?? throw new SynoException("Search did not return a task id");
    }

    public async Task<SynoSearchPage> SearchListAsync(string server, string sid, string taskId, int offset, int limit, CancellationToken ct)
    {
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.Search&version=2&method=list&taskid={Uri.EscapeDataString(taskId)}&offset={offset}&limit={limit}&additional=[\"size\",\"time\"]&_sid={Uri.EscapeDataString(sid)}";
        var json = await GetCheckedAsync(url, ct);
        var data = System.Text.Json.JsonSerializer.Deserialize<SearchListResp>(json, _jsonOpts)?.Data;
        var items = data?.Files?.Select(f => new SynoItem(f.Name, f.Path, f.IsDir, f.Additional?.Size ?? 0, f.Additional?.Mtime ?? 0)).ToList() ?? [];
        return new SynoSearchPage(items, data?.Total ?? items.Count, data?.Finished ?? true);
    }

    public async Task SearchStopAsync(string server, string sid, string taskId, CancellationToken ct)
    {
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.Search&version=2&method=clean&taskid={Uri.EscapeDataString(JsonList(taskId))}&_sid={Uri.EscapeDataString(sid)}";
        await GetCheckedAsync(url, ct);
    }

    private static string JsonList(string value) => System.Text.Json.JsonSerializer.Serialize(new[] { value });

    /// <summary>GET a FileStation call and map its error envelope the way the calls above do inline.</summary>
    private async Task<string> GetCheckedAsync(string url, CancellationToken ct)
    {
        using var res = await GetShortAsync(url, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}", authFailure: false);
        var probe = System.Text.Json.JsonSerializer.Deserialize<ErrorProbe>(json, _jsonOpts);
        if (probe is not null && !probe.Success)
        {
            var code = probe.Error?.Code;
            if (code == 119) throw new SynoException("Session expired", code, sessionExpired: true);
            throw new SynoException($"Synology error {code}", code);
        }
        return json;
    }

    public async Task<SynoStorageInfo> GetStorageInfoAsync(string server, string sid, CancellationToken ct)
    {
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.Info&version=2&method=get&_sid={Uri.EscapeDataString(sid)}";
        using var res = await GetShortAsync(url, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}", authFailure: false);
        var probe = System.Text.Json.JsonSerializer.Deserialize<ErrorProbe>(json, _jsonOpts);
        if (probe is not null && !probe.Success)
        {
            var code = probe.Error?.Code;
            if (code == 119) throw new SynoException("Session expired", code, sessionExpired: true);
            throw new SynoException($"Synology error {code}", code);
        }
        // A NAS that doesn't report volume totals yields 0/0 — the page hides the meter rather
        // than inventing a figure (it used to fall back to a made-up "3.1 TB of 8 TB").
        var resp = System.Text.Json.JsonSerializer.Deserialize<StorageInfoResp>(json, _jsonOpts);
        var total = resp?.Data?.TotalBytes ?? 0;
        var used = resp?.Data?.UsedBytes ?? 0;
        return new SynoStorageInfo(server, used, total, Math.Max(0, total - used));
    }

    private record AuthResp(bool Success, AuthData? Data, [property: JsonPropertyName("error")] ErrorObj? Error)
    {
        public int? ErrorCode => Error?.Code;
    }
    private record AuthData([property: JsonPropertyName("sid")] string Sid);
    private record ErrorObj([property: JsonPropertyName("code")] int Code);
    private record ShareListResp(bool Success, ShareListData? Data);
    private record ShareListData([property: JsonPropertyName("shares")] List<ShareDto> Shares);
    private record ShareDto(string Path, string Name);
    private record FileListResp(bool Success, FileListData? Data);
    private record FileListData([property: JsonPropertyName("files")] List<FileDto> Files);
    private record FileDto(string Name, string Path, [property: JsonPropertyName("isdir")] bool IsDir, AdditionalDto? Additional);
    // FileStation nests timestamps: additional.time = { mtime, crtime, atime, ctime } (unix seconds).
    private record AdditionalDto(long Size, TimeDto? Time)
    {
        public long Mtime => Time?.Mtime ?? 0;
    }
    private record TimeDto(long Mtime);
    private record ErrorProbe(bool Success, ErrorObj? Error);
    private record SearchStartResp(bool Success, SearchStartData? Data);
    private record SearchStartData([property: JsonPropertyName("taskid")] string TaskId);
    private record SearchListResp(bool Success, SearchListData? Data);
    private record SearchListData(int Total, bool Finished, [property: JsonPropertyName("files")] List<FileDto>? Files);
    private record SharingCreateResp(bool Success, SharingData? Data);
    private record SharingData([property: JsonPropertyName("links")] List<ShareLinkItemDto> Links);
    private record ShareLinkItemDto(string Id, string Url, string Path, [property: JsonPropertyName("has_password")] bool HasPassword);
    private record StorageInfoResp(bool Success, StorageData? Data);
    private record StorageData([property: JsonPropertyName("total")] long TotalBytes, [property: JsonPropertyName("used")] long UsedBytes);
}
