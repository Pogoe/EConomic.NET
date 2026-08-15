using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using EConomic.Authentication;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// Sends every write the client offers and inspects the bytes that leave.
/// </summary>
/// <remarks>
/// <para>
/// Each write is covered live, but a live test can only report that e-conomic accepted the request.
/// It cannot say what was in it, and the mistake this guards against is one the server does report —
/// twice already. A property the caller never set was serialized as <c>0</c>, and e-conomic rejects
/// <c>0</c> for anything it declares with a minimum of 1, so the whole request failed. The first
/// occurrence was on a draft invoice's <c>lineNumber</c>; the second was the same bug one level
/// down, inside an array element, because the fix had not recurred.
/// </para>
/// <para>
/// So the assertion is not that a request was made. It is that nothing appears in the body that the
/// caller did not put there: no zero, no explicit null, no empty string, no year-one date. Every
/// required property is filled in first, since leaving one unset would produce exactly the value
/// this is looking for and blame the generator for the test's own omission.
/// </para>
/// <para>
/// The resources come from reflection over the client rather than from a list, nested ones included,
/// so a write added later is covered without anyone remembering to add it here.
/// </para>
/// <para>
/// One value is deliberately not reported: a <see langword="false"/>. The payloads declare their
/// booleans as plain <see cref="bool"/> rather than as nullable, so an untouched <c>barred</c> is
/// sent as <c>false</c> — and that was checked against a live agreement rather than assumed. A
/// <c>PUT</c> that omits <c>barred</c> entirely clears it just the same, so the two requests mean
/// the same thing to e-conomic and there is nothing to fix.
/// </para>
/// </remarks>
public class WriteRequestTests
{
    [Fact]
    public async Task Every_write_sends_only_what_the_caller_supplied()
    {
        var failures = new List<string>();
        var sent = new List<string>();

        foreach (var (name, resource) in Resources())
        {
            var recorder = new Recorder();
            var target = resource(recorder);

            foreach (var write in Writes(target.GetType()))
            {
                var supplied = new HashSet<string>(StringComparer.Ordinal);
                var arguments = Arguments(write, supplied);
                if (arguments is null)
                {
                    continue;
                }

                recorder.Clear();

                try
                {
                    await (Task)write.Invoke(target, arguments)!;
                }
#pragma warning disable CA1031 // Anything at all here means the request never left, which is the finding.
                catch (Exception exception) when (recorder.Method is null)
#pragma warning restore CA1031
                {
                    failures.Add($"{name}.{write.Name} sent nothing: {exception}");
                    continue;
                }
                catch (Exceptions.EconomicApiException)
                {
                    // The stub answers every write with an empty body, so mapping the response back
                    // may well fail. What was sent is already recorded, and that is what is at issue
                    // here; the response side is covered by FacadeMappingTests.
                }

                var path = $"{name}.{write.Name}";
                sent.Add(path);

                Assert.Equal(Expected(write.Name), recorder.Method);

                if (recorder.Body is { } body)
                {
                    using var document = JsonDocument.Parse(body);
                    Inspect(document.RootElement, path, failures, supplied);
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // A guard against the reflection silently matching nothing, which would make this pass
        // while sending no writes at all.
        Assert.True(sent.Count >= 147, $"Expected every write; only sent {sent.Count}.");
    }

    /// <summary>
    /// Confirms the check above can fail.
    /// </summary>
    /// <remarks>
    /// The body of a correct request contains nothing unusual, so a broken inspection and a correct
    /// library are indistinguishable. This pins each value the inspection is there to catch.
    /// </remarks>
    [Theory]
    [InlineData("""{ "lineNumber": 0 }""")]
    [InlineData("""{ "name": null }""")]
    [InlineData("""{ "name": "" }""")]
    [InlineData("""{ "date": "0001-01-01" }""")]
    [InlineData("""{ "lines": [ { "sortKey": 0 } ] }""")]
    [InlineData("""{ "recipient": { "vatZone": { "vatZoneNumber": 0 } } }""")]
    public void The_check_reports_a_value_the_caller_never_supplied(string body)
    {
        var failures = new List<string>();

        using var document = JsonDocument.Parse(body);
        Inspect(document.RootElement, "probe", failures);

        Assert.Single(failures);
    }

    [Fact]
    public void The_check_accepts_a_body_the_caller_filled_in()
    {
        var failures = new List<string>();

        using var document = JsonDocument.Parse(
            """{ "name": "Acme", "customer": { "customerNumber": 7 }, "lines": [ { "quantity": 1.5 } ] }""");
        Inspect(document.RootElement, "probe", failures);

        Assert.Empty(failures);
    }

    /// <summary>The method a write of this name is expected to use.</summary>
    private static HttpMethod Expected(string write) => write switch
    {
        "CreateAsync" => HttpMethod.Post,
        "UpdateAsync" => HttpMethod.Put,
        _ => HttpMethod.Delete,
    };

    /// <summary>Every resource on the client, including those reached through a parent.</summary>
    /// <remarks>
    /// Starting from the API surfaces rather than the client itself, so the OpenAPI services are
    /// covered the moment they hang off <c>client.Open</c>, without this being touched.
    /// </remarks>
    private static IEnumerable<(string Name, Func<Recorder, object> Open)> Resources()
    {
        // A collection scoped by a path parameter hangs off the surface itself rather than off a
        // parent resource — /pricegroups/{n}/specialprices has no price-group resource to reach it
        // through on this surface, so it is a method on the API taking that identifier.
        foreach (var (surface, scoped) in Surfaces()
            .SelectMany(s => s.PropertyType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => (Surface: s, Method: m)))
            .Where(x => x.Method.ReturnType.Name.EndsWith("Resource", StringComparison.Ordinal)
                && x.Method.GetParameters().Length == 1)
            .OrderBy(x => x.Method.Name, StringComparer.Ordinal))
        {
            var host = surface;
            var opener = scoped;

            yield return (
                $"{surface.Name}.{scoped.Name}",
                recorder => opener.Invoke(
                    host.GetValue(Client(recorder)),
                    [Scalar(opener.GetParameters()[0].ParameterType)])!);
        }

        foreach (var (surface, property) in Surfaces()
            .SelectMany(s => s.PropertyType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (Surface: s, Property: p)))
            .Where(x => x.Property.PropertyType.Name.EndsWith("Resource", StringComparison.Ordinal))
            .OrderBy(x => x.Property.Name, StringComparer.Ordinal))
        {
            yield return ($"{surface.Name}.{property.Name}", recorder => property.GetValue(surface.GetValue(Client(recorder)))!);

            // A nested collection hangs off its parent rather than off the client — the contacts of
            // a customer, the vouchers of a journal — so it is reached by calling the parent with an
            // identifier.
            foreach (var nested in property.PropertyType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.ReturnType.Name.EndsWith("Resource", StringComparison.Ordinal)
                    && m.GetParameters().Length == 1)
                .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var owner = property;
                var opener = nested;
                var host = surface;

                yield return (
                    $"{surface.Name}.{property.Name}.{nested.Name}",
                    recorder => opener.Invoke(
                        owner.GetValue(host.GetValue(Client(recorder))),
                        [Scalar(opener.GetParameters()[0].ParameterType)])!);
            }
        }
    }

