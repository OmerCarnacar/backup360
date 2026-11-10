using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Backup360.Core;

/// <summary>Sağlayıcı adresleri. Testler sahte sunucuya yönlendirebilsin diye değiştirilebilir.</summary>
public static class Endpoints
{
    public static string GoogleAuth { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";
    public static string GoogleToken { get; set; } = "https://oauth2.googleapis.com/token";
    public static string DriveApi { get; set; } = "https://www.googleapis.com/drive/v3";
    public static string DriveUpload { get; set; } = "https://www.googleapis.com/upload/drive/v3";
    public static string MicrosoftAuth { get; set; } = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    public static string MicrosoftToken { get; set; } = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    public static string Graph { get; set; } = "https://graph.microsoft.com/v1.0";
    public static string DropboxAuth { get; set; } = "https://www.dropbox.com/oauth2/authorize";
    public static string DropboxToken { get; set; } = "https://api.dropboxapi.com/oauth2/token";
    public static string DropboxApi { get; set; } = "https://api.dropboxapi.com/2";
    public static string DropboxContent { get; set; } = "https://content.dropboxapi.com/2";
}

public sealed class OAuthToken
{
    public string Access { get; set; } = "";
    public string Refresh { get; set; } = "";
    public DateTime Expiry { get; set; }

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["access_token"] = Access, ["token_type"] = "Bearer", ["refresh_token"] = Refresh,
        ["expiry"] = Expiry.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
    });

    public static OAuthToken Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var token = new OAuthToken { Access = Text("access_token"), Refresh = Text("refresh_token") };
            if (DateTime.TryParse(Text("expiry"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expiry)) token.Expiry = expiry;
            if (token.Access.Length == 0 && token.Refresh.Length == 0) throw new ArgumentException("Token içinde access_token veya refresh_token yok.");
            return token;
        }
        catch (JsonException)
        {
            throw new ArgumentException("Token geçerli bir JSON değil. \"Token al\" düğmesini kullanın.");
        }
    }
}

/// <summary>RedirectHost ve Port: sağlayıcıya kayıtlı yönlendirme adresi. Port 0 ise boş bir port seçilir.</summary>
public sealed record OAuthApp(string AuthUrl, string TokenUrl, string ClientId, string? ClientSecret, string Scope, string Extra, string RedirectHost, int Port);

public static class OAuth
{
    /// <summary>OneDrive ve Dropbox yönlendirme adresini tam eşleşmeyle ister; bu yüzden port sabittir.</summary>
    public const int FixedPort = 53682;
    public static string FixedRedirect => $"http://localhost:{FixedPort}/";

