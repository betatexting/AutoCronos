using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using AutoCronos.Desktop.Domain;

namespace AutoCronos.Desktop.Services;

public sealed class Suite360IntegrationService
{
    private const string BaseUrl = "https://suiteweb.contasnet.com.br/api/public/v1/";
    private const string ApiKeyEnvironmentVariable = "AUTOCRONOS_SUITE360_API_KEY";
    private static readonly HttpClient Client = new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _customersCacheLock = new();
    private readonly object _ticketFormCacheLock = new();
    private readonly string _settingsPath;
    private IReadOnlyList<SuiteClient>? _customersCache;
    private DateTime _customersCacheExpiresAtUtc;
    private SuiteTicketFormOptions? _ticketFormCache;
    private DateTime _ticketFormCacheExpiresAtUtc;

    public Suite360IntegrationService()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCronos");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "suite360-integration.json");
    }

    public async Task<SuiteTicketFormOptions> GetTicketFormOptionsAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = RequireApiKey();
        lock (_ticketFormCacheLock)
        {
            if (_ticketFormCache is not null && _ticketFormCacheExpiresAtUtc > DateTime.UtcNow)
                return _ticketFormCache;
        }

        var ticketTypesTask = GetTicketOptionsAsync("tipos-chamado?all=1", ["nome", "descricao"], apiKey, cancellationToken);
        var originsTask = GetTicketOptionsAsync("origens-chamado?all=1", ["descricao", "nome"], apiKey, cancellationToken);
        var sectorsTask = GetTicketOptionsAsync("setores?all=1", ["descricao", "nome"], apiKey, cancellationToken);
        var executorsTask = GetTicketOptionsAsync("usuarios?ativo=1&all=1", ["nome", "username"], apiKey, cancellationToken, includeSector: true);
        await Task.WhenAll(ticketTypesTask, originsTask, sectorsTask, executorsTask);

        var options = new SuiteTicketFormOptions(ticketTypesTask.Result, originsTask.Result, sectorsTask.Result, executorsTask.Result);
        lock (_ticketFormCacheLock)
        {
            _ticketFormCache = options;
            _ticketFormCacheExpiresAtUtc = DateTime.UtcNow.AddMinutes(15);
        }
        return options;
    }

    public async Task<IReadOnlyList<SuiteWhatsAppConversation>> GetOpenWhatsAppConversationsAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = RequireApiKey();
        var waitingTask = GetWhatsAppConversationsAsync("aguardando", apiKey, cancellationToken);
        var inServiceTask = GetWhatsAppConversationsAsync("em_atendimento", apiKey, cancellationToken);
        await Task.WhenAll(waitingTask, inServiceTask);
        return waitingTask.Result
            .Concat(inServiceTask.Result)
            .Where(item => item.SectorId is not null &&
                           item.FinalizedAtUtc is null &&
                           item.Status is "aguardando" or "em_atendimento")
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
    }

    public async Task<IReadOnlyList<SuiteCustomerCandidate>> FindCustomersAsync(
        string? sourceEmail,
        string? customerTaxId,
        string? companyName,
        CancellationToken cancellationToken = default)
    {
        var apiKey = RequireApiKey();
        var candidates = new Dictionary<long, SuiteCustomerCandidate>();
        var normalizedTaxId = DomainText.NormalizeTaxId(customerTaxId);
        if (normalizedTaxId is not null)
        {
            foreach (var customer in await GetCustomersByTaxIdAsync(apiKey, normalizedTaxId, cancellationToken))
                candidates[customer.Id] = customer.ToCandidate();
        }

        var customers = await GetAllCustomersAsync(apiKey, cancellationToken);
        if (candidates.Count == 0 && NormalizeEmail(sourceEmail) is { } normalizedEmail)
        {
            foreach (var customer in customers.Where(customer => customer.Emails.Contains(normalizedEmail)))
                candidates[customer.Id] = customer.ToCandidate();
        }

        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(companyName))
        {
            foreach (var customer in customers.Where(customer => customer.MatchesName(companyName)))
                candidates[customer.Id] = customer.ToCandidate();
        }

        return candidates.Values
            .OrderBy(candidate => candidate.CompanyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<SuiteTicketsCreated> CreateTicketsAsync(SuiteTicketRequest request, CancellationToken cancellationToken = default)
    {
        var apiKey = RequireApiKey();
        var customerIds = request.CustomerIds.Distinct().Where(id => id > 0).ToArray();
        if (customerIds.Length == 0)
            throw new InvalidOperationException("Selecione ao menos uma empresa para criar o chamado.");

        var payload = new Dictionary<string, object?>
        {
            ["cliente_ids"] = customerIds,
            ["tipo_apontamento_id"] = request.TicketTypeId,
            ["origem_id"] = request.OriginId,
            ["setores_vinculados"] = new[] { request.SectorId },
            ["titulo"] = request.Title,
            ["descricao"] = request.Description,
            ["finaliza_apontamento"] = request.FinalizeImmediately,
            ["enviar_emails"] = false
        };
        if (request.ExecutorId is { } executorId)
        {
            payload["responsavel_conclusao_tipo"] = "PERSONALIZADO";
            payload["setor_responsavel_conclusao_id"] = request.SectorId;
            payload["responsavel_conclusao_id"] = executorId;
        }
        else
        {
            payload["responsavel_conclusao_tipo"] = "RESPONSAVEL_SETOR_EMPRESA";
            payload["setor_responsavel_conclusao_id"] = request.SectorId;
        }

        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, "chamados", apiKey, content, cancellationToken);
        return ReadCreatedTickets(response.RootElement);
    }

    private async Task<IReadOnlyList<SuiteClient>> GetAllCustomersAsync(string apiKey, CancellationToken cancellationToken)
    {
        lock (_customersCacheLock)
        {
            if (_customersCache is not null && _customersCacheExpiresAtUtc > DateTime.UtcNow)
                return _customersCache;
        }

        using var response = await SendAsync(HttpMethod.Get, "clientes?all=1", apiKey, content: null, cancellationToken);
        var customers = ReadCustomers(response.RootElement);
        lock (_customersCacheLock)
        {
            _customersCache = customers;
            _customersCacheExpiresAtUtc = DateTime.UtcNow.AddMinutes(15);
        }
        return customers;
    }

    private static async Task<IReadOnlyList<SuiteClient>> GetCustomersByTaxIdAsync(string apiKey, string taxId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"clientes?cnpj={Uri.EscapeDataString(taxId)}&per_page=10", apiKey, content: null, cancellationToken);
        return ReadCustomers(response.RootElement)
            .Where(customer => string.Equals(customer.TaxId, taxId, StringComparison.Ordinal))
            .ToList();
    }

    private static async Task<IReadOnlyList<SuiteTicketOption>> GetTicketOptionsAsync(
        string path,
        IReadOnlyList<string> nameProperties,
        string apiKey,
        CancellationToken cancellationToken,
        bool includeSector = false)
    {
        using var response = await SendAsync(HttpMethod.Get, path, apiKey, content: null, cancellationToken);
        return EnumerateData(response.RootElement)
            .Select(item => ReadTicketOption(item, nameProperties, includeSector))
            .Where(option => option is not null)
            .Cast<SuiteTicketOption>()
            .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static async Task<IReadOnlyList<SuiteWhatsAppConversation>> GetWhatsAppConversationsAsync(
        string status,
        string apiKey,
        CancellationToken cancellationToken)
    {
        const int pageSize = 200;
        const int maximumPages = 250;
        var conversations = new List<SuiteWhatsAppConversation>();
        for (var page = 1; page <= maximumPages; page++)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                $"whatsapp/conversas?status={Uri.EscapeDataString(status)}&per_page={pageSize}&page={page}",
                apiKey,
                content: null,
                cancellationToken);
            var pageItems = EnumerateData(response.RootElement)
                .Select(ReadWhatsAppConversation)
                .Where(item => item is not null)
                .Cast<SuiteWhatsAppConversation>()
                .ToList();
            conversations.AddRange(pageItems);

            var lastPage = response.RootElement.TryGetProperty("meta", out var meta) &&
                           TryGetInt64(meta, "last_page", out var lastPageValue)
                ? lastPageValue
                : page;
            if (page >= lastPage || pageItems.Count < pageSize)
                break;
        }
        return conversations;
    }

    private static SuiteWhatsAppConversation? ReadWhatsAppConversation(JsonElement element)
    {
        if (!TryGetInt64(element, "id", out var id))
            return null;
        var status = ReadString(element, "status") ?? string.Empty;
        var hashId = ReadString(element, "hash_id") ?? string.Empty;
        var protocol = ReadString(element, "protocolo") ?? id.ToString(CultureInfo.InvariantCulture);
        var sectorId = TryGetInt64(element, "departamento_id", out var departmentValue) ? departmentValue : (long?)null;
        var sectorName = "Sem setor";
        if (element.TryGetProperty("departamento", out var department) && department.ValueKind == JsonValueKind.Object)
            sectorName = ReadString(department, "descricao") ?? sectorName;
        var contactId = TryGetInt64(element, "whatsapp_contato_id", out var contactValue) ? contactValue : (long?)null;
        var contactName = ReadString(element, "nome_exibicao") ?? ReadString(element, "contact_name") ?? "Contato sem nome";
        var phone = ReadString(element, "telefone_cliente") ?? ReadString(element, "contact_phone") ?? string.Empty;
        if (element.TryGetProperty("contato", out var contact) && contact.ValueKind == JsonValueKind.Object)
        {
            contactName = ReadString(contact, "nome") ?? contactName;
            phone = ReadString(contact, "telefone") ?? phone;
        }
        var customerId = TryGetInt64(element, "cliente_id", out var customerValue) ? customerValue : (long?)null;
        var attendantId = TryGetInt64(element, "atendente_responsavel_id", out var attendantValue) ? attendantValue : (long?)null;
        var unreadCount = TryGetInt64(element, "unread_count", out var unreadValue) && unreadValue <= int.MaxValue
            ? (int)unreadValue
            : 0;
        var lastMessageAtUtc = ReadSuiteDateTimeUtc(element, "last_message_at") ??
                               ReadSuiteDateTimeUtc(element, "ultima_mensagem_at") ??
                               DateTime.UtcNow;
        var protocolStartedAtUtc = ReadSuiteDateTimeUtc(element, "criado_em") ?? lastMessageAtUtc;
        var finalizedAtUtc = ReadSuiteDateTimeUtc(element, "finalizada_em");
        return new SuiteWhatsAppConversation(
            id,
            hashId,
            protocol,
            status,
            sectorId,
            sectorName,
            contactId,
            contactName,
            phone,
            customerId,
            attendantId,
            unreadCount,
            ReadString(element, "last_message") ?? string.Empty,
            lastMessageAtUtc,
            protocolStartedAtUtc,
            finalizedAtUtc);
    }

    private static DateTime? ReadSuiteDateTimeUtc(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value) || !DateTime.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dateTime))
            return null;
        return DateTime.SpecifyKind(dateTime, DateTimeKind.Local).ToUniversalTime();
    }

    private static async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string path,
        string apiKey,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await Client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"O Suite360 retornou HTTP {(int)response.StatusCode} sem uma resposta valida.");
        }

        if (response.IsSuccessStatusCode &&
            document.RootElement.TryGetProperty("success", out var success) &&
            success.ValueKind == JsonValueKind.True)
            return document;

        var message = document.RootElement.TryGetProperty("error", out var error) &&
                      error.TryGetProperty("message", out var errorMessage)
            ? errorMessage.GetString()
            : null;
        document.Dispose();
        throw new InvalidOperationException($"O Suite360 retornou um erro: {message ?? $"HTTP {(int)response.StatusCode}"}");
    }

    private static IReadOnlyList<SuiteClient> ReadCustomers(JsonElement root) => EnumerateData(root)
        .Select(ReadCustomer)
        .Where(customer => customer is not null)
        .Cast<SuiteClient>()
        .ToList();

    private static SuiteClient? ReadCustomer(JsonElement customer)
    {
        if (!TryGetInt64(customer, "id", out var id))
            return null;

        var legalName = ReadString(customer, "razao_social");
        var tradeName = ReadString(customer, "nome_fantasia");
        var companyName = legalName
            ?? tradeName
            ?? $"Empresa #{id}";
        var taxId = DomainText.NormalizeTaxId(ReadString(customer, "cnpj"));
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var propertyName in new[] { "email", "email_secundario" })
        {
            var value = ReadString(customer, propertyName);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            foreach (var email in value.Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (NormalizeEmail(email) is { } normalizedEmail)
                    emails.Add(normalizedEmail);
            }
        }
        return new SuiteClient(id, companyName, legalName, tradeName, taxId, emails);
    }

    private static SuiteTicketOption? ReadTicketOption(JsonElement element, IReadOnlyList<string> nameProperties, bool includeSector)
    {
        if (!TryGetInt64(element, "id", out var id))
            return null;

        var name = nameProperties.Select(propertyName => ReadString(element, propertyName))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? $"Item #{id}";
        var sectorId = includeSector && TryGetInt64(element, "setor_id", out var value) ? (long?)value : null;
        return new SuiteTicketOption(id, name, sectorId);
    }

    private static SuiteTicketsCreated ReadCreatedTickets(JsonElement root)
    {
        var ticketIds = new List<long>();
        var protocols = new List<string>();
        if (root.TryGetProperty("data", out var data))
        {
            if (TryGetInt64(data, "id", out var ticketId))
                ticketIds.Add(ticketId);
            AddString(protocols, ReadString(data, "protocolo"));
        }
        if (root.TryGetProperty("meta", out var meta))
        {
            AddNumbers(ticketIds, meta, "apontamentos_ids");
            AddStrings(protocols, meta, "protocolos");
        }

        ticketIds = ticketIds.Distinct().ToList();
        protocols = protocols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ticketIds.Count == 0)
            throw new InvalidOperationException("O Suite360 confirmou a criacao, mas nao retornou os identificadores dos chamados.");
        return new SuiteTicketsCreated(ticketIds, protocols);
    }

    private static IEnumerable<JsonElement> EnumerateData(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
            yield break;

        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
                yield return item;
        }
        else if (data.ValueKind == JsonValueKind.Object)
        {
            yield return data;
        }
    }

    private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;
        return property.ValueKind == JsonValueKind.Number
            ? property.TryGetInt64(out value)
            : property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out value);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static void AddNumbers(List<long> target, JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
            return;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                target.Add(number);
        }
    }

    private static void AddStrings(List<string> target, JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
            return;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String)
                AddString(target, value.GetString());
        }
    }

    private static void AddString(List<string> target, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            target.Add(value);
    }

    private static string? NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var match = Regex.Match(value, @"[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)+");
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private string RequireApiKey() => GetApiKey()
        ?? throw new InvalidOperationException("A integração do Suite360 não está configurada. Gere novamente o executável informando a chave da API durante a publicação.");

    private string? GetApiKey()
    {
        var environmentKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentKey))
            return environmentKey.Trim();

        if (File.Exists(_settingsPath))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<Suite360IntegrationSettings>(File.ReadAllText(_settingsPath), JsonOptions);
                if (!string.IsNullOrWhiteSpace(settings?.ProtectedApiKey))
                {
                    var encryptedKey = Convert.FromBase64String(settings.ProtectedApiKey);
                    var key = ProtectedData.Unprotect(encryptedKey, optionalEntropy: null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(key);
                }
            }
            catch (Exception)
            {
            }
        }

        using var resource = typeof(Suite360IntegrationService).Assembly
            .GetManifestResourceStream("AutoCronos.Suite360ApiKey");
        if (resource is null)
            return null;
        using var reader = new StreamReader(resource, Encoding.UTF8);
        var embeddedKey = reader.ReadToEnd().Trim();
        return string.IsNullOrWhiteSpace(embeddedKey) ? null : embeddedKey;
    }

    private sealed record SuiteClient(long Id, string CompanyName, string? LegalName, string? TradeName, string? TaxId, HashSet<string> Emails)
    {
        public SuiteCustomerCandidate ToCandidate() => new(Id, CompanyName, TaxId);

        public bool MatchesName(string searchText)
        {
            var normalizedSearch = DomainText.NormalizeSubject(searchText);
            return new[] { LegalName, TradeName }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => DomainText.NormalizeSubject(name!))
                .Any(name => name.Length >= 4 &&
                             (normalizedSearch.Contains(name, StringComparison.Ordinal) ||
                              name.Contains(normalizedSearch, StringComparison.Ordinal)));
        }
    }

    private sealed class Suite360IntegrationSettings
    {
        public string? ProtectedApiKey { get; set; }
    }

}

public sealed record SuiteTicketRequest(
    IReadOnlyList<long> CustomerIds,
    int TicketTypeId,
    int OriginId,
    int SectorId,
    int? ExecutorId,
    string Title,
    string Description,
    bool FinalizeImmediately);

public sealed record SuiteTicketsCreated(IReadOnlyList<long> Ids, IReadOnlyList<string> Protocols);
public sealed record SuiteTicketFormOptions(
    IReadOnlyList<SuiteTicketOption> TicketTypes,
    IReadOnlyList<SuiteTicketOption> Origins,
    IReadOnlyList<SuiteTicketOption> Sectors,
    IReadOnlyList<SuiteTicketOption> Executors);

public sealed record SuiteWhatsAppConversation(
    long Id,
    string HashId,
    string Protocol,
    string Status,
    long? SectorId,
    string SectorName,
    long? ContactId,
    string ContactName,
    string Phone,
    long? CustomerId,
    long? AttendantId,
    int UnreadCount,
    string LastMessage,
    DateTime LastMessageAtUtc,
    DateTime ProtocolStartedAtUtc,
    DateTime? FinalizedAtUtc);
