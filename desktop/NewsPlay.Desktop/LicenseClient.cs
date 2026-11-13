using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace NewsPlay.Desktop;

internal sealed class LicenseClient
{
    private const string DefaultServer = "https://playout.tejtak.com/public";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NewsPlay.License.State.v1");
    private readonly string statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewsPlay", "license.bin");
    private State? state;
    public LicenseClient() => state = ReadState();
    public string Server => state?.Server ?? DefaultServer;
    public string Status => state is null ? "Not activated" : ValidateCached(out var error) ? $"Activated · {state.Claims?.Edition ?? "Standard"}" : "Needs attention · " + error;
    public void Reload() => state = ReadState();
    public bool NeedsRenewal => state is not null && DateTimeOffset.UtcNow > state.LastValidatedUtc.AddDays(7);
    public async Task RefreshIfDue() { if (NeedsRenewal) await Refresh(); }

    public async Task Activate(string server, string licenseKey)
    {
        var baseUri = NormalizeServer(server);
        if (string.IsNullOrWhiteSpace(licenseKey)) throw new InvalidOperationException("Enter the license key issued from the NewsPlay portal.");
        var installId = state?.InstallationId ?? Guid.NewGuid().ToString("D");
        var binding = MakeBinding(installId);
        using var keyResponse = await Http.GetAsync(new Uri(baseUri, "api/license/public-key"));
        keyResponse.EnsureSuccessStatusCode();
        var keyJson = await keyResponse.Content.ReadFromJsonAsync<KeyResponse>() ?? throw new InvalidOperationException("License server returned no public key.");
        if (!string.Equals(keyJson.Algorithm, "RS256", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(keyJson.PublicKey)) throw new InvalidOperationException("The license server returned an unsupported signing key.");
        if (state is not null && !string.Equals(NormalizePem(state.PublicKey), NormalizePem(keyJson.PublicKey), StringComparison.Ordinal)) throw new InvalidOperationException("The server signing key changed. Contact NewsPlay support for a coordinated key update; the existing key pin was preserved.");

        using var challengeResponse = await Http.PostAsJsonAsync(new Uri(baseUri, "api/v1/activations/challenge"), new
        {
            license_key = licenseKey.Trim(), installation_id = installId, device_binding = binding,
            device_name = Environment.MachineName, app_version = GetVersion()
        });
        await EnsureResponse(challengeResponse);
        var challenge = await challengeResponse.Content.ReadFromJsonAsync<Challenge>() ?? throw new InvalidOperationException("License server returned an invalid activation challenge.");
        using var completeResponse = await Http.PostAsJsonAsync(new Uri(baseUri, "api/v1/activations/complete"), new
        {
            challenge_id = challenge.ChallengeId, nonce = challenge.Nonce, device_name = Environment.MachineName
        });
        await EnsureResponse(completeResponse);
        var completed = await completeResponse.Content.ReadFromJsonAsync<ActivationResponse>() ?? throw new InvalidOperationException("License server returned no signed license.");
        if (string.IsNullOrWhiteSpace(completed.License) || string.IsNullOrWhiteSpace(completed.ActivationId)) throw new InvalidOperationException("Activation response is incomplete.");
        var claims = Verify(completed.License, keyJson.PublicKey);
        ValidateClaims(claims, binding);
        state = new State { Server = baseUri.ToString().TrimEnd('/'), InstallationId = installId, ActivationId = completed.ActivationId, DeviceBinding = binding, PublicKey = keyJson.PublicKey, License = completed.License, LastValidatedUtc = DateTimeOffset.UtcNow, GraceDays = Math.Clamp(completed.OfflineGraceDays, 1, 60) };
        state.Claims = claims;
        SaveState();
    }

    public async Task Refresh()
    {
        if (state is null || string.IsNullOrWhiteSpace(state.ActivationId)) throw new InvalidOperationException("Activate NewsPlay first.");
        using var response = await Http.PostAsJsonAsync(new Uri(NormalizeServer(state.Server), "api/v1/activations/renew"), new { activation_id = state.ActivationId, device_binding = MakeBinding(state.InstallationId) });
        await EnsureResponse(response);
        var renewed = await response.Content.ReadFromJsonAsync<RenewResponse>() ?? throw new InvalidOperationException("License server returned no renewal response.");
        var claims = Verify(renewed.License, state.PublicKey);
        ValidateClaims(claims, MakeBinding(state.InstallationId));
        state.License = renewed.License; state.Claims = claims; state.LastValidatedUtc = DateTimeOffset.UtcNow;
        SaveState();
    }

    public async Task Deactivate()
    {
        if (state is null || string.IsNullOrWhiteSpace(state.ActivationId)) throw new InvalidOperationException("This PC has no stored activation.");
        using var response = await Http.PostAsJsonAsync(new Uri(NormalizeServer(state.Server), "api/v1/activations/deactivate"), new { activation_id = state.ActivationId, device_binding = MakeBinding(state.InstallationId) });
        await EnsureResponse(response);
        if (File.Exists(statePath)) File.Delete(statePath);
        state = null;
    }

    public bool ValidateCached(out string message)
    {
        message = "No signed license is stored.";
        if (state is null) return false;
        try
        {
            var claims = Verify(state.License, state.PublicKey);
            ValidateClaims(claims, MakeBinding(state.InstallationId));
            var graceEnd = state.LastValidatedUtc.AddDays(Math.Clamp(state.GraceDays, 1, 60));
            if (DateTimeOffset.UtcNow > graceEnd) { message = "Online validation is overdue. Connect to the license server."; return false; }
            state.Claims = claims;
            message = DateTimeOffset.UtcNow > state.LastValidatedUtc.AddDays(7) ? "Valid · renewal recommended" : "Valid";
            return true;
        }
        catch (Exception ex) { message = ex.Message; return false; }
    }

    private static LicenseClaims Verify(string document, string publicKey)
    {
        var parts = document.Split('.');
        if (parts.Length != 2) throw new InvalidOperationException("Signed license format is invalid.");
        var payload = FromBase64Url(parts[0]); var signature = FromBase64Url(parts[1]);
        using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey);
        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new InvalidOperationException("License signature verification failed.");
        return JsonSerializer.Deserialize<LicenseClaims>(payload) ?? throw new InvalidOperationException("License claims are invalid.");
    }

