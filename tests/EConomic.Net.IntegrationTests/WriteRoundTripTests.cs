using System.Net.Http;
using EConomic.Authentication;
using EConomic.Exceptions;
using EConomic.Rest;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Exercises create, update and delete against a real agreement.
/// </summary>
/// <remarks>
/// <para>
/// These are separate from the read integration tests because they need a different environment and
/// carry a different risk. The public demo agreement rejects writes, so they require a real
/// agreement's tokens, and they create and delete records in it. They are therefore opted into
/// separately and never run in CI.
/// </para>
/// <para>
/// Point them at a throwaway agreement. Everything they create is prefixed <c>ZZ Probe</c> and
/// deleted again, but a failed run can leave a record behind.
/// </para>
/// </remarks>
public class WriteRoundTripTests
{
    private const string OptInVariable = "ECONOMIC_RUN_WRITE_TESTS";
    private const string AppSecretVariable = "ECONOMIC_APP_SECRET_TOKEN";
    private const string AgreementGrantVariable = "ECONOMIC_AGREEMENT_GRANT_TOKEN";

    [Fact]
    public async Task A_customer_survives_a_create_update_delete_round_trip()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        Customer? created = null;

        try
        {
            created = await client.Customers.CreateAsync(
                new CustomerCreate
                {
                    Name = "ZZ Probe Round Trip",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                    City = "Ringsted",
                },
                token);

            // The create schema declares neither of these; the server sends both.
            Assert.True(created.CustomerNumber > 0);
            Assert.NotNull(created.Self);
            Assert.Equal("ZZ Probe Round Trip", created.Name);
            Assert.Equal("Ringsted", created.City);

            var updated = await client.Customers.UpdateAsync(
                created.CustomerNumber,
                new CustomerUpdate
                {
                    Name = "ZZ Probe Round Trip (updated)",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            Assert.Equal("ZZ Probe Round Trip (updated)", updated.Name);

            // PUT replaces rather than patches, so the city omitted above must now be gone.
            Assert.True(string.IsNullOrEmpty(updated.City), $"Expected city to be cleared, got '{updated.City}'.");
        }
        finally
        {
            if (created is not null)
            {
                await client.Customers.DeleteAsync(created.CustomerNumber, token);
            }
        }

        // Deleting twice is what reports the record as gone. Updating it would not: PUT is an
        // upsert, covered separately below.
        var gone = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Customers.DeleteAsync(created!.CustomerNumber, token));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Updating_a_customer_that_does_not_exist_creates_it()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        // PUT is an upsert, which neither the schemas nor the documentation mention: sending one
        // for an identifier that does not exist answers 201 Created and the resource appears.
        // Anything relying on PUT to fail for a missing record is therefore wrong.
        const int number = 999;

        var upserted = await client.Customers.UpdateAsync(
            number,
            new CustomerUpdate
            {
                Name = "ZZ Probe Upsert",
                Currency = "DKK",
                CustomerGroupNumber = 1,
                PaymentTermsNumber = 1,
                VatZoneNumber = 1,
            },
            token);

        try
        {
            Assert.Equal(number, upserted.CustomerNumber);
            Assert.Equal("ZZ Probe Upsert", upserted.Name);
        }
        finally
        {
            await client.Customers.DeleteAsync(number, token);
        }
    }

    [Fact]
    public async Task A_unit_can_be_created_even_though_its_schema_describes_no_identifier()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        Unit? created = null;

