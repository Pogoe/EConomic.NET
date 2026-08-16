using System.Text;
using System.Text.Json;

// Sends one raw request to e-conomic and prints exactly what comes back.
//
// The published schemas have been wrong often enough — 201 responses declared as 200, timestamps
// labelled as dates, filterable properties omitted — that the only reliable description of this API
// is a recorded response. This tool exists to produce those recordings: it does no mapping, no
// deserialization and no interpretation, so what it prints is what the server actually sent.
//
//   dotnet run --project tools/EConomic.Probe -- GET /products?pagesize=2
//   dotnet run --project tools/EConomic.Probe -- POST /customers --data @new-customer.json
//   dotnet run --project tools/EConomic.Probe -- GET /products --save products-page
//
// Credentials come from ECONOMIC_APP_SECRET_TOKEN and ECONOMIC_AGREEMENT_GRANT_TOKEN, falling back
// to the public read-only demo agreement. Never paste real tokens onto the command line: they end
// up in shell history.

var arguments = new Arguments(args);

if (arguments.ShowHelp)
{
    Console.WriteLine(Arguments.Usage);
    return 0;
}

if (arguments.Method is null || arguments.Path is null)
{
    await Console.Error.WriteLineAsync("A method and a path are required.");
    await Console.Error.WriteLineAsync(Arguments.Usage);
    return 2;
}

string appSecret = Environment.GetEnvironmentVariable("ECONOMIC_APP_SECRET_TOKEN") ?? "demo";
string agreementGrant = Environment.GetEnvironmentVariable("ECONOMIC_AGREEMENT_GRANT_TOKEN") ?? "demo";
bool usingDemo = appSecret == "demo" && agreementGrant == "demo";

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-AppSecretToken", appSecret);
httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-AgreementGrantToken", agreementGrant);

string url = arguments.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
    ? arguments.Path
    : arguments.BaseAddress.TrimEnd('/') + "/" + arguments.Path.TrimStart('/');

using var request = new HttpRequestMessage(new HttpMethod(arguments.Method.ToUpperInvariant()), url);

string? body = await arguments.ReadBodyAsync().ConfigureAwait(false);
if (body is not null)
{
    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
}

// A write is only replayed rather than repeated if it carries a key, and knowing whether e-conomic
// honours that is one of the things this tool is for.
if (arguments.IdempotencyKey is { } key)
{
    request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
}

Console.WriteLine($"→ {request.Method} {url}");
Console.WriteLine($"  agreement: {(usingDemo ? "public demo (read-only)" : "configured from environment")}");

if (body is not null)
{
    Console.WriteLine($"  body: {body.Length} bytes");
    Console.WriteLine(Indent(Pretty(body)));
}

Console.WriteLine();

using var response = await httpClient.SendAsync(request).ConfigureAwait(false);
string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

Console.WriteLine($"← {(int)response.StatusCode} {response.ReasonPhrase}");

foreach (string header in Headers(response))
{
    Console.WriteLine($"  {header}");
}

Console.WriteLine();
Console.WriteLine(responseBody.Length == 0 ? "(empty body)" : Pretty(responseBody));

if (arguments.SaveAs is { } name)
{
    string path = Path.Combine("tests", "Fixtures", name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, responseBody.ReplaceLineEndings("\n")).ConfigureAwait(false);
    Console.WriteLine();
    Console.WriteLine($"saved {path}");

    if (!usingDemo)
    {
        // Fixtures are committed. A dedicated test agreement holds nothing sensitive, but the
        // agreement's own record does — /self carries the owner's name, email and address.
        Console.WriteLine("NOTE: this came from a configured agreement, not the public demo.");
        Console.WriteLine("Check it for personal data before committing — /self and /self/user especially.");
    }
}

return response.IsSuccessStatusCode ? 0 : 1;

static IEnumerable<string> Headers(HttpResponseMessage response)
{
    // The headers that carry information found nowhere else: what the call cost, what budget is
    // left, what support will ask for, and whether a write was replayed rather than performed.
    string[] interesting =
    [
        "X-RequestId", "X-CallCost", "X-RateLimiting", "X-EconomicVersion",
        "X-ResultFromCache", "Location", "Retry-After",
    ];

    foreach (string name in interesting)
    {
        if (response.Headers.TryGetValues(name, out var values)
            || response.Content.Headers.TryGetValues(name, out values))
        {
            yield return $"{name}: {string.Join(", ", values)}";
        }
    }

    if (response.Content.Headers.ContentType is { } contentType)
    {
        yield return $"Content-Type: {contentType}";
    }
}

static string Pretty(string json)
{
    try
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
    }
    catch (JsonException)
    {
        // A non-JSON body is itself worth seeing verbatim — several errors come back as plain text.
        return json;
    }
}

static string Indent(string text) =>
    string.Join("\n", text.Split('\n').Select(line => "    " + line));

internal sealed class Arguments
{
    public const string Usage = """
        Usage: <METHOD> <PATH> [options]

          --data <json|@file>     Request body, inline or read from a file.
          --save <name>           Write the response body to tests/Fixtures/<name>.json.
                                  Only permitted against the public demo agreement.
          --idempotency-key <key> Send an Idempotency-Key header.
          --base <url>            Base address. Defaults to https://restapi.e-conomic.com.
          --help                  Show this text.

        Credentials are read from ECONOMIC_APP_SECRET_TOKEN and ECONOMIC_AGREEMENT_GRANT_TOKEN,
        defaulting to the public read-only demo agreement.
        """;

    private readonly string? _data;

    public Arguments(string[] args)
    {
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    ShowHelp = true;
                    break;
                case "--data" when i + 1 < args.Length:
                    _data = args[++i];
                    break;
                case "--save" when i + 1 < args.Length:
                    SaveAs = args[++i];
                    break;
                case "--idempotency-key" when i + 1 < args.Length:
                    IdempotencyKey = args[++i];
                    break;
                case "--base" when i + 1 < args.Length:
                    BaseAddress = args[++i];
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        Method = positional.Count > 0 ? positional[0] : null;
        Path = positional.Count > 1 ? positional[1] : null;
    }

    public bool ShowHelp { get; }

    public string? Method { get; }

    public string? Path { get; }

    public string? SaveAs { get; }

    public string? IdempotencyKey { get; }

    public string BaseAddress { get; private set; } = "https://restapi.e-conomic.com";

    public async Task<string?> ReadBodyAsync()
    {
        if (_data is null)
        {
            return null;
        }

        return _data.StartsWith('@')
            ? await File.ReadAllTextAsync(_data[1..]).ConfigureAwait(false)
            : _data;
    }
}