    /// <summary>The API surfaces the client exposes — <c>Rest</c> today, <c>Open</c> as it lands.</summary>
    private static IEnumerable<PropertyInfo> Surfaces() =>
        typeof(EconomicClient)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.Name.EndsWith("Api", StringComparison.Ordinal))
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    /// <summary>The writes a resource offers.</summary>
    private static IEnumerable<MethodInfo> Writes(Type resource) =>
        resource.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name is "CreateAsync" or "UpdateAsync" or "DeleteAsync")
            .OrderBy(m => m.Name, StringComparer.Ordinal);

    /// <summary>Arguments for one write, or <see langword="null"/> if it takes something unexpected.</summary>
    /// <remarks>
    /// Deleting every draft invoice at once takes a confirmation rather than an identifier, and it
    /// has its own test — this one would only re-assert the URL.
    /// </remarks>
    private static object?[]? Arguments(MethodInfo write, HashSet<string> supplied)
    {
        var arguments = new List<object?>();

        foreach (var parameter in write.GetParameters())
        {
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                arguments.Add(TestContext.Current.CancellationToken);
            }
            else if (parameter.ParameterType == typeof(int) || parameter.ParameterType == typeof(string))
            {
                arguments.Add(Scalar(parameter.ParameterType));
            }
            else if (IsPayload(parameter.ParameterType))
            {
                arguments.Add(Structured(
                    parameter.ParameterType,
                    Generated(write.DeclaringType!, parameter.ParameterType),
                    supplied));
            }
            else
            {
                return null;
            }
        }

        return [.. arguments];
    }

    /// <summary>
    /// Builds a payload with its structure filled in and its required properties set.
    /// </summary>
    /// <remarks>
    /// Nested objects and one array element are always created, because that is where both of the
    /// live failures happened. Optional values are deliberately left alone: the point is to see what
    /// the library sends for a property the caller never touched.
    /// </remarks>
    private static object Structured(Type type, Type? generated, HashSet<string> supplied)
    {
        var instance = Activator.CreateInstance(type)!;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var target = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var counterpart = Counterpart(generated, property.Name);

            if (IsPayload(target))
            {
                property.SetValue(instance, Structured(target, counterpart, supplied));
            }
            else if (target.IsGenericType
                && typeof(IEnumerable).IsAssignableFrom(target)
                && target.GetGenericArguments() is [var element]
                && IsPayload(element))
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
                list.Add(Structured(element, Element(counterpart), supplied));
                property.SetValue(instance, list);
            }
            else if (property.GetCustomAttribute<RequiredMemberAttribute>() is not null)
            {
                var value = Scalar(target, counterpart);
                property.SetValue(instance, value);
                supplied.Add(value.ToString()!);
            }
        }

        return instance;
    }

    /// <summary>The generated payload a facade payload is copied onto, for the properties it constrains.</summary>
    /// <remarks>
    /// The public payloads take a plain <see langword="string"/> where the generated one takes an
    /// enum, so that a value e-conomic adds later is not a breaking change here. Only the generated
    /// side knows which strings are acceptable, and "probe" is not one of them.
    /// </remarks>
    private static Type? Generated(Type resource, Type payload) =>
        resource.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == "ToGenerated" && m.GetParameters() is [var first, ..] && first.ParameterType == payload)
            .Select(m => m.ReturnType)
            .FirstOrDefault();

    /// <summary>The generated property of the same name, if there is one.</summary>
    private static Type? Counterpart(Type? generated, string name)
    {
        var property = generated?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

        return property is null ? null : Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
    }

    /// <summary>What a generated collection holds.</summary>
    private static Type? Element(Type? collection) =>
        collection?.IsGenericType == true && collection.GetGenericArguments() is [var element] ? element : null;

    /// <summary>Whether this is one of the library's own payload records, on either surface.</summary>
    /// <remarks>
    /// Both namespaces, deliberately. Matching only <c>EConomic.Rest</c> silently skipped every
    /// create and update on the OpenAPI services — they were counted as covered because their
    /// deletes, which take no payload, still ran.
    /// </remarks>
    private static bool IsPayload(Type type) =>
        type.IsClass && type != typeof(string) && type != typeof(Uri)
        && type.Namespace is "EConomic.Rest" or "EConomic.Open";

    /// <summary>A value the generated payload will accept, and that no caller could have left unset.</summary>
    private static object Scalar(Type type, Type? generated) =>
        type == typeof(string) && generated is { IsEnum: true }
            ? Enum.GetNames(generated)[0]
            : Scalar(type);

    /// <summary>A value that could not be mistaken for one the caller left unset.</summary>
    private static object Scalar(Type type) => type switch
    {
        _ when type.IsEnum => Enum.GetValues(type).GetValue(0)!,
        _ when type == typeof(string) => "probe",
        _ when type == typeof(bool) => true,
        _ when type == typeof(DateOnly) => new DateOnly(2026, 4, 7),
        _ when type == typeof(DateTime) => new DateTime(2026, 4, 7, 8, 53, 29, DateTimeKind.Utc),
        _ when type == typeof(DateTimeOffset) => new DateTimeOffset(2026, 4, 7, 8, 53, 29, TimeSpan.Zero),
        _ when type == typeof(decimal) => 1.5m,
        _ when type == typeof(double) => 1.5d,
        _ when type == typeof(Uri) => new Uri("https://restapi.e-conomic.com/probe/7"),
        _ => Convert.ChangeType(7, type, CultureInfo.InvariantCulture),
    };

    /// <summary>Reports anything in the body the caller did not supply.</summary>
    /// <param name="element">Part of a request body.</param>
    /// <param name="path">Where it sits, for the message.</param>
    /// <param name="failures">Collects what was found.</param>
    /// <param name="supplied">
    /// Every string the caller actually set. A leaked enum is the reason this is needed: it arrives
    /// as an ordinary string — an unset <c>paymentTermsType</c> was sent as <c>"net"</c> — and
    /// nothing about the value itself gives it away. Dates are exempt, since an unset one has its
    /// own check and a set one is formatted rather than copied.
    /// </param>
    private static void Inspect(
        JsonElement element,
        string path,
        List<string> failures,
        IReadOnlySet<string>? supplied = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Inspect(property.Value, $"{path}.{property.Name}", failures, supplied);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Inspect(item, $"{path}[{index++}]", failures, supplied);
                }

                break;

            case JsonValueKind.Null:
                failures.Add($"{path} was sent as null. e-conomic rejects an explicit null; omit it instead.");
                break;

            case JsonValueKind.Number when element.GetDecimal() == 0:
                failures.Add($"{path} was sent as 0, which e-conomic rejects wherever it declares a minimum of 1.");
                break;

            case JsonValueKind.String when element.GetString() is "":
                failures.Add($"{path} was sent as an empty string.");
                break;

            case JsonValueKind.String when element.GetString()?.StartsWith("0001-01-01", StringComparison.Ordinal) == true:
                failures.Add($"{path} was sent as an unset date.");
                break;

            case JsonValueKind.String
                when supplied is not null
                    && element.GetString() is { } text
                    && !supplied.Contains(text)
                    && !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, out _):
                failures.Add($"{path} was sent as \"{text}\", which the caller never supplied.");
                break;

            default:
                break;
        }
    }

    private static EconomicClient Client(Recorder recorder) =>
        new(new HttpClient(recorder), EconomicOptions.Demo());

    /// <summary>A transport that answers everything and remembers what it was asked.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Body { get; private set; }

        public void Clear()
        {
            Method = null;
            Body = null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;

            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                RequestMessage = request,
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
