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
/// Booking a draft invoice, which the generated write templates cannot express.
/// </summary>
/// <remarks>
/// e-conomic models booking as a <c>POST</c> to <c>/invoices/booked</c> carrying a reference to the
/// draft. That is an action on a draft rather than the creation of a booked invoice from a payload
/// describing one, so it has no <c>{Entity}Create</c> model and does not fit the shape every other
/// write follows. It is written by hand for the same reason <c>DELETE</c> is: the pattern is real,
/// and pretending otherwise would distort the generator.
/// </remarks>
public sealed partial class DraftInvoiceResource
{
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
