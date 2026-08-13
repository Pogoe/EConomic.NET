using EConomic.SpecConverter;
using Xunit;

namespace EConomic.SpecConverter.Tests;

public class EndpointResolverTests
{
    [Theory]
    [InlineData("customers.get.schema.json", "/customers", "get")]
    [InlineData("customers.post.schema.json", "/customers", "post")]
    [InlineData("customers.customerNumber.get.schema.json", "/customers/{customerNumber}", "get")]
    [InlineData("accounting-years.get.schema.json", "/accounting-years", "get")]
    [InlineData(
        "customers.customerNumber.contacts.contactNumber.get.schema.json",
        "/customers/{customerNumber}/contacts/{contactNumber}",
        "get")]
    public void Resolves_ordinary_paths(string fileName, string expectedPath, string expectedMethod)
    {
        var endpoint = EndpointResolver.Resolve(fileName);

        Assert.Equal(expectedPath, endpoint.Path);
        Assert.Equal(expectedMethod, endpoint.Method);
    }

    [Fact]
    public void Hyphenated_literal_segments_are_not_split_into_parameters()
    {
        var endpoint = EndpointResolver.Resolve("delivery-locations.get.schema.json");

        Assert.Equal("/delivery-locations", endpoint.Path);
        Assert.Empty(endpoint.Parameters);
    }

    [Fact]
    public void Composite_key_segments_become_two_parameters_in_one_segment()
    {
        // e-conomic addresses a voucher by accounting year and number joined with a hyphen.
        var endpoint = EndpointResolver.Resolve(
            "journals.journalNumber.vouchers.accountingYear-voucherNumber.attachment.get.schema.json");

        Assert.Equal(
            "/journals/{journalNumber}/vouchers/{accountingYear}-{voucherNumber}/attachment",
            endpoint.Path);
        Assert.Equal(["journalNumber", "accountingYear", "voucherNumber"], endpoint.Parameters);
    }

    [Theory]
    [InlineData("journals.journalNumber.templates.manualCustomerInvoice.get.schema.json", "manualCustomerInvoice")]
    [InlineData("journals.journalNumber.templates.financeVoucher.get.schema.json", "financeVoucher")]
    public void CamelCase_template_names_stay_literal(string fileName, string literal)
    {
        var endpoint = EndpointResolver.Resolve(fileName);

        Assert.EndsWith($"/{literal}", endpoint.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(literal, endpoint.Parameters);
    }

    [Theory]
    [InlineData("vat-types.id.get.schema.json", "/vat-types/{id}", "id")]
    [InlineData("currencies.code.get.schema.json", "/currencies/{code}", "code")]
    [InlineData("customer-groups.customergroupnumber.put.schema.json", "/customer-groups/{customergroupnumber}", "customergroupnumber")]
    public void Lower_case_parameters_are_recognised(string fileName, string expectedPath, string parameter)
    {
        // Casing cannot decide this: these are parameters despite being lower-case, which is why
        // the resolver works from an explicit table.
        var endpoint = EndpointResolver.Resolve(fileName);

        Assert.Equal(expectedPath, endpoint.Path);
        Assert.Contains(parameter, endpoint.Parameters);
    }

    [Fact]
    public void Unknown_parameter_looking_segments_are_reported_rather_than_guessed()
    {
        var unknown = EndpointResolver.UnknownCamelCaseSegments(
            ["widgets.someBrandNewNumber.get.schema.json"]);

        Assert.Equal("someBrandNewNumber", Assert.Single(unknown));
    }

    [Fact]
    public void Declared_segments_are_not_reported_as_unknown()
    {
        var unknown = EndpointResolver.UnknownCamelCaseSegments(
        [
            "customers.customerNumber.get.schema.json",
            "journals.journalNumber.templates.financeVoucher.get.schema.json",
        ]);

        Assert.Empty(unknown);
    }

    [Theory]
    [InlineData("customers.json")]
    [InlineData("customers.fetch.schema.json")]
    public void Rejects_names_it_does_not_understand(string fileName) =>
        Assert.Throws<ArgumentException>(() => EndpointResolver.Resolve(fileName));
}
