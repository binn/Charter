using Charter.Data.Notifications;
using Charter.Data.Teaching;
using Charter.Notifications;
using Charter.Teaching;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Charter.Data;

/// <summary>
/// Binds the seams other subsystems left open to Postgres.
/// </summary>
/// <remarks>
/// <para>
/// Teaching and notifications each ship a working in-process default so neither is blocked on a
/// table existing. Those defaults are real implementations, not stubs — but they lose everything
/// when the container restarts, and section 2.3 is unambiguous that the container restarts whenever
/// it likes. These are the durable ones.
/// </para>
/// <para>
/// Registered outright rather than with <c>TryAdd</c>, on purpose. The in-memory registrations are
/// <c>TryAdd</c>, so this wins whether it runs before them or after: last registration wins when one
/// service is resolved, and a <c>TryAdd</c> that runs later sees these and does nothing. The
/// alternative — matching <c>TryAdd</c> here too — would make which store an instance gets depend on
/// the order the extension methods happen to be called in <c>Program.cs</c>, which is a persistence
/// decision nobody would think to look for there.
/// </para>
/// <para>
/// Every store is a singleton that opens a scope per operation rather than holding a
/// <see cref="CharterDbContext"/>. The interfaces they implement are consumed by singletons —
/// <c>IEmailSender</c> holds the delivery log, the notification dispatcher holds the preference
/// store — so a scoped registration would be a captive-dependency failure at startup, and a
/// singleton DbContext would be a shared change tracker across requests.
/// </para>
/// </remarks>
public static class StoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Postgres-backed teaching, notification and invitation stores.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="DataServiceCollectionExtensions.AddCharterData"/>, so wiring the
    /// persistence layer up is one call and there is no way to get a <see cref="CharterDbContext"/>
    /// without also getting the stores that need one.
    /// </remarks>
    public static IServiceCollection AddCharterStores(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(CharterTime.System);

        // Section 13: the ledger a requester graduates through, the walkthrough cache that makes a
        // second open free, and the per-user cap on the one unbounded surface.
        services.AddSingleton<IConceptLedgerStore, EfConceptLedgerStore>();
        services.AddSingleton<IWalkthroughStore, EfWalkthroughStore>();
        services.AddSingleton<IExplainThisQuota, EfExplainThisQuota>();

        // Section 22 and change spec 001 C.3.
        services.AddSingleton<INotificationPreferenceStore, EfNotificationPreferenceStore>();
        services.AddSingleton<IEmailDeliveryLog, EfEmailDeliveryLog>();

        // Section 30.2. Scoped, because unlike the others it is consumed from a request.
        services.AddScoped<InvitationStore>();

        return services;
    }
}
