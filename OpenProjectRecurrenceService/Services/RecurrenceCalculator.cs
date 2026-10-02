namespace OpenProjectRecurrenceService;

public sealed class RecurrenceCalculator
{
    public DateOnly GetNextOccurrence(DateOnly currentOccurrence, RecurrenceType recurrenceType, int interval)
    {
        if (interval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        }

        return recurrenceType switch
        {
            RecurrenceType.Daily => currentOccurrence.AddDays(interval),
            RecurrenceType.Weekly => currentOccurrence.AddDays(interval * 7),
            RecurrenceType.Monthly => AddMonths(currentOccurrence, interval),
            RecurrenceType.Yearly => AddYears(currentOccurrence, interval),
            RecurrenceType.AfterCompletion => currentOccurrence.AddDays(interval),
            _ => throw new ArgumentOutOfRangeException(nameof(recurrenceType), recurrenceType, "Unsupported recurrence type.")
        };
    }

    public bool IsDue(DateOnly nextOccurrence, DateOnly today, int generateAhead)
    {
        return nextOccurrence.AddDays(-generateAhead) <= today;
    }

    private static DateOnly AddMonths(DateOnly currentOccurrence, int months)
    {
        var totalMonths = currentOccurrence.Month - 1 + months;
        var targetYear = currentOccurrence.Year + (totalMonths / 12);
        var targetMonth = (totalMonths % 12) + 1;
        var lastDayOfTargetMonth = DateTime.DaysInMonth(targetYear, targetMonth);
        var day = Math.Min(currentOccurrence.Day, lastDayOfTargetMonth);

        return new DateOnly(targetYear, targetMonth, day);
    }

    private static DateOnly AddYears(DateOnly currentOccurrence, int years)
    {
        var targetYear = currentOccurrence.Year + years;
        var lastDayOfTargetMonth = DateTime.DaysInMonth(targetYear, currentOccurrence.Month);
        var day = Math.Min(currentOccurrence.Day, lastDayOfTargetMonth);

        return new DateOnly(targetYear, currentOccurrence.Month, day);
    }
}
