using System.Text.Json;
using EConomic.Rest.Generated;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// Deserializes a recorded live response through the source-generated resolver.
/// </summary>
/// <remarks>
/// The stub responses elsewhere are written by hand and therefore only contain what was thought of.
/// This one is what e-conomic actually sent, so it exercises the fields a hand-written fixture
/// omits — and it was a real omission that broke the first live run.
/// </remarks>
public class GeneratedDeserializationTests
{
    private static readonly JsonSerializerOptions SourceGenerated = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.TypeInfoResolverChain.Add(EconomicRestJsonContext.Default);
        return options;
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void A_recorded_customer_collection_round_trips_through_the_source_generated_resolver()
    {
        var json = Fixture("customers.collection.json");
        var collection = JsonSerializer.Deserialize<CustomerCollection>(json, SourceGenerated);

        Assert.NotNull(collection);
        Assert.NotEmpty(collection.Collection);

        var first = collection.Collection[0];
        Assert.True(first.CustomerNumber > 0);
        Assert.False(string.IsNullOrWhiteSpace(first.Name));
        Assert.NotNull(first.CustomerGroup);
        Assert.NotNull(first.Self);
    }

    [Fact]
    public void The_free_form_metadata_object_does_not_break_the_response()
    {
        // metaData is typed `object` in the generated model. Without metadata for it in the
        // context there is no reflection fallback to rescue the deserialization.
        var json = Fixture("customers.collection.json");
        var collection = JsonSerializer.Deserialize<CustomerCollection>(json, SourceGenerated);

        Assert.NotNull(collection?.MetaData);
    }
}
