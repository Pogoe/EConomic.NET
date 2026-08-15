namespace EConomic.IntegrationTests;

/// <summary>
/// Which collections cannot be filled with a record, and why.
/// </summary>
/// <remarks>
/// <para>
/// A sweep over a collection that has no records proves far less than it appears to: the call
/// round-trips and maps nothing, so a mapping that is wrong or missing entirely passes. The seed
/// fills every collection it can, and this names the rest — each with the reason it is out of
/// reach, so "empty" is a decision on the record rather than an accident nobody noticed.
/// </para>
/// <para>
/// The reasons fall into four kinds. Some records only exist after a state change no endpoint
/// publishes: nothing promotes a draft order into a sent one. Some are created in the e-conomic
/// user interface and nowhere else. Some need a write this library does not express yet, which
/// makes the entry a worklist item rather than a permanent fact. And two need booking, which
/// leaves records that cannot be removed again.
/// </para>
/// <para>
/// <see cref="CoverageTests"/> checks this in both directions. A collection that is empty and not
/// named here fails, so a new collection cannot arrive uncovered; and a collection named here that
/// turns out to have records also fails, so an entry cannot quietly stop being true — the same
/// terms every other curated table in this repository is held to.
/// </para>
/// </remarks>
internal static class AgreementCoverage
{
    /// <summary>Collections the seed cannot fill, keyed by <c>{surface}.{property}</c>.</summary>
    public static IReadOnlyDictionary<string, string> Unseedable { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // No endpoint promotes a draft into these. The legacy API can create a draft order or
            // quote and archive nothing; sending and archiving happen in the user interface.
            ["Rest.SentOrders"] = "no endpoint promotes a draft order to sent",
            ["Rest.ArchivedOrders"] = "no endpoint archives an order",
            ["Rest.SentQuotes"] = "no endpoint promotes a draft quote to sent",
            ["Rest.ArchivedQuotes"] = "no endpoint archives a quote",
            ["Rest.SentInvoices"] = "an invoice is sent from the user interface, not the API",

            // Booking consumes a draft and produces a record with no delete. Behind
            // ECONOMIC_RUN_BOOKING_TESTS for that reason, so the coverage sweep must not depend
            // on it.
            ["Rest.PaidInvoices"] = "needs a booked invoice, which cannot be deleted again",
            ["Rest.OverdueInvoices"] = "needs a booked invoice past its due date",

            // Three that look unseedable and are not, recorded here because the reasoning is worth
            // keeping: the legacy API publishes no create for employees, departments or
            // departmental distributions, and all three fill anyway. The two surfaces name the same
            // records differently — a projects-service employee is a legacy employee, and a
            // dimension value is a legacy department. The seed reaches them through the OpenAPI
            // surface, so they are deliberately absent from this table. "The legacy API cannot
            // create it" is not the same statement as "this library cannot create it".

            // Writes this library does not express yet. These are worklist entries: expressing the
            // write removes the entry, and the check above then insists on it.
            ["Open.AttachedDocuments"] = "upload is multipart/form-data, not expressed yet",
            ["Open.ProductSalesPricesInCurrency"] = "nested under a product, not expressed yet",
            ["Open.SubscriptionLines"] = "nested under a subscription, not expressed yet",
            ["Open.DimensionDistributions"] = "the distributions property has no single shape",

            // Lines of a sales document, which the quote-to-cash service publishes no way to create
            // — its draft invoice carries no lines. Only the draft invoice family is reachable
            // through the legacy surface, so the sent, archived and booked views stay empty.
            ["Open.SalesSentOrderLines"] = "needs a sent order, which nothing promotes a draft into",
            ["Open.SalesArchivedOrderLines"] = "needs an archived order, which nothing archives",
            ["Open.SalesSentQuoteLines"] = "needs a sent quote, which nothing promotes a draft into",
            ["Open.SalesArchivedQuoteLines"] = "needs an archived quote, which nothing archives",
            ["Open.SalesDraftOrderLines"] = "the quote-to-cash order carries no lines to create",
            ["Open.SalesDraftQuoteLines"] = "the quote-to-cash quote carries no lines to create",

            // Derived from a booked invoice rather than written directly.
            ["Open.DimensionBookedInvoiceLines"] = "needs a booked invoice, which cannot be deleted again",
        };
}