    private static void ValidateClaims(LicenseClaims claims, string binding)
    {
        if (claims.Schema != 1 || !string.Equals(claims.Algorithm, "RS256", StringComparison.Ordinal) || !string.Equals(claims.Product, "NewsPlay", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("This signed license is not for this NewsPlay product.");
        if (!string.Equals(claims.DeviceBindingHash, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding))).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("This license is activated to a different PC.");
        if (claims.ExpiresUtc is { } expiry && DateTimeOffset.UtcNow > expiry) throw new InvalidOperationException("This license has expired.");
        if (claims.IssuedUtc > DateTimeOffset.UtcNow.AddMinutes(10)) throw new InvalidOperationException("License issue time is invalid.");
    }

    private State? ReadState()
    {
        try { if (!File.Exists(statePath)) return null; var protectedBytes = File.ReadAllBytes(statePath); return JsonSerializer.Deserialize<State>(ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }
    private void SaveState()
    {
        if (state is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
        File.WriteAllBytes(statePath, ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }
    private static string MakeBinding(string installId)
    {
        var machineId = (string?)Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?.GetValue("MachineGuid") ?? Environment.MachineName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("NewsPlay|" + machineId + "|" + installId))).ToLowerInvariant();
    }
    private static Uri NormalizeServer(string server)
    {
        if (!Uri.TryCreate(server.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Use the portal HTTPS base URL, for example https://playout.tejtak.com/public.");
        return uri;
    }
    private static async Task EnsureResponse(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        try { using var json = JsonDocument.Parse(body); throw new InvalidOperationException(json.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? $"License server error ({(int)response.StatusCode})." : $"License server error ({(int)response.StatusCode})."); }
        catch (JsonException) { throw new InvalidOperationException($"License server error ({(int)response.StatusCode}): {body}"); }
    }
    private static byte[] FromBase64Url(string value) { var text = value.Replace('-', '+').Replace('_', '/'); text += new string('=', (4 - text.Length % 4) % 4); return Convert.FromBase64String(text); }
    private static string NormalizePem(string pem) => string.Join("\n", pem.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    private static string GetVersion() => typeof(LicenseClient).Assembly.GetName().Version?.ToString() ?? "1.0.0";
    private sealed class State { public string Server { get; set; } = DefaultServer; public string InstallationId { get; set; } = ""; public string ActivationId { get; set; } = ""; public string DeviceBinding { get; set; } = ""; public string PublicKey { get; set; } = ""; public string License { get; set; } = ""; public DateTimeOffset LastValidatedUtc { get; set; } public int GraceDays { get; set; } = 14; [JsonIgnore] public LicenseClaims? Claims { get; set; } }
    private sealed class KeyResponse { [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = ""; [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = ""; }
    private sealed class Challenge { [JsonPropertyName("challenge_id")] public string ChallengeId { get; set; } = ""; [JsonPropertyName("nonce")] public string Nonce { get; set; } = ""; }
    private sealed class ActivationResponse { [JsonPropertyName("license")] public string License { get; set; } = ""; [JsonPropertyName("activation_id")] public string ActivationId { get; set; } = ""; [JsonPropertyName("offline_grace_days")] public int OfflineGraceDays { get; set; } = 14; }
    private sealed class RenewResponse { [JsonPropertyName("license")] public string License { get; set; } = ""; }
    private sealed class LicenseClaims { [JsonPropertyName("schema")] public int Schema { get; set; } [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = ""; [JsonPropertyName("product")] public string Product { get; set; } = ""; [JsonPropertyName("edition")] public string Edition { get; set; } = ""; [JsonPropertyName("issuedUtc")] public DateTimeOffset IssuedUtc { get; set; } [JsonPropertyName("expiresUtc")] public DateTimeOffset? ExpiresUtc { get; set; } [JsonPropertyName("deviceBindingHash")] public string? DeviceBindingHash { get; set; } }
}
