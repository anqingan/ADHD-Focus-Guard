using System.Security.Cryptography;
using System.Text.Json;
using Vigil.Core;

namespace Vigil.Infrastructure;

public sealed class JsonSettingsStore : IAppSettingsStore
{
    private static readonly byte[] Entropy = "Vigil.Windows.ApiKey.v1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsFile;
    private readonly string _secretFile;

    public JsonSettingsStore(string? settingsFile = null, string? secretFile = null)
    {
        AppPaths.EnsureCreated();
        _settingsFile = settingsFile ?? AppPaths.SettingsFile;
        _secretFile = secretFile ?? AppPaths.SecretFile;
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile) ?? AppPaths.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(_secretFile) ?? AppPaths.Root);
    }

    public async Task<ProviderSettings> LoadProviderAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(_settingsFile))
        {
            return ProviderSettings.Default;
        }

        try
        {
            await using var stream = File.OpenRead(_settingsFile);
            var document = await JsonSerializer.DeserializeAsync<SettingsDocument>(stream, cancellationToken: cancellationToken);
            var baseUrl = ProviderValidation.NormalizeBaseUrl(document?.BaseUrl ?? ProviderSettings.Default.BaseUrl);
            var model = string.IsNullOrWhiteSpace(document?.Model)
                ? ""
                : ProviderValidation.NormalizeModel(document.Model);
            var secret = await ReadSecretAsync(cancellationToken);
            var textModel = string.IsNullOrWhiteSpace(document?.TextModel)
                ? model
                : ProviderValidation.NormalizeModel(document.TextModel);
            return new ProviderSettings(baseUrl, model, secret?.BaseUrl == baseUrl) { TextModel = textModel };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await SimpleLog.WriteAsync("settings", $"读取配置失败：{ex.GetType().Name}");
            return ProviderSettings.Default;
        }
    }

    public async Task SaveProviderAsync(
        string baseUrl,
        string model,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var normalizedBaseUrl = ProviderValidation.NormalizeBaseUrl(baseUrl);
        var normalizedModel = ProviderValidation.NormalizeModel(model);
        if (apiKey.Length > 16_384)
        {
            throw new ArgumentException("API Key 过长。", nameof(apiKey));
        }

        AppPaths.EnsureCreated();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await WriteSecretAsync(new SecretDocument(normalizedBaseUrl, apiKey.Trim()), cancellationToken);
        }
        else
        {
            var existing = await ReadSecretAsync(cancellationToken);
            if (existing is null || existing.BaseUrl != normalizedBaseUrl)
            {
                throw new ArgumentException("首次配置或更改 Base URL 时必须重新输入 API Key。", nameof(apiKey));
            }
        }

        var existingDocument = await LoadSettingsDocumentAsync(cancellationToken);
        var document = new SettingsDocument(normalizedBaseUrl, normalizedModel, existingDocument.TextModel);
        var settingsBytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        await AtomicWriteAsync(_settingsFile, settingsBytes, cancellationToken);
    }

    public async Task SaveProviderModelsAsync(string baseUrl, string textModel, string visionModel, string apiKey, CancellationToken cancellationToken = default)
    {
        var normalizedBaseUrl = ProviderValidation.NormalizeBaseUrl(baseUrl);
        var normalizedText = ProviderValidation.NormalizeModel(textModel);
        var normalizedVision = ProviderValidation.NormalizeModel(visionModel);
        if (apiKey.Length > 16_384) throw new ArgumentException("API Key 过长。", nameof(apiKey));
        if (!string.IsNullOrWhiteSpace(apiKey)) await WriteSecretAsync(new SecretDocument(normalizedBaseUrl, apiKey.Trim()), cancellationToken);
        else
        {
            var existing = await ReadSecretAsync(cancellationToken);
            if (existing is null || existing.BaseUrl != normalizedBaseUrl) throw new ArgumentException("首次配置或更改 Base URL 时必须重新输入 API Key。", nameof(apiKey));
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SettingsDocument(normalizedBaseUrl, normalizedVision, normalizedText), JsonOptions);
        await AtomicWriteAsync(_settingsFile, bytes, cancellationToken);
    }

    public async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_secretFile))
        {
            return null;
        }
        var provider = await LoadSettingsDocumentAsync(cancellationToken);
        var secret = await ReadSecretAsync(cancellationToken);
        return secret is not null && secret.BaseUrl == provider.BaseUrl ? secret.ApiKey : null;
    }

    private async Task<SettingsDocument> LoadSettingsDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsFile))
        {
            return new SettingsDocument(ProviderSettings.Default.BaseUrl, "", ProviderSettings.Default.TextModel);
        }
        await using var stream = File.OpenRead(_settingsFile);
        var document = await JsonSerializer.DeserializeAsync<SettingsDocument>(stream, cancellationToken: cancellationToken)
                       ?? throw new InvalidDataException("配置文件为空。");
        return document with { BaseUrl = ProviderValidation.NormalizeBaseUrl(document.BaseUrl) };
    }

    private async Task WriteSecretAsync(SecretDocument secret, CancellationToken cancellationToken)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(secret);
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                await AtomicWriteAsync(_secretFile, protectedBytes, cancellationToken);
            }
            finally
            {
                Array.Clear(protectedBytes);
            }
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    private async Task<SecretDocument?> ReadSecretAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_secretFile))
        {
            return null;
        }
        var protectedBytes = await File.ReadAllBytesAsync(_secretFile, cancellationToken);
        try
        {
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<SecretDocument>(plain);
            }
            catch (JsonException)
            {
                // Unbound secrets from early development builds are deliberately
                // invalidated so a tampered settings file cannot redirect them.
                return null;
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            Array.Clear(protectedBytes);
        }
    }

    private static async Task AtomicWriteAsync(string destination, byte[] data, CancellationToken cancellationToken)
    {
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, data, cancellationToken);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record SettingsDocument(string BaseUrl, string Model, string? TextModel = null);
    private sealed record SecretDocument(string BaseUrl, string ApiKey);
}
