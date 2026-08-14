using Generated = EConomic.Rest.Generated;

namespace EConomic.Rest;

/// <summary>How e-conomic should send an invoice when it is booked.</summary>
/// <remarks>
/// A closed set on the wire, so it is an enum here rather than a string. The generated enums stay
/// internal because a spec change adding a member would be a breaking change for consumers; this
/// one is hand-written and therefore ours to version.
/// </remarks>
public enum EconomicInvoiceDelivery
{
    /// <summary>Book the invoice without sending it.</summary>
    None,

    /// <summary>Send it as an e-invoice over EAN.</summary>
    Ean,

    /// <summary>Send it by email.</summary>
    Email,
}

/// <summary>
/// Confirms a delete that takes no identifier and removes every record in a collection.
/// </summary>
/// <remarks>
/// The parameter exists to be inconvenient. e-conomic's bulk delete looks exactly like an ordinary
/// one at the call site — no identifier, no filter, no undo — so the intent is spelled out in the
/// argument instead. The default value is <see cref="Unspecified"/> and is rejected, which means
/// <c>default</c> and an uninitialised field both fail rather than deleting an agreement's work.
/// </remarks>
public enum DraftInvoiceBulkDelete
{
    /// <summary>No confirmation was given. Rejected.</summary>
    Unspecified = 0,

    /// <summary>Delete every draft invoice on the agreement.</summary>
    EveryDraft = 1,
}

/// <summary>
/// The draft invoice operations the generated write templates cannot express.
/// </summary>
/// <remarks>
/// Both are shapes that occur once in the whole API, and encoding either in the generator would
/// distort it for no gain. Booking is an <em>action</em> on a draft — a <c>POST</c> to
/// <c>/invoices/booked</c> carrying a reference to it — rather than the creation of a booked
/// invoice from a payload describing one, so it has no <c>{Entity}Create</c> model. The bulk delete
/// is a <c>DELETE</c> on a collection rather than on a record, and so takes no identifier at all.
/// </remarks>
public sealed partial class DraftInvoiceResource
{
    /// <summary>Deletes <strong>every</strong> draft invoice on the agreement.</summary>
    /// <param name="confirmation">Must be <see cref="DraftInvoiceBulkDelete.EveryDraft"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes once the drafts are gone.</returns>
    /// <remarks>
    /// <strong>This deletes all of them.</strong> e-conomic exposes it as a <c>DELETE</c> on the
    /// collection itself — <c>DELETE /invoices/drafts</c> — which takes no identifier and no filter,
    /// and there is no undo. It is a plausible typo away from the single-draft delete, so the
    /// confirmation is required and has no usable default.
    /// <para>
    /// It is deliberately named apart from <c>DeleteAsync</c> as well, so the two never resolve to
    /// each other by overload.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">No confirmation was given.</exception>
    /// <exception cref="Exceptions.EconomicApiException">The request failed.</exception>
    public Task DeleteEveryDraftAsync(
        DraftInvoiceBulkDelete confirmation,
        CancellationToken cancellationToken = default)
    {
        if (confirmation != DraftInvoiceBulkDelete.EveryDraft)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confirmation),
                confirmation,
                $"Deleting every draft invoice requires {nameof(DraftInvoiceBulkDelete)}."
                + $"{nameof(DraftInvoiceBulkDelete.EveryDraft)}. Use DeleteAsync to delete one.");
        }

        return FacadeTransport.DeleteAsync(_httpClient, "invoices/drafts", cancellationToken);
    }

    /// <summary>Books a draft invoice, turning it into a booked one.</summary>
    /// <param name="draftInvoiceNumber">The draft to book.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The booked invoice.</returns>
    /// <remarks>
    /// <strong>Booking is not reversible.</strong> See the overload taking a
    /// <paramref name="draftInvoiceNumber"/> and a delivery method for the details.
    /// <para>
    /// This overload exists so the common call reads like every other one in the library, where the
    /// cancellation token follows the identifier. Without it, <c>BookAsync(number, token)</c> binds
    /// the token to the optional invoice number instead and fails to compile.
    /// </para>
    /// </remarks>
    /// <exception cref="Exceptions.EconomicApiException">The request failed.</exception>
    public Task<BookedInvoice> BookAsync(int draftInvoiceNumber, CancellationToken cancellationToken) =>
        BookAsync(draftInvoiceNumber, bookWithNumber: null, EconomicInvoiceDelivery.None, cancellationToken);

    /// <summary>Books a draft invoice, turning it into a booked one.</summary>
    /// <param name="draftInvoiceNumber">The draft to book.</param>
    /// <param name="bookWithNumber">The invoice number to book with. Omit to let e-conomic assign the next one.</param>
    /// <param name="sendBy">Whether e-conomic should also send the invoice.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The booked invoice.</returns>
    /// <remarks>
    /// <strong>Booking is not reversible.</strong> A booked invoice is part of the accounting
    /// record: it cannot be edited or deleted, only corrected with a credit note. The draft is gone
    /// once this returns.
    /// <para>
    /// See <see href="https://restdocs.e-conomic.com/#post-invoices-booked"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="Exceptions.EconomicApiException">The request failed.</exception>
    public async Task<BookedInvoice> BookAsync(
        int draftInvoiceNumber,
        int? bookWithNumber = null,
        EconomicInvoiceDelivery sendBy = EconomicInvoiceDelivery.None,
        CancellationToken cancellationToken = default)
    {
        var body = new Generated.ForBookingAnInvoice
        {
            DraftInvoice = new() { DraftInvoiceNumber = draftInvoiceNumber },
        };

        if (bookWithNumber is { } number)
        {
            body.BookWithNumber = number;
        }

        // The wire values are "none", "EAN" and "Email" — inconsistently cased, and the server is
        // strict about it, so they are spelled out rather than derived from the member names.
        body.SendBy = FacadeTransport.ParseEnum(
            sendBy switch
            {
                EconomicInvoiceDelivery.Ean => "EAN",
                EconomicInvoiceDelivery.Email => "Email",
                _ => "none",
            },
            body.SendBy);

        var response = await FacadeTransport.SendAsync(
            () => _client.PostInvoicesBookedAsync(body, cancellationToken),
            "POST /invoices/booked").ConfigureAwait(false);

        return BookedInvoicePageSource.Map(response);
    }
}
