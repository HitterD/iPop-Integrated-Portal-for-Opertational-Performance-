using System.Net.Http.Json;
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
        foreach (var key in new[] { "spj", "krw", "jtb", "smg" })
        {
            var v = cfg[$"Synology:Servers:{key}"]?.Trim().TrimEnd('/') ?? cfg[$"Synology:{key}"]?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(v)) map[key] = v;
        }
        // Fallback per IP — DSM custom port 30001, HTTPS (nginx: plain HTTP -> 400)
        if (!map.ContainsKey("spj")) map["spj"] = "https://192.168.2.16:30001";
        if (!map.ContainsKey("krw")) map["krw"] = "https://10.12.2.6:30001";
        if (!map.ContainsKey("jtb")) map["jtb"] = "https://10.11.2.6:30001";
        if (!map.ContainsKey("smg")) map["smg"] = "https://10.14.2.16:30001";
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
        using var res = await http.GetAsync(url, ct);
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
        using var res = await http.GetAsync(url, ct);
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
        using var res = await http.GetAsync(url, ct);
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

    public async Task<Stream> DownloadAsync(string server, string sid, string path, CancellationToken ct)
    {
        var url = $"{BaseFor(server)}/webapi/entry.cgi?api=SYNO.FileStation.Download&version=2&method=download&path={Uri.EscapeDataString(path)}&_sid={Uri.EscapeDataString(sid)}&mode=download";
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
            throw new SynoException($"Synology HTTP {(int)res.StatusCode}", authFailure: false);
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        res.Dispose();
        return new MemoryStream(bytes);
    }

    public async Task UploadAsync(string server, string sid, string dir, string filename, Stream content, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("SYNO.FileStation.Upload"), "api" },
            { new StringContent("2"), "version" },
            { new StringContent("upload"), "method" },
            { new StringContent($"\"{dir}\""), "path" },
            { new StringContent("true"), "overwrite" },
            { new StringContent(sid), "_sid" },
        };
        form.Add(new StreamContent(content), "file", filename);
        using var res = await http.PostAsync($"{BaseFor(server)}/webapi/entry.cgi", form, ct);
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
    private record AdditionalDto(long Size, long Mtime);
    private record ErrorProbe(bool Success, ErrorObj? Error);
}
