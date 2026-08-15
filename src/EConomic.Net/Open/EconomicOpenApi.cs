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
        /// <summary>The accounting years service, covering years and their periods.</summary>
        public const string AccountingYears = "accountingyearsapi/v2.0.1/";

        /// <summary>The accounts service, covering accounts, key figure codes and total intervals.</summary>
        public const string Accounts = "accountsapi/v7.0.0/";

        /// <summary>The booked entries service, read-only apart from matching.</summary>
        public const string BookedEntries = "bookedEntriesapi/v4.0.0/";

        /// <summary>The budgets service, which publishes budget figures and nothing else.</summary>
        public const string Budgets = "budgetsapi/v2.0.0/";

        /// <summary>The customers service, covering customers, contacts and delivery locations.</summary>
        public const string Customers = "customersapi/v3.1.0/";

        /// <summary>The dimensions service, covering dimensions, their values and distributions.</summary>
        public const string Dimensions = "dimensionsapi/v5.3.1/";

        /// <summary>The documents service, covering documents attached to vouchers.</summary>
        public const string Documents = "documentsapi/v4.0.1/";

        /// <summary>The journals service, covering journals, draft entries and accruals.</summary>
        public const string Journals = "journalsapi/v15.0.0/";

        /// <summary>The products service, covering products, their groups, and pricing.</summary>
        public const string Products = "productsapi/v2.0.0/";

        /// <summary>The projects service, covering projects, activities, time and mileage.</summary>
        /// <remarks>
        /// Every collection but employees and employee groups needs the Project module enabled on
        /// the agreement; without it e-conomic answers <c>403</c> with
        /// <c>AccessDeniedAgreementMissingModules</c>.
        /// </remarks>
        public const string Projects = "projectsapi/v1.1.0/";

        /// <summary>The quote-to-cash service, covering draft invoices and the lines of sales documents.</summary>
        public const string Sales = "q2capi/v5.3.0/";

        /// <summary>The subscriptions service, covering subscriptions, subscribers and lines.</summary>
        public const string Subscriptions = "subscriptionsapi/v6.0.3/";

        /// <summary>The suppliers service, which covers only contacts and groups.</summary>
        public const string Suppliers = "suppliersapi/v2.0.0/";

        /// <summary>The webhooks service, for subscribing to agreement events.</summary>
        public const string Webhooks = "webhooksapi/v1.0.0/";
    }

    /// <summary><c>/AccountingYears</c>, identified by the year as a string.</summary>
    /// <remarks>
    /// The one collection here with no cursor listing: e-conomic publishes only the classic paged
    /// one, so enumerating it pages rather than following a cursor, and asking for a cursor page
    /// throws rather than silently doing something else.
    /// </remarks>
    public AccountingYearResource AccountingYears => new(httpClient, Address(Services.AccountingYears));

    /// <summary><c>/accountingYears/periods</c>, across every year.</summary>
    public AccountingYearPeriodResource AccountingYearPeriods =>
        new(httpClient, Address(Services.AccountingYears));

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

    /// <summary><c>/booked-entries</c>, the posted entries. Read-only.</summary>
    /// <remarks>
    /// e-conomic publishes a <c>POST /booked-entries/match</c> that matches entries against one
    /// another. It is an action on existing records rather than a create, so it is not part of this
    /// resource and is not expressed yet.
    /// </remarks>
    public BookedEntryResource BookedEntries => new(httpClient, Address(Services.BookedEntries));

    /// <summary><c>/booked-entries/matched-pairs</c>, entries already matched to each other.</summary>
    /// <remarks>
    /// Cursor only: this collection publishes neither the classic paged listing nor a count, so it
    /// can be enumerated and filtered but not sorted, paged by number, or counted. The surface says
    /// so — there is no <c>OrderBy</c> to reach for and its sort surface is empty.
    /// </remarks>
    public MatchedBookedEntriesPairResource MatchedBookedEntriesPairs =>
        new(httpClient, Address(Services.BookedEntries));

    /// <summary><c>/budget-figures</c>, the budget by account and period.</summary>
    public BudgetFigureResource BudgetFigures => new(httpClient, Address(Services.Budgets));

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

    /// <summary><c>/AttachedDocuments</c>, the files attached to vouchers.</summary>
    /// <remarks>
    /// Without a create: e-conomic attaches a document as <c>multipart/form-data</c> carrying the
    /// file itself, which is not a shape this facade expresses. Nor is the PDF download, which
    /// answers with a binary body rather than JSON. Both are reported by the generator rather than
    /// quietly missing.
    /// </remarks>
    public AttachedDocumentResource AttachedDocuments => new(httpClient, Address(Services.Documents));

    /// <summary><c>/dimensions</c>, the dimensions an agreement defines.</summary>
    public DimensionResource Dimensions => new(httpClient, Address(Services.Dimensions));

    /// <summary><c>/values</c>, the values each dimension can take.</summary>
    /// <remarks>
    /// Deleted by payload rather than by identifier, which is how e-conomic publishes it: a value is
    /// keyed by dimension and key together, and neither is in the path.
    /// </remarks>
    public DimensionValueResource DimensionValues => new(httpClient, Address(Services.Dimensions));

    /// <summary><c>/distributions</c>, how a dimension's values divide an amount. Read-only.</summary>
    public DimensionDistributionResource DimensionDistributions =>
        new(httpClient, Address(Services.Dimensions));

    /// <summary><c>/dimension-data/accounts</c>, the dimensions attached to accounts.</summary>
    public DimensionAccountResource DimensionAccounts => new(httpClient, Address(Services.Dimensions));

    /// <summary><c>/dimension-data/booked-entries</c>, the dimensions attached to booked entries.</summary>
    public DimensionBookedEntryResource DimensionBookedEntries =>
        new(httpClient, Address(Services.Dimensions));

    /// <summary><c>/dimension-data/booked-invoice-lines</c>. Read-only.</summary>
    public DimensionBookedInvoiceLineResource DimensionBookedInvoiceLines =>
        new(httpClient, Address(Services.Dimensions));

    /// <summary><c>/dimension-data/budget-figures</c>, the dimensions attached to budget figures.</summary>
    public DimensionBudgetFigureResource DimensionBudgetFigures =>
        new(httpClient, Address(Services.Dimensions));

    /// <summary><c>/dimension-data/draft-entries</c>, the dimensions attached to draft entries.</summary>
    public DimensionDraftEntryResource DimensionDraftEntries =>
        new(httpClient, Address(Services.Dimensions));

    /// <summary><c>/dimension-data/sales-document-lines</c>, the dimensions on invoice and order lines.</summary>
    public DimensionSalesDocumentLineResource DimensionSalesDocumentLines =>
        new(httpClient, Address(Services.Dimensions));

    /// <summary><c>/journals</c>, the journals entries are posted through.</summary>
    /// <remarks>
    /// Without booking: e-conomic publishes five separate <c>POST</c> actions that book a journal's
    /// entries, and an action on existing records is not a shape this facade expresses. Booking a
    /// draft invoice is available on the legacy surface, at <c>client.Rest.DraftInvoices</c>.
    /// </remarks>
    public JournalResource Journals => new(httpClient, Address(Services.Journals));

    /// <summary><c>/draft-entries</c>, the unposted entries across every journal.</summary>
    public JournalDraftEntryResource JournalDraftEntries => new(httpClient, Address(Services.Journals));

    /// <summary><c>/accruals</c>, the accrual schedules attached to draft entries.</summary>
    /// <remarks>
    /// Deleted by payload, like dimension values, and for the same reason: an accrual is keyed by
    /// journal and entry together.
    /// </remarks>
    public JournalDraftEntryAccrualResource JournalDraftEntryAccruals =>
        new(httpClient, Address(Services.Journals));

    /// <summary><c>/products</c>, whose identifier is a string rather than a number.</summary>
    /// <remarks>
    /// Without a count, and its paged listing sits at <c>/productspaged/paged</c> rather than
    /// beside it. Both are e-conomic's shape, not an omission here.
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

    /// <summary><c>/Projects</c>, the projects work is booked against.</summary>
    /// <remarks>
    /// Without <c>/Projects/allowed</c>, which is not a listing of projects but a question about one
    /// employee — it requires an <c>employeeNumber</c> and answers with the projects that employee
    /// may book to. An enquiry rather than a collection, so it is not a resource here.
    /// </remarks>
    public ProjectResource Projects => new(httpClient, Address(Services.Projects));

    /// <summary><c>/ProjectGroups</c>, which every project belongs to.</summary>
    public ProjectGroupResource ProjectGroups => new(httpClient, Address(Services.Projects));

    /// <summary><c>/ProjectStatuses</c>, the statuses a project moves through.</summary>
    public ProjectStatusResource ProjectStatuses => new(httpClient, Address(Services.Projects));

    /// <summary><c>/Activities</c>, the catalogue of activities that can be booked.</summary>
    /// <remarks>
    /// The master record, addressed by <c>activityNumber</c> from time entries and from
    /// <see cref="ProjectActivityAssignments"/>. Not to be confused with that: this is the activity
    /// itself, with its group, prices and cut-off date.
    /// </remarks>
    public ProjectActivityResource ProjectActivities => new(httpClient, Address(Services.Projects));

    /// <summary><c>/project-activities</c>, an activity assigned to a project.</summary>
    /// <remarks>
    /// A record of its own — its own number, a date range, a responsible employee and a completed
    /// flag — rather than a view of <see cref="ProjectActivities"/>. e-conomic calls both of these
    /// "activities", which is why this one carries the longer name.
    /// </remarks>
    public ProjectActivityAssignmentResource ProjectActivityAssignments =>
        new(httpClient, Address(Services.Projects));

    /// <summary><c>/ActivityGroups</c>, which every activity belongs to.</summary>
    public ProjectActivityGroupResource ProjectActivityGroups =>
        new(httpClient, Address(Services.Projects));

    /// <summary><c>/Employees</c>, the employees who book time and mileage.</summary>
    /// <remarks>
    /// This projection carries the contact details — phone and email — and
    /// <see cref="ProjectEmployeeDetails"/> carries the project settings. They are the same records
    /// under both, addressed by the same number.
    /// </remarks>
    public ProjectEmployeeResource ProjectEmployees => new(httpClient, Address(Services.Projects));

    /// <summary><c>/project-employees</c>, the same employees with their project settings.</summary>
    /// <remarks>
    /// Rates, approval and invoicing rights, employee type and an address. Note it is not a superset
    /// of <see cref="ProjectEmployees"/>: phone and email appear only there.
    /// </remarks>
    public ProjectEmployeeDetailsResource ProjectEmployeeDetails =>
        new(httpClient, Address(Services.Projects));

    /// <summary><c>/EmployeeGroups</c>, which every employee belongs to.</summary>
    /// <remarks>
    /// e-conomic publishes these twice, at <c>/EmployeeGroups</c> and
    /// <c>/project-employeegroups</c>, and the two answer with the same records down to the
    /// <c>objectVersion</c> hash. One type covers both.
    /// </remarks>
    public ProjectEmployeeGroupResource ProjectEmployeeGroups =>
        new(httpClient, Address(Services.Projects));

    /// <summary><c>/project-customers</c>, the customers projects can be attached to. Read-only.</summary>
    public ProjectCustomerResource ProjectCustomers => new(httpClient, Address(Services.Projects));

    /// <summary><c>/CostTypes</c>, the cost categories a project's costs fall into. Read-only.</summary>
    public ProjectCostTypeResource ProjectCostTypes => new(httpClient, Address(Services.Projects));

    /// <summary><c>/CostTypeGroups</c>, which every cost type belongs to. Read-only.</summary>
    public ProjectCostTypeGroupResource ProjectCostTypeGroups =>
        new(httpClient, Address(Services.Projects));

    /// <summary><c>/TimeEntries</c>, the hours booked to a project.</summary>
    /// <remarks>
    /// Without <c>/TimeEntries/approve</c>: approving is an action on records that already exist,
    /// which is not a shape this facade expresses.
    /// </remarks>
    public ProjectTimeEntryResource ProjectTimeEntries => new(httpClient, Address(Services.Projects));

    /// <summary><c>/TimeEntryPrices</c>, the cost and sales rate per activity. Read-only.</summary>
    /// <remarks>
    /// Publishes no count endpoint, so this resource offers no <c>CountAsync</c>.
    /// </remarks>
    public ProjectTimeEntryPriceResource ProjectTimeEntryPrices =>
        new(httpClient, Address(Services.Projects));

    /// <summary><c>/Mileages</c>, the distances booked to a project.</summary>
    /// <remarks>Without <c>/Mileages/approve</c>, for the same reason as time entries.</remarks>
    public ProjectMileageResource ProjectMileages => new(httpClient, Address(Services.Projects));

    /// <summary><c>/MileagePrices</c>, the cost and sales rate per distance. Read-only.</summary>
    /// <remarks>
    /// Publishes no count endpoint, so this resource offers no <c>CountAsync</c>.
    /// </remarks>
    public ProjectMileagePriceResource ProjectMileagePrices =>
        new(httpClient, Address(Services.Projects));

    /// <summary><c>/invoices/drafts</c>, the invoices not yet booked.</summary>
    /// <remarks>
    /// The counterpart to <c>client.Rest.DraftInvoices</c>, and not a replacement for it: booking a
    /// draft is published only on the legacy surface, and this service has no equivalent. Its
    /// <c>PUT /invoices/drafts/{n}/sensitive-data</c> is likewise not expressed — it writes one
    /// designated field rather than the record.
    /// </remarks>
    public SalesDraftInvoiceResource SalesDraftInvoices => new(httpClient, Address(Services.Sales));

    /// <summary><c>/invoices/drafts/lines</c>, read-only across every draft invoice.</summary>
    /// <remarks>
    /// Reading is a collection of its own; writing is not. e-conomic publishes the create, update and
    /// delete under <c>/invoices/drafts/{documentId}/lines</c>, whose listing is a plain array rather
    /// than a collection — the same split the products and subscriptions services have.
    /// </remarks>
    public SalesDraftInvoiceLineResource SalesDraftInvoiceLines => new(httpClient, Address(Services.Sales));

    /// <summary><c>/invoices/booked/lines</c>, read-only across every booked invoice.</summary>
    public SalesBookedInvoiceLineResource SalesBookedInvoiceLines =>
        new(httpClient, Address(Services.Sales));

    /// <summary><c>/orders/drafts/lines</c>, the lines of orders not yet sent. Read-only.</summary>
    /// <remarks>
    /// e-conomic scopes these by a status in the path, taking <c>drafts</c>, <c>sent</c> or
    /// <c>archived</c>. They are three properties rather than one method taking the status, which is
    /// how the legacy surface already models the documents themselves — <c>client.Rest.DraftOrders</c>,
    /// <c>SentOrders</c> and <c>ArchivedOrders</c>. All three carry the same
    /// <see cref="SalesOrderLine"/>.
    /// </remarks>
    public SalesOrderLineResource SalesDraftOrderLines =>
        new(httpClient, Address(Services.Sales), "drafts");

    /// <summary><c>/orders/sent/lines</c>, the lines of orders already sent. Read-only.</summary>
    public SalesOrderLineResource SalesSentOrderLines =>
        new(httpClient, Address(Services.Sales), "sent");

    /// <summary><c>/orders/archived/lines</c>, the lines of archived orders. Read-only.</summary>
    public SalesOrderLineResource SalesArchivedOrderLines =>
        new(httpClient, Address(Services.Sales), "archived");

    /// <summary><c>/quotes/drafts/lines</c>, the lines of quotes not yet sent. Read-only.</summary>
    /// <remarks>Scoped by status exactly as the order lines are.</remarks>
    public SalesQuoteLineResource SalesDraftQuoteLines =>
        new(httpClient, Address(Services.Sales), "drafts");

    /// <summary><c>/quotes/sent/lines</c>, the lines of quotes already sent. Read-only.</summary>
    public SalesQuoteLineResource SalesSentQuoteLines =>
        new(httpClient, Address(Services.Sales), "sent");

    /// <summary><c>/quotes/archived/lines</c>, the lines of archived quotes. Read-only.</summary>
    public SalesQuoteLineResource SalesArchivedQuoteLines =>
        new(httpClient, Address(Services.Sales), "archived");

    /// <summary><c>/Subscriptions</c>, the recurring billing definitions.</summary>
    public SubscriptionResource Subscriptions => new(httpClient, Address(Services.Subscriptions));

    /// <summary><c>/Subscribers</c>, the customers a subscription is billed to.</summary>
    public SubscriptionSubscriberResource SubscriptionSubscribers =>
        new(httpClient, Address(Services.Subscriptions));

    /// <summary><c>/subscriptionlines</c>, read-only across every subscription.</summary>
    /// <remarks>
    /// Reading is a collection of its own; writing is not. e-conomic publishes the create, update
    /// and delete under <c>/subscriptions/{subscriptionNumber}/lines</c>, whose listing is a plain
    /// array rather than a collection, so it is not expressed here — the same split the products
    /// service has for sales prices in currency.
    /// </remarks>
    public SubscriptionLineResource SubscriptionLines => new(httpClient, Address(Services.Subscriptions));

    /// <summary><c>/Contacts</c> of the suppliers service.</summary>
    /// <remarks>
    /// A different shape from <see cref="CustomerContacts"/> despite the shared name, which is why
    /// both carry their service: this one hangs off <c>supplierNumber</c>.
    /// </remarks>
    public SupplierContactResource SupplierContacts => new(httpClient, Address(Services.Suppliers));

    /// <summary><c>/Groups</c>, the supplier groups.</summary>
    public SupplierGroupResource SupplierGroups => new(httpClient, Address(Services.Suppliers));

    /// <summary><c>/webhooks</c>, keyed by the event type they subscribe to.</summary>
    /// <remarks>
    /// e-conomic also publishes <c>/EventTypes</c>, a plain array rather than a collection, so it
    /// has no query, no paging and no place on this surface yet.
    /// </remarks>
    public WebhookResource Webhooks => new(httpClient, Address(Services.Webhooks));

    private Uri Address(string service) => new(baseAddress, service);
}
