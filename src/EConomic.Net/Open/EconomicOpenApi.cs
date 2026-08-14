namespace EConomic.Open;

/// <summary>
/// The OpenAPI services, reached through <see cref="EconomicClient.Open"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each service is versioned independently and carries its version in the URL, so the address is
/// composed per service rather than shared. The version is pinned here to the specification the
/// client was generated from — a service moving to a new major version is a change to this library,
/// not something that happens underneath it.
/// </para>
/// <para>
/// Collections hang off this directly, as they do on the legacy surface, and every name carries the
/// service that publishes it. That is not decoration: customers and suppliers both publish a
/// <c>Contact</c>, so one of them would otherwise have to be renamed the day the other landed.
/// </para>
/// <para>
/// Deliberately not interchangeable with <see cref="Rest.EconomicRestApi"/>. Both publish an entity
/// called <c>Customer</c>, and they disagree: here the caller supplies the customer number and a
/// payment terms reference is <c>paymentTermId</c>, while the legacy surface has the server assign
/// the number and spells it <c>paymentTermsNumber</c>.
/// </para>
/// </remarks>
/// <param name="httpClient">The configured transport.</param>
/// <param name="baseAddress">The root of <c>apis.e-conomic.com</c>.</param>
public sealed class EconomicOpenApi(HttpClient httpClient, Uri baseAddress)
{
    /// <summary>
    /// The service versions this client is generated against, each as it appears in the URL.
    /// </summary>
    /// <remarks>
    /// Kept in step with <c>specs/openapi/</c> by a test that reads the version out of each
    /// specification's <c>servers</c> entry and compares it with this. A specification refresh that
    /// bumps a version therefore fails the build rather than silently addressing an endpoint the
    /// generated client no longer matches.
    /// </remarks>
    internal static class Services
    {
        /// <summary>The accounts service, covering accounts, key figure codes and total intervals.</summary>
        public const string Accounts = "accountsapi/v7.0.0/";

        /// <summary>The customers service, covering customers, contacts and delivery locations.</summary>
        public const string Customers = "customersapi/v3.1.0/";

        /// <summary>The products service, covering products, their groups, and pricing.</summary>
        public const string Products = "productsapi/v2.0.0/";

        /// <summary>The suppliers service, which covers only contacts and groups.</summary>
        public const string Suppliers = "suppliersapi/v2.0.0/";
    }

    /// <summary><c>/Accounts</c>, the chart of accounts.</summary>
    public AccountResource Accounts => new(httpClient, Address(Services.Accounts));

    /// <summary><c>/KeyFigureCodes</c>, the codes an account can be assigned to. Read-only.</summary>
    public AccountKeyFigureCodeResource AccountKeyFigureCodes => new(httpClient, Address(Services.Accounts));

    /// <summary><c>/TotalIntervals</c>, the account ranges a total account sums.</summary>
    /// <remarks>
    /// Without a delete: e-conomic addresses that one by account number and starting account
    /// together, and a two-part key is not a shape this facade expresses.
    /// </remarks>
    public AccountTotalIntervalResource AccountTotalIntervals => new(httpClient, Address(Services.Accounts));

    /// <summary><c>/Customers</c>, whose numbers you assign rather than the server.</summary>
    public CustomerResource Customers => new(httpClient, Address(Services.Customers));

    /// <summary><c>/Contacts</c> of the customers service, addressed as a collection of their own.</summary>
    /// <remarks>
    /// Unlike the legacy surface, where contacts hang off a customer and cannot be addressed
    /// without one, these are a top-level collection filtered by <c>customerNumber</c>.
    /// </remarks>
    public CustomerContactResource CustomerContacts => new(httpClient, Address(Services.Customers));

    /// <summary><c>/DeliveryLocations</c>, likewise a collection of its own.</summary>
    public CustomerDeliveryLocationResource CustomerDeliveryLocations =>
        new(httpClient, Address(Services.Customers));

    /// <summary><c>/products</c>, whose identifier is a string rather than a number.</summary>
    /// <remarks>
    /// Without a count: this is the one collection in these three services that publishes no
    /// <c>/count</c>, and its paged listing sits at <c>/productspaged/paged</c> rather than beside
    /// it. Both are e-conomic's shape, not an omission here.
    /// </remarks>
    public ProductResource Products => new(httpClient, Address(Services.Products));

    /// <summary><c>/productgroups</c>, which every product belongs to.</summary>
    public ProductGroupResource ProductGroups => new(httpClient, Address(Services.Products));

    /// <summary><c>/pricegroups</c>, the price lists a customer can be put on.</summary>
    public ProductPriceGroupResource ProductPriceGroups => new(httpClient, Address(Services.Products));

    /// <summary><c>/salespriceincurrencies</c>, read-only across every product.</summary>
    /// <remarks>
    /// Reading is a collection of its own; writing is not. e-conomic publishes the create, update
    /// and delete under <c>/products/{productNumber}/salespriceincurrencies</c>, keyed by currency,
    /// which is a shape this facade does not yet express.
    /// </remarks>
    public ProductSalesPriceInCurrencyResource ProductSalesPricesInCurrency =>
        new(httpClient, Address(Services.Products));

    /// <summary><c>/pricegroups/{priceGroupNumber}/specialprices</c>, one price group's overrides.</summary>
    /// <param name="priceGroupNumber">The price group these prices belong to.</param>
    /// <returns>The collection, scoped to that price group.</returns>
    public ProductSpecialPriceResource ProductSpecialPrices(int priceGroupNumber) =>
        new(httpClient, Address(Services.Products), priceGroupNumber);

    /// <summary><c>/specialprices/{productSpecialPriceNumber}/specialpricesincurrency</c>.</summary>
    /// <param name="productSpecialPriceNumber">The special price these belong to.</param>
    /// <returns>The collection, scoped to that special price.</returns>
    /// <remarks>
    /// Reached from a special price's own number rather than from the price group, because that is
    /// how e-conomic addresses it — this collection hangs off a different path root than the one
    /// its parent is read from.
    /// </remarks>
    public ProductSpecialPriceInCurrencyResource ProductSpecialPricesInCurrency(int productSpecialPriceNumber) =>
        new(httpClient, Address(Services.Products), productSpecialPriceNumber);

    /// <summary><c>/productgroups/{productGroupNumber}/zones</c>, a group's VAT zones.</summary>
    /// <param name="productGroupNumber">The product group these zones belong to.</param>
    /// <returns>The collection, scoped to that product group.</returns>
    public ProductZoneResource ProductZones(int productGroupNumber) =>
        new(httpClient, Address(Services.Products), productGroupNumber);

    /// <summary><c>/Contacts</c> of the suppliers service.</summary>
    /// <remarks>
    /// A different shape from <see cref="CustomerContacts"/> despite the shared name, which is why
    /// both carry their service: this one hangs off <c>supplierNumber</c>.
    /// </remarks>
    public SupplierContactResource SupplierContacts => new(httpClient, Address(Services.Suppliers));

    /// <summary><c>/Groups</c>, the supplier groups.</summary>
    public SupplierGroupResource SupplierGroups => new(httpClient, Address(Services.Suppliers));

    private Uri Address(string service) => new(baseAddress, service);
}
