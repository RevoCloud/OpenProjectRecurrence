using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenProjectRecurrenceService;

namespace OpenProjectRecurrenceService.Tests;

[TestClass]
public sealed class RecurrenceCalculatorTests
{
    [TestMethod]
    public void MonthlyRecurrenceAddsMonthsAndClampsToMonthEnd()
    {
        var calculator = new RecurrenceCalculator();

        var result = calculator.GetNextOccurrence(new DateOnly(2026, 01, 31), RecurrenceType.Monthly, 1);

        Assert.AreEqual(new DateOnly(2026, 02, 28), result);
    }

    [TestMethod]
    public void DailyRecurrenceAddsDays()
    {
        var calculator = new RecurrenceCalculator();

        var result = calculator.GetNextOccurrence(new DateOnly(2026, 11, 05), RecurrenceType.Daily, 2);

        Assert.AreEqual(new DateOnly(2026, 11, 07), result);
    }

    [TestMethod]
    public void AfterCompletionRecurrenceUsesDayOffset()
    {
        var calculator = new RecurrenceCalculator();

        var result = calculator.GetNextOccurrence(new DateOnly(2026, 11, 05), RecurrenceType.AfterCompletion, 12);

        Assert.AreEqual(new DateOnly(2026, 11, 17), result);
    }

    [TestMethod]
    public void SubjectFormattingUsesScheduledOccurrenceDate()
    {
        var policy = new GeneratedWorkPackageCopyPolicy();
        var source = new WorkPackageTemplate
        {
            Id = 100,
            ProjectId = 42,
            Subject = "Controle back-ups",
            DueAfter = 14,
            RecurrenceEnabled = true,
            Relations = [new RelationLink { Type = "related to", Id = 20, Name = "ISO control" }]
        };

        var generated = policy.Build(source, new DateOnly(2026, 11, 05), 7);

        Assert.AreEqual("Controle back-ups - 2026-11", generated.Subject);
        Assert.AreEqual(new DateOnly(2026, 11, 05), generated.StartDate);
        Assert.AreEqual(new DateOnly(2026, 11, 19), generated.DueDate);
        Assert.IsFalse(generated.RecurrenceEnabled);
        Assert.AreEqual(1, generated.Relations.Count);
    }
}
