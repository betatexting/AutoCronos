using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutoCronos.Desktop.Application;
using AutoCronos.Desktop.Domain;

namespace AutoCronos.Desktop.Services;

public sealed class GmailEmailIntegrationService
{
    private const string SettingsFileName = "gmail-integration.json";
    private const string InboxLabelName = "AutoCronos/Entrada";
    private const string ProcessedLabelName = "AutoCronos/Processado";
    private const string PendingLabelName = "AutoCronos/Pendente";
    private const string IgnoredLabelName = "AutoCronos/Ignorado";
    private const int MaxMessagesPerSync = 25;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly HttpClient _httpClient = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly string _settingsPath;

    private GmailIntegrationSettings _settings;
    private string? _lastMessage;

    public GmailEmailIntegrationService()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCronos");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, SettingsFileName);
        _settings = LoadSettings();
    }

    public EmailSettingsSnapshot GetSettingsSnapshot() =>
        new(_settings.ClientId, _settings.ClientSecret, _settings.EmailAddress, _settings.LastSyncAtUtc);

    public EmailConnectionStatus GetStatus()
    {
        var isConfigured = !string.IsNullOrWhiteSpace(_settings.ClientId);
        var isConnected = isConfigured && !string.IsNullOrWhiteSpace(_settings.RefreshToken);
        var statusText = isConnected
            ? "Gmail conectado"
            : isConfigured
                ? "Gmail configurado, aguardando conexao"
                : "Gmail nao configurado";

        var detailText = _lastMessage;
        if (string.IsNullOrWhiteSpace(detailText))
        {
            detailText = isConnected
                ? BuildConnectedDetail()
                : isConfigured
                    ? "Use Conectar para autorizar o acesso a sua caixa."
                    : "Informe o Client ID do Google para habilitar a integracao.";
        }

        return new EmailConnectionStatus(isConfigured, isConnected, statusText, detailText, _settings.EmailAddress, _settings.LastSyncAtUtc);
    }

    public void SaveSettings(string clientId, string clientSecret)
    {
        _settings.ClientId = clientId.Trim();
        _settings.ClientSecret = clientSecret.Trim();
        SaveSettings();
        _lastMessage = string.IsNullOrWhiteSpace(_settings.ClientId)
            ? "Configuracao vazia. Preencha o Client ID do Google."
            : "Configuracao do Gmail salva.";
    }

    public async Task<EmailConnectionStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.ClientId))
            {
                _lastMessage = "Preencha o Client ID antes de conectar.";
                return GetStatus();
            }

            var authorization = await AuthorizeAsync(cancellationToken);
            _settings.RefreshToken = authorization.RefreshToken;
            _settings.EmailAddress = authorization.EmailAddress;
            SaveSettings();

            await EnsureLabelsAsync(authorization.AccessToken, cancellationToken);
            _lastMessage = $"Conectado como {authorization.EmailAddress}. Marcadores verificados no Gmail.";
            return GetStatus();
        }
        catch (Exception exception)
        {
            _lastMessage = $"Falha ao conectar no Gmail: {SimplifyException(exception)}";
            return GetStatus();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<EmailConnectionStatus> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            _settings.RefreshToken = null;
            _settings.EmailAddress = null;
            SaveSettings();
            _lastMessage = "Conta do Gmail desconectada.";
            return GetStatus();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<EmailSyncSummary> SyncAsync(
        IReadOnlyList<string> subjectPatterns,
        Func<EmailInput, EmailProcessingResult> processEmail,
        CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.ClientId) || string.IsNullOrWhiteSpace(_settings.RefreshToken))
            {
                var message = "Conecte uma conta do Gmail antes de sincronizar.";
                _lastMessage = message;
                return new EmailSyncSummary(false, true, 0, 0, 0, 0, 0, message);
            }

            var accessToken = await RefreshAccessTokenAsync(cancellationToken);
            var labels = await EnsureLabelsAsync(accessToken, cancellationToken);
            var query = BuildSearchQuery(subjectPatterns);
            var messageIds = await SearchMessagesAsync(accessToken, query, cancellationToken);

            var scanned = 0;
            var createdProcesses = 0;
            var createdApprovals = 0;
            var ignoredMessages = 0;
            var failedMessages = 0;

            foreach (var messageId in messageIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;

                try
                {
                    var message = await GetMessageAsync(accessToken, messageId, cancellationToken);
                    var input = ConvertToEmailInput(message);
                    var result = processEmail(input);

                    var addLabelIds = new List<string> { labels.InboxLabelId };
                    if (result.ApprovalId is not null)
                    {
                        addLabelIds.Add(labels.PendingLabelId);
                        createdApprovals++;
                    }
                    else if (ShouldIgnore(result))
                    {
                        addLabelIds.Add(labels.IgnoredLabelId);
                        ignoredMessages++;
                    }
                    else
                    {
                        addLabelIds.Add(labels.ProcessedLabelId);
                        if (result.CreatedProcess) createdProcesses++;
                    }

                    await ModifyLabelsAsync(accessToken, messageId, addLabelIds, [labels.PendingLabelId, labels.ProcessedLabelId, labels.IgnoredLabelId], cancellationToken);
                }
                catch (Exception exception)
                {
                    failedMessages++;
                    _lastMessage = $"Ultima falha na sincronizacao: {SimplifyException(exception)}";
                }
            }

            _settings.LastSyncAtUtc = DateTime.UtcNow;
            SaveSettings();

            var summary = scanned == 0
                ? "Sincronizacao concluida. Nenhum e-mail novo encontrado para as regras monitoradas."
                : $"Sincronizacao concluida: {scanned} e-mail(s), {createdProcesses} processo(s), {createdApprovals} pendencia(s), {ignoredMessages} ignorado(s), {failedMessages} falha(s).";

            _lastMessage = summary;
            return new EmailSyncSummary(failedMessages == 0, false, scanned, createdProcesses, createdApprovals, ignoredMessages, failedMessages, summary);
        }
        catch (Exception exception)
        {
            var message = $"Falha ao sincronizar com o Gmail: {SimplifyException(exception)}";
            _lastMessage = message;
            return new EmailSyncSummary(false, false, 0, 0, 0, 0, 1, message);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task MarkPendingMessageHandledAsync(string providerMessageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ClientId) || string.IsNullOrWhiteSpace(_settings.RefreshToken) || string.IsNullOrWhiteSpace(providerMessageId))
            return;

        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            var accessToken = await RefreshAccessTokenAsync(cancellationToken);
            var labels = await EnsureLabelsAsync(accessToken, cancellationToken);
            await ModifyLabelsAsync(accessToken, providerMessageId, [labels.InboxLabelId, labels.ProcessedLabelId], [labels.PendingLabelId, labels.IgnoredLabelId], cancellationToken);
            _lastMessage = "Marcadores do Gmail atualizados apos a decisao da pendencia.";
        }
        catch (Exception exception)
        {
            _lastMessage = $"A pendencia foi resolvida localmente, mas houve falha ao atualizar os marcadores no Gmail: {SimplifyException(exception)}";
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<GmailAuthorizationResult> AuthorizeAsync(CancellationToken cancellationToken)
    {
        var redirectPort = GetAvailablePort();
        var redirectUri = $"http://127.0.0.1:{redirectPort}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var verifier = CreateCodeVerifier();
        var challenge = CreateCodeChallenge(verifier);
        var scope = Uri.EscapeDataString("openid email https://www.googleapis.com/auth/gmail.modify https://www.googleapis.com/auth/gmail.labels");
        var authorizationUrl =
            $"https://accounts.google.com/o/oauth2/v2/auth?client_id={Uri.EscapeDataString(_settings.ClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code&scope={scope}&access_type=offline&prompt=consent&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256";

        Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });

        var contextTask = listener.GetContextAsync();
        var completedTask = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(3), cancellationToken));
        if (completedTask != contextTask)
            throw new TimeoutException("A autorizacao expirou antes do retorno do Google.");

        var context = await contextTask;
        var authorizationCode = context.Request.QueryString["code"];
        var error = context.Request.QueryString["error"];
        await RespondToBrowserAsync(context.Response, error is null, cancellationToken);

        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"O Google recusou a autorizacao: {error}.");
        if (string.IsNullOrWhiteSpace(authorizationCode))
            throw new InvalidOperationException("O Google nao retornou o codigo de autorizacao.");

        var tokenResponse = await ExchangeAuthorizationCodeAsync(authorizationCode, verifier, redirectUri, cancellationToken);
        var emailAddress = await GetEmailAddressAsync(tokenResponse.AccessToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
            throw new InvalidOperationException("O Google nao retornou refresh token. Revogue o acesso anterior e tente novamente.");

        return new GmailAuthorizationResult(tokenResponse.AccessToken, tokenResponse.RefreshToken, emailAddress);
    }

    private async Task<string> RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string?>
        {
            ["client_id"] = _settings.ClientId,
            ["client_secret"] = string.IsNullOrWhiteSpace(_settings.ClientSecret) ? null : _settings.ClientSecret,
            ["refresh_token"] = _settings.RefreshToken,
            ["grant_type"] = "refresh_token"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(values.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value!))
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var document = await ReadJsonAsync(response, cancellationToken);
        return GetRequiredString(document.RootElement, "access_token");
    }

    private async Task<GmailTokenResponse> ExchangeAuthorizationCodeAsync(string authorizationCode, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string?>
        {
            ["client_id"] = _settings.ClientId,
            ["client_secret"] = string.IsNullOrWhiteSpace(_settings.ClientSecret) ? null : _settings.ClientSecret,
            ["code"] = authorizationCode,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(values.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value!))
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var document = await ReadJsonAsync(response, cancellationToken);
        return new GmailTokenResponse(
            GetRequiredString(document.RootElement, "access_token"),
            GetRequiredString(document.RootElement, "refresh_token"));
    }

    private async Task<string> GetEmailAddressAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var document = await ReadJsonAsync(response, cancellationToken);
        return GetRequiredString(document.RootElement, "email");
    }

    private async Task<GmailLabelSet> EnsureLabelsAsync(string accessToken, CancellationToken cancellationToken)
    {
        var labels = await ListLabelsAsync(accessToken, cancellationToken);
        var inboxLabelId = await EnsureLabelIdAsync(accessToken, labels, InboxLabelName, cancellationToken);
        var processedLabelId = await EnsureLabelIdAsync(accessToken, labels, ProcessedLabelName, cancellationToken);
        var pendingLabelId = await EnsureLabelIdAsync(accessToken, labels, PendingLabelName, cancellationToken);
        var ignoredLabelId = await EnsureLabelIdAsync(accessToken, labels, IgnoredLabelName, cancellationToken);
        return new GmailLabelSet(inboxLabelId, processedLabelId, pendingLabelId, ignoredLabelId);
    }

    private async Task<Dictionary<string, string>> ListLabelsAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/labels", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var document = await ReadJsonAsync(response, cancellationToken);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!document.RootElement.TryGetProperty("labels", out var labelsElement))
            return result;

        foreach (var labelElement in labelsElement.EnumerateArray())
        {
            if (!labelElement.TryGetProperty("name", out var nameElement) || !labelElement.TryGetProperty("id", out var idElement))
                continue;

            var name = nameElement.GetString();
            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
                continue;

            result[name] = id;
        }

        return result;
    }

    private async Task<string> EnsureLabelIdAsync(string accessToken, Dictionary<string, string> labels, string labelName, CancellationToken cancellationToken)
    {
        if (labels.TryGetValue(labelName, out var existingId))
            return existingId;

        var payload = JsonSerializer.Serialize(new
        {
            name = labelName,
            labelListVisibility = "labelShow",
            messageListVisibility = "show"
        });

        using var request = CreateAuthorizedRequest(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/labels", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var document = await ReadJsonAsync(response, cancellationToken);
        var id = GetRequiredString(document.RootElement, "id");
        labels[labelName] = id;
        return id;
    }

    private async Task<IReadOnlyList<string>> SearchMessagesAsync(string accessToken, string query, CancellationToken cancellationToken)
    {
        var url = $"https://gmail.googleapis.com/gmail/v1/users/me/messages?q={Uri.EscapeDataString(query)}&maxResults={MaxMessagesPerSync}";
        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var document = await ReadJsonAsync(response, cancellationToken);

        if (!document.RootElement.TryGetProperty("messages", out var messagesElement))
            return [];

        return messagesElement
            .EnumerateArray()
            .Select(messageElement => messageElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();
    }

    private async Task<GmailMessage> GetMessageAsync(string accessToken, string messageId, CancellationToken cancellationToken)
    {
        var url = $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{messageId}?format=full";
        using var request = CreateAuthorizedRequest(HttpMethod.Get, url, accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var document = await ReadJsonAsync(response, cancellationToken);

        var subject = GetHeader(document.RootElement, "Subject") ?? "(sem assunto)";
        var body = ExtractBody(document.RootElement);
        var internalDate = document.RootElement.TryGetProperty("internalDate", out var internalDateElement)
            ? ParseInternalDate(internalDateElement.GetString())
            : DateTime.UtcNow;

        return new GmailMessage(messageId, subject, body, internalDate);
    }

    private async Task ModifyLabelsAsync(string accessToken, string messageId, IReadOnlyCollection<string> addLabelIds, IReadOnlyCollection<string> removeLabelIds, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            addLabelIds = addLabelIds.Distinct(StringComparer.Ordinal).ToArray(),
            removeLabelIds = removeLabelIds.Except(addLabelIds, StringComparer.Ordinal).ToArray()
        });

        using var request = CreateAuthorizedRequest(HttpMethod.Post, $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{messageId}/modify", accessToken);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static EmailInput ConvertToEmailInput(GmailMessage message)
    {
        var taxId = ExtractTaxId(message.Subject, message.Body);
        var companyName = ExtractCompanyName(message.Body);
        var competence = ExtractCompetence(message.Subject, message.Body);
        return new EmailInput(message.Id, message.Subject, taxId, companyName, competence, message.ReceivedAtUtc);
    }

    private static string? ExtractTaxId(string subject, string body)
    {
        var candidates = Regex.Matches($"{subject}\n{body}", @"\d[\d\.\-\/]{10,20}")
            .Select(match => new string(match.Value.Where(char.IsDigit).ToArray()))
            .Where(value => value.Length is 11 or 14)
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static string? ExtractCompanyName(string body)
    {
        foreach (var pattern in new[]
                 {
                     @"(?:razao social|raz.o social|empresa|contribuinte)\s*[:\-]\s*(?<value>[^\r\n]+)",
                     @"(?:nome empresarial|nome fantasia)\s*[:\-]\s*(?<value>[^\r\n]+)"
                 })
        {
            var match = Regex.Match(body, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            var value = match.Groups["value"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? ExtractCompetence(string subject, string body)
    {
        foreach (var source in new[] { subject, body })
        {
            var labeledMatch = Regex.Match(source, @"compet.ncia\s*[:\-]?\s*(?<value>\d{2}/\d{4})", RegexOptions.IgnoreCase);
            if (labeledMatch.Success)
                return labeledMatch.Groups["value"].Value;

            var fallbackMatch = Regex.Match(source, @"\b\d{2}/\d{4}\b");
            if (fallbackMatch.Success)
                return fallbackMatch.Value;
        }

        return null;
    }

    private static bool ShouldIgnore(EmailProcessingResult result) =>
        result.Message.Contains("sem regra reconhecida", StringComparison.OrdinalIgnoreCase) ||
        result.Message.Contains("CPF/CNPJ valido", StringComparison.OrdinalIgnoreCase);

    private static string BuildSearchQuery(IReadOnlyList<string> subjectPatterns)
    {
        var uniquePatterns = subjectPatterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(pattern => $"subject:\"{pattern.Replace("\"", "\\\"", StringComparison.Ordinal)}\"")
            .ToList();

        var baseQuery = $"newer_than:365d -in:trash -in:spam -label:\"{ProcessedLabelName}\" -label:\"{PendingLabelName}\" -label:\"{IgnoredLabelName}\"";
        return uniquePatterns.Count == 0
            ? baseQuery
            : $"{baseQuery} ({string.Join(" OR ", uniquePatterns)})";
    }

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url, string accessToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task RespondToBrowserAsync(HttpListenerResponse response, bool success, CancellationToken cancellationToken)
    {
        response.StatusCode = success ? (int)HttpStatusCode.OK : (int)HttpStatusCode.BadRequest;
        response.ContentType = "text/html; charset=utf-8";
        var html = success
            ? "<html><body style=\"font-family:Segoe UI;padding:24px;\">Conexao autorizada. Voce ja pode voltar ao AutoCronos.</body></html>"
            : "<html><body style=\"font-family:Segoe UI;padding:24px;\">A autorizacao falhou. Volte ao AutoCronos para tentar novamente.</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        response.Close();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(content) ? response.ReasonPhrase ?? "Falha HTTP." : content);
        return JsonDocument.Parse(content);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var valueElement))
            throw new InvalidOperationException($"Resposta do Google sem o campo {propertyName}.");

        var value = valueElement.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Resposta do Google sem valor para {propertyName}.");

        return value;
    }

    private static string? GetHeader(JsonElement messageElement, string headerName)
    {
        if (!messageElement.TryGetProperty("payload", out var payloadElement))
            return null;
        if (!payloadElement.TryGetProperty("headers", out var headersElement))
            return null;

        foreach (var headerElement in headersElement.EnumerateArray())
        {
            if (!headerElement.TryGetProperty("name", out var nameElement) || !headerElement.TryGetProperty("value", out var valueElement))
                continue;

            var currentName = nameElement.GetString();
            if (!string.Equals(currentName, headerName, StringComparison.OrdinalIgnoreCase))
                continue;

            return valueElement.GetString();
        }

        return null;
    }

    private static string ExtractBody(JsonElement messageElement)
    {
        if (!messageElement.TryGetProperty("payload", out var payloadElement))
            return string.Empty;

        var plainText = FindBodyPart(payloadElement, "text/plain");
        if (!string.IsNullOrWhiteSpace(plainText))
            return plainText;

        var html = FindBodyPart(payloadElement, "text/html");
        return string.IsNullOrWhiteSpace(html) ? string.Empty : HtmlToPlainText(html);
    }

    private static string? FindBodyPart(JsonElement payloadElement, string mimeType)
    {
        if (payloadElement.TryGetProperty("mimeType", out var mimeTypeElement) &&
            string.Equals(mimeTypeElement.GetString(), mimeType, StringComparison.OrdinalIgnoreCase))
        {
            return DecodeBody(payloadElement);
        }

        if (!payloadElement.TryGetProperty("parts", out var partsElement))
            return null;

        foreach (var partElement in partsElement.EnumerateArray())
        {
            var nested = FindBodyPart(partElement, mimeType);
            if (!string.IsNullOrWhiteSpace(nested))
                return nested;
        }

        return null;
    }

    private static string? DecodeBody(JsonElement payloadElement)
    {
        if (!payloadElement.TryGetProperty("body", out var bodyElement))
            return null;
        if (!bodyElement.TryGetProperty("data", out var dataElement))
            return null;

        var encoded = dataElement.GetString();
        if (string.IsNullOrWhiteSpace(encoded))
            return null;

        var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/').PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '='));
        return Encoding.UTF8.GetString(bytes);
    }

    private static string HtmlToPlainText(string html)
    {
        var withoutTags = Regex.Replace(html, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static DateTime ParseInternalDate(string? value)
    {
        if (!long.TryParse(value, out var milliseconds))
            return DateTime.UtcNow;

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string CreateCodeVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private GmailIntegrationSettings LoadSettings()
    {
        if (!File.Exists(_settingsPath))
            return new GmailIntegrationSettings();

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<GmailIntegrationSettings>(json, JsonOptions) ?? new GmailIntegrationSettings();
        }
        catch
        {
            return new GmailIntegrationSettings();
        }
    }

    private void SaveSettings()
    {
        var json = JsonSerializer.Serialize(_settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }

    private string BuildConnectedDetail()
    {
        if (_settings.LastSyncAtUtc is null)
            return "Conta conectada. Use Sincronizar para buscar e classificar os e-mails.";

        return $"Conta conectada. Ultima sincronizacao em {_settings.LastSyncAtUtc.Value.ToLocalTime():dd/MM/yyyy HH:mm}.";
    }

    private static string SimplifyException(Exception exception)
    {
        var message = exception.Message.Trim();
        return string.IsNullOrWhiteSpace(message) ? exception.GetType().Name : message;
    }

    private sealed class GmailIntegrationSettings
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string? RefreshToken { get; set; }
        public string? EmailAddress { get; set; }
        public DateTime? LastSyncAtUtc { get; set; }
    }

    private sealed record GmailTokenResponse(string AccessToken, string RefreshToken);
    private sealed record GmailAuthorizationResult(string AccessToken, string RefreshToken, string EmailAddress);
    private sealed record GmailLabelSet(string InboxLabelId, string ProcessedLabelId, string PendingLabelId, string IgnoredLabelId);
    private sealed record GmailMessage(string Id, string Subject, string Body, DateTime ReceivedAtUtc);
}
