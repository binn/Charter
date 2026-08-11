using Charter.Domain;

namespace Charter.Budgets;

/// <summary>
/// The half-open window <c>[Start, End)</c> a budget's spend is counted over, and when it resets.
/// </summary>
/// <param name="Start">Inclusive.</param>
/// <param name="End">Exclusive. This is also the reset instant a <c>queue_until_reset</c> shows.</param>
public readonly record struct BudgetPeriodWindow(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>Whether an instant falls inside the window.</summary>
    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;

    /// <summary>
    /// The current window for <paramref name="budget"/> at <paramref name="instant"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All arithmetic is UTC. A budget period that followed the viewer's timezone would reset at a
    /// different moment for each person reading the dashboard, and "did it reset yet" is exactly the
    /// question a queued session's date is answering.
    /// </para>
    /// <para>
    /// <see cref="Budget.PeriodAnchor"/> is the billing day or fiscal year start (section 34.2).
    /// Where it is absent the calendar is used: months start on the 1st, weeks on Monday, quarters
    /// in January, and a fiscal year in January too — which is a guess, and the reason the anchor
    /// exists for the many organisations it is wrong for.
    /// </para>
    /// </remarks>
    public static BudgetPeriodWindow For(Budget budget, DateTimeOffset instant)
    {
        ArgumentNullException.ThrowIfNull(budget);

        var now = instant.ToUniversalTime();

        return budget.Period switch
        {
            BudgetPeriod.Daily => Daily(budget, now),
            BudgetPeriod.Weekly => Weekly(budget, now),
            BudgetPeriod.Monthly => Monthly(budget, now, months: 1),
            BudgetPeriod.Quarterly => Monthly(budget, now, months: 3),
            BudgetPeriod.Rolling30Days => new BudgetPeriodWindow(now.AddDays(-30), now.AddDays(1)),
            BudgetPeriod.FiscalYear => FiscalYear(budget, now),

            // A campaign budget is its own window (section 34.7). Create() guarantees both ends.
            BudgetPeriod.OneOff => new BudgetPeriodWindow(
                budget.StartsAt ?? now,
                budget.EndsAt ?? now.AddYears(1)),
            _ => Monthly(budget, now, months: 1),
        };
    }

    private static BudgetPeriodWindow Daily(Budget budget, DateTimeOffset now)
    {
        var hour = budget.PeriodAnchor?.ToUniversalTime().Hour ?? 0;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, TimeSpan.Zero);

        if (start > now)
        {
            start = start.AddDays(-1);
        }

        return new BudgetPeriodWindow(start, start.AddDays(1));
    }

    private static BudgetPeriodWindow Weekly(Budget budget, DateTimeOffset now)
    {
        var anchorDay = budget.PeriodAnchor?.ToUniversalTime().DayOfWeek ?? DayOfWeek.Monday;
        var midnight = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        var back = ((int)midnight.DayOfWeek - (int)anchorDay + 7) % 7;
        var start = midnight.AddDays(-back);

        return new BudgetPeriodWindow(start, start.AddDays(7));
    }

    private static BudgetPeriodWindow Monthly(Budget budget, DateTimeOffset now, int months)
    {
        // The billing day, clamped into short months: a budget anchored on the 31st still rolls over
        // in February rather than skipping it.
        var anchorDay = Math.Clamp(budget.PeriodAnchor?.ToUniversalTime().Day ?? 1, 1, 28);

        var start = new DateTimeOffset(now.Year, now.Month, anchorDay, 0, 0, 0, TimeSpan.Zero);

        if (months > 1)
        {
            // Quarters run from the anchor's month, so a fiscal quarter starting in February keeps
            // its own boundaries instead of being forced onto the calendar's.
            var anchorMonth = budget.PeriodAnchor?.ToUniversalTime().Month ?? 1;
            var offset = ((now.Month - anchorMonth) % months + months) % months;
            start = start.AddMonths(-offset);
        }

        if (start > now)
        {
            start = start.AddMonths(-months);
        }

        return new BudgetPeriodWindow(start, start.AddMonths(months));
    }

    private static BudgetPeriodWindow FiscalYear(Budget budget, DateTimeOffset now)
    {
        var anchor = budget.PeriodAnchor?.ToUniversalTime();
        var month = anchor?.Month ?? 1;
        var day = Math.Clamp(anchor?.Day ?? 1, 1, 28);

        var start = new DateTimeOffset(now.Year, month, day, 0, 0, 0, TimeSpan.Zero);

        if (start > now)
        {
            start = start.AddYears(-1);
        }

        return new BudgetPeriodWindow(start, start.AddYears(1));
    }
}