        try
        {
            // UnitsCreate declares only `name`. The server returns unitNumber and self as well,
            // which is why this method exists at all.
            created = await client.Units.CreateAsync(new UnitCreate { Name = "ZZ Probe Unit" }, token);

            Assert.True(created.UnitNumber > 0);
            Assert.NotNull(created.Self);
            Assert.Equal("ZZ Probe Unit", created.Name);
        }
        finally
        {
            if (created is not null)
            {
                await client.Units.DeleteAsync(created.UnitNumber, token);
            }
        }
    }

    [Fact]
    public async Task A_product_keeps_an_alphanumeric_number_through_the_round_trip()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        const string number = "ZZ-PROBE-1";
        var created = false;

        try
        {
            // The path parameter used to be typed as an integer, which would have made this
            // unrepresentable. Product numbers are strings, and the server accepts them in the path.
            var product = await client.Products.CreateAsync(
                new ProductCreate { ProductNumber = number, Name = "ZZ Probe Product", ProductGroupNumber = 1 },
                token);

            created = true;
            Assert.Equal(number, product.ProductNumber);

            var updated = await client.Products.UpdateAsync(
                number,
                new ProductUpdate { Name = "ZZ Probe Product (updated)", ProductGroupNumber = 1 },
                token);

            Assert.Equal(number, updated.ProductNumber);
            Assert.Equal("ZZ Probe Product (updated)", updated.Name);
        }
        finally
        {
            if (created)
            {
                await client.Products.DeleteAsync(number, token);
            }
        }
    }

    [Fact]
    public async Task Deleting_twice_reports_the_second_attempt_as_missing()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;

        var created = await client.Units.CreateAsync(new UnitCreate { Name = "ZZ Probe Delete Twice" }, token);
        await client.Units.DeleteAsync(created.UnitNumber, token);

        // e-conomic documents delete as non-idempotent, and identifiers are reused, which together
        // are why the retry handler refuses to repeat a delete that carries no idempotency key.
        var second = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Units.DeleteAsync(created.UnitNumber, token));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task A_payment_term_round_trips_with_its_enum_typed_property()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        PaymentTerms? created = null;

        try
        {
            // paymentTermsType is an enum in the generated layer and required by e-conomic, so a
            // create is impossible without it. The public model takes the string the API uses.
            created = await client.PaymentTerms.CreateAsync(
                new PaymentTermsCreate { Name = "ZZ Probe Terms", PaymentTermsType = "net", DaysOfCredit = 14 },
                token);

            Assert.True(created.PaymentTermsNumber > 0);
            Assert.Equal("ZZ Probe Terms", created.Name);
            Assert.Equal("net", created.PaymentTermsType, ignoreCase: true);
        }
        finally
        {
            if (created is not null)
            {
                await client.PaymentTerms.DeleteAsync(created.PaymentTermsNumber, token);
            }
        }
    }

    [Fact]
    public async Task A_customer_group_requires_a_caller_supplied_number()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        const int number = 90;

        // Unlike customers, whose number the server assigns, a customer group create is rejected
        // without one — which is why the number is required on the create model.
        var created = await client.CustomerGroups.CreateAsync(
            new CustomerGroupCreate { CustomerGroupNumber = number, Name = "ZZ Probe Group", AccountNumber = 5600 },
            token);

        try
        {
            Assert.Equal(number, created.CustomerGroupNumber);
            Assert.Equal("ZZ Probe Group", created.Name);
        }
        finally
        {
            await client.CustomerGroups.DeleteAsync(number, token);
        }
    }

    [Fact]
    public async Task A_supplier_round_trips()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        Supplier? created = null;

        try
        {
            created = await client.Suppliers.CreateAsync(
                new SupplierCreate
                {
                    Name = "ZZ Probe Supplier",
                    Currency = "DKK",
                    SupplierGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            Assert.True(created.SupplierNumber > 0);
            Assert.NotNull(created.Self);

            var updated = await client.Suppliers.UpdateAsync(
                created.SupplierNumber,
                new SupplierUpdate
                {
                    Name = "ZZ Probe Supplier (updated)",
                    Currency = "DKK",
                    SupplierGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            Assert.Equal("ZZ Probe Supplier (updated)", updated.Name);
        }
        finally
        {
            if (created is not null)
            {
                await client.Suppliers.DeleteAsync(created.SupplierNumber, token);
            }
        }
    }

    [Fact]
    public async Task A_customers_contacts_and_delivery_locations_round_trip()
    {
        SkipUnlessOptedIn();

        var client = CreateClient();
        var token = TestContext.Current.CancellationToken;
        Customer? customer = null;

        try
        {
            customer = await client.Customers.CreateAsync(
                new CustomerCreate
                {
                    Name = "ZZ Probe Nested Host",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermsNumber = 1,
                    VatZoneNumber = 1,
                },
                token);

            // A nested collection is reached through its parent rather than off the client, because
            // it cannot be addressed without the parent's identifier.
            var contacts = client.Customers.Contacts(customer.CustomerNumber);

            var contact = await contacts.CreateAsync(
                new CustomerContactCreate { Name = "ZZ Probe Contact", Email = "contact@example.com" },
                token);

            Assert.True(contact.CustomerContactNumber > 0);
            Assert.Equal("ZZ Probe Contact", contact.Name);

            var renamed = await contacts.UpdateAsync(
                contact.CustomerContactNumber,
                new CustomerContactUpdate { Name = "ZZ Probe Contact (updated)" },
                token);

            Assert.Equal("ZZ Probe Contact (updated)", renamed.Name);

            var page = await contacts.GetPageAsync(0, token);
            Assert.Contains(page.Items, c => c.CustomerContactNumber == contact.CustomerContactNumber);

            await contacts.DeleteAsync(contact.CustomerContactNumber, token);

            var locations = client.Customers.DeliveryLocations(customer.CustomerNumber);
            var location = await locations.CreateAsync(
                new DeliveryLocationCreate { Address = "Odinsparken 4", City = "Ringsted", PostalCode = "4100" },
                token);

            Assert.True(location.DeliveryLocationNumber > 0);
            await locations.DeleteAsync(location.DeliveryLocationNumber, token);
        }
        finally
        {
            if (customer is not null)
            {
                await client.Customers.DeleteAsync(customer.CustomerNumber, token);
            }
        }
    }

    private static EconomicClient CreateClient()
    {
        var options = new EconomicOptions
        {
            AppSecretToken = Environment.GetEnvironmentVariable(AppSecretVariable)!,
            AgreementGrantToken = Environment.GetEnvironmentVariable(AgreementGrantVariable)!,
        };

        return new EconomicClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, options);
    }

    private static void SkipUnlessOptedIn()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable(OptInVariable) is not "1",
            $"Set {OptInVariable}=1 to run write tests against a real agreement.");

        // Falling back to the demo tokens would send writes the demo agreement rejects, producing a
        // confusing failure rather than a clear one.
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppSecretVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AgreementGrantVariable)),
            $"Set {AppSecretVariable} and {AgreementGrantVariable} to a real agreement's tokens.");
    }
}