    public static OAuthApp AppFor(TargetDefinition d)
    {
        switch (d.Type)
        {
            case "drive":
                var (id, secret) = GoogleClient.Resolve(d);
                if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Google Drive için Client ID gerekli.");
                // Tüm Drive'a erişim Google'ın denetimine tabidir; uygulamayla gelen kimlik yalnızca kendi dosyalarını görür.
                var full = d.FullAccess && !string.IsNullOrWhiteSpace(d.ClientId);
                return new OAuthApp(Endpoints.GoogleAuth, Endpoints.GoogleToken, id.Trim(), secret?.Trim(),
                    full ? "https://www.googleapis.com/auth/drive" : "https://www.googleapis.com/auth/drive.file", "&access_type=offline&prompt=consent", "127.0.0.1", 0);
            case "onedrive":
                if (string.IsNullOrWhiteSpace(d.ClientId)) throw new ArgumentException("OneDrive için Client ID gerekli.");
                return new OAuthApp(Endpoints.MicrosoftAuth, Endpoints.MicrosoftToken, d.ClientId.Trim(), null, "Files.ReadWrite.All offline_access", "", "localhost", FixedPort);
            case "dropbox":
                if (string.IsNullOrWhiteSpace(d.ClientId)) throw new ArgumentException("Dropbox için App key gerekli.");
                return new OAuthApp(Endpoints.DropboxAuth, Endpoints.DropboxToken, d.ClientId.Trim(), null, "", "&token_access_type=offline", "localhost", FixedPort);
            default:
                throw new ArgumentException("Bu hedef türü tarayıcı onayı kullanmaz.");
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Onay sayfasının adresini <paramref name="open"/> ile verir, tarayıcının bu bilgisayara
    /// geri dönmesini bekler ve dönen kodu token ile değiştirir (PKCE).
    /// </summary>
    public static async Task<OAuthToken> AuthorizeAsync(OAuthApp app, Action<string> open, CancellationToken ct)
    {
        var listeners = new List<TcpListener>();
        try
        {
            var first = new TcpListener(IPAddress.Loopback, app.Port);
            try { first.Start(); }
            catch (SocketException) { throw new IOException($"{app.Port} numaralı port başka bir program tarafından kullanılıyor. O programı kapatıp yeniden deneyin."); }
            listeners.Add(first);
            var port = ((IPEndPoint)first.LocalEndpoint).Port;
            if (app.RedirectHost == "localhost")
            {
                // Tarayıcı "localhost" için önce IPv6'yı deneyebilir.
                var v6 = new TcpListener(IPAddress.IPv6Loopback, port);
                try { v6.Start(); listeners.Add(v6); } catch (SocketException) { }
            }

            var redirect = $"http://{app.RedirectHost}:{port}/";
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var url = $"{app.AuthUrl}?client_id={Uri.EscapeDataString(app.ClientId)}&redirect_uri={Uri.EscapeDataString(redirect)}&response_type=code&state={state}" +
                      $"&code_challenge={challenge}&code_challenge_method=S256" + (app.Scope.Length > 0 ? "&scope=" + Uri.EscapeDataString(app.Scope) : "") + app.Extra;
            open(url);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            string code;
            try { code = await WaitForCodeAsync(listeners, state, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Hesap onayı 5 dakika içinde gelmedi."); }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["client_id"] = app.ClientId, ["code_verifier"] = verifier,
            };
            return await TokenAsync(app, form, null, ct);
        }
        finally
        {
            foreach (var l in listeners) l.Stop();
        }
    }

    private static async Task<string> WaitForCodeAsync(List<TcpListener> listeners, string state, CancellationToken ct)
    {
        var pending = listeners.Select(l => l.AcceptTcpClientAsync(ct).AsTask()).ToList();
        while (true)
        {
            var done = await Task.WhenAny(pending);
            var index = pending.IndexOf(done);
            pending[index] = listeners[index].AcceptTcpClientAsync(ct).AsTask();
            using var client = await done;
            var query = await ReadQueryAsync(client, ct);
            if (query == null || !query.ContainsKey("state")) { await ReplyAsync(client, 404, "Sayfa bulunamadı.", ct); continue; }
            if (query["state"] != state) { await ReplyAsync(client, 400, "Bu onay isteği geçersiz. Uygulamadan yeniden başlatın.", ct); continue; }
            if (query.TryGetValue("error", out var error))
            {
                await ReplyAsync(client, 200, "Onay verilmedi. Bu sekmeyi kapatıp Backup360'a dönebilirsiniz.", ct);
                throw new IOException("Hesap onayı verilmedi: " + (query.GetValueOrDefault("error_description") ?? error));
            }
            if (!query.TryGetValue("code", out var code)) { await ReplyAsync(client, 400, "Onay kodu gelmedi.", ct); continue; }
            await ReplyAsync(client, 200, "Onay alındı. Bu sekmeyi kapatıp Backup360'a dönebilirsiniz.", ct);
            return code;
        }
    }

    private static async Task<Dictionary<string, string>?> ReadQueryAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var buffer = new byte[16384];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var parts = Encoding.ASCII.GetString(buffer, 0, length).Split('\n')[0].Split(' ');
        if (parts.Length < 2 || parts[0] != "GET") return null;
        var cut = parts[1].IndexOf('?');
        var result = new Dictionary<string, string>();
        if (cut < 0) return result;
        foreach (var pair in parts[1][(cut + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0) result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return result;
    }

    private static async Task ReplyAsync(TcpClient client, int status, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes($"<!doctype html><html lang=\"tr\"><head><meta charset=\"utf-8\"><title>Backup360</title></head><body style=\"font-family:Segoe UI,sans-serif;text-align:center;margin-top:18vh\"><h2>Backup360</h2><p>{WebUtility.HtmlEncode(message)}</p></body></html>");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        var stream = client.GetStream();
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public static Task<OAuthToken> RefreshAsync(OAuthApp app, OAuthToken token, CancellationToken ct)
    {
        if (token.Refresh.Length == 0) throw new IOException("invalid_grant: oturum süresi doldu ve yenileme anahtarı yok.");
        return TokenAsync(app, new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = token.Refresh, ["client_id"] = app.ClientId }, token.Refresh, ct);
    }

    private static async Task<OAuthToken> TokenAsync(OAuthApp app, Dictionary<string, string> form, string? keepRefresh, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(app.ClientSecret)) form["client_secret"] = app.ClientSecret;
        using var response = await Web.IdleAsync(tok => Web.Client.PostAsync(app.TokenUrl, new FormUrlEncodedContent(form), tok), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new IOException($"Hesap onayı tamamlanamadı (HTTP {(int)response.StatusCode}): {Web.Trim(text)}");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n) ? n : 3600;
        return new OAuthToken
        {
            Access = root.GetProperty("access_token").GetString() ?? "",
            Refresh = root.TryGetProperty("refresh_token", out var r) && r.GetString() is { Length: > 0 } fresh ? fresh : keepRefresh ?? "",
            Expiry = DateTime.UtcNow.AddSeconds(seconds),
        };
    }
}

/// <summary>Geçerli erişim anahtarını verir; süresi dolunca yeniler ve hedef kaydına yazar.</summary>
public sealed class TokenSource
{
    private readonly string _name;
    private readonly OAuthApp _app;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OAuthToken _token;

    public TokenSource(TargetDefinition d)
    {
        _name = d.Name;
        _app = OAuth.AppFor(d);
        _token = OAuthToken.Parse(d.Token);
    }

    private bool Valid => _token.Access.Length > 0 && _token.Expiry > DateTime.UtcNow.AddMinutes(2);

    public void Invalidate() => _token.Expiry = DateTime.MinValue;

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (Valid) return _token.Access;
        await _gate.WaitAsync(ct);
        try
        {
            if (Valid) return _token.Access;
            // Çalışan başka bir süreç (zamanlanmış yedek) yenilemiş olabilir.
            if (TargetStore.Load(_name)?.Token is { Length: > 0 } stored && OAuthToken.Parse(stored) is { } other && other.Access != _token.Access && other.Expiry > DateTime.UtcNow.AddMinutes(2))
            {
                _token = other;
                return _token.Access;
            }
            _token = await OAuth.RefreshAsync(_app, _token, ct);
            TargetStore.SaveToken(_name, _token.ToJson());
            return _token.Access;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Kimlikli API çağrısı: yetki yenileme, hız sınırı ve geçici hatalarda bekleyip yeniden deneme.</summary>
public sealed class ApiClient(TokenSource tokens)
{
    private const int Attempts = 6;

    public static async Task<IOException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return new IOException($"HTTP {(int)response.StatusCode}: {Web.Trim(body, 600)}");
    }

    public static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct);
    }

    private static bool Throttled(HttpStatusCode status, string body) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500 ||
        (status == HttpStatusCode.Forbidden && (body.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) || body.Contains("userRateLimitExceeded", StringComparison.OrdinalIgnoreCase)));

    /// <summary><paramref name="make"/> her denemede yeni istek üretir. Yanıtı çağıran kapatır.</summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct, bool headersOnly = false, int seconds = 120)
    {
        for (var attempt = 1; ; attempt++)
        {
            var access = await tokens.GetAsync(ct);
            HttpResponseMessage response;
            try
            {
                response = await Web.IdleAsync(tok =>
                {
                    var request = make();
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                    return Web.Client.SendAsync(request, headersOnly ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, tok);
                }, ct, seconds);
            }
            catch (Exception e) when (e is HttpRequestException or IOException && attempt < Attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
            {
                response.Dispose();
                tokens.Invalidate();
                continue;
            }
            if (response.IsSuccessStatusCode || attempt >= Attempts) return response;
            if ((int)response.StatusCode is < 400 or 404 or 409) return response;

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!Throttled(response.StatusCode, body))
            {
                // Gövde okundu; çağıran yeniden okuyabilsin diye geri konur.
                var copy = new HttpResponseMessage(response.StatusCode) { Content = new StringContent(body), ReasonPhrase = response.ReasonPhrase };
                response.Dispose();
                return copy;
            }
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            response.Dispose();
            await Task.Delay(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, ct);
        }
    }

    public async Task<JsonDocument> JsonAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        using var response = await SendAsync(make, ct);
        await EnsureAsync(response, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
    }

    public static HttpRequestMessage Json(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body != null) request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }
}
