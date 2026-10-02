using Microsoft.Extensions.Options;

namespace OpenProjectRecurrenceService;

public sealed class RecurrenceProcessor(
    OpenProjectClient openProjectClient,
    RecurrenceCalculator recurrenceCalculator,
    GeneratedWorkPackageCopyPolicy generatedWorkPackageCopyPolicy,
    IOptions<OpenProjectOptions> options,
    ILogger<RecurrenceProcessor> logger)
{
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        var templates = await openProjectClient.GetTemplatesAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var template in templates)
        {
            if (template.NextOccurrence is null || string.IsNullOrWhiteSpace(template.GeneratedTypeName))
            {
                logger.LogWarning("Skipping template {TemplateId} because recurrence is not fully configured.", template.Id);
                continue;
            }

            var maximumOccurrences = options.Value.MaximumCatchUpOccurrencesPerTemplatePerRun;
            var scheduledOccurrence = template.NextOccurrence.Value;
            var processed = 0;

            while (processed < maximumOccurrences && recurrenceCalculator.IsDue(scheduledOccurrence, today, template.GenerateAhead))
            {
                try
                {
                    var generatedTypeId = await openProjectClient.ResolveGeneratedTypeIdAsync(template.GeneratedTypeName, cancellationToken);
                    var generatedWorkPackage = generatedWorkPackageCopyPolicy.Build(template, scheduledOccurrence, generatedTypeId);
                    var createdId = await openProjectClient.CreateGeneratedWorkPackageAsync(template, generatedWorkPackage, cancellationToken);

                    if (createdId <= 0)
                    {
                        throw new InvalidOperationException($"OpenProject did not return a valid generated work package id for template {template.Id} on scheduled occurrence {scheduledOccurrence:yyyy-MM-dd}.");
                    }

                    var nextOccurrence = recurrenceCalculator.GetNextOccurrence(scheduledOccurrence, template.RecurrenceType, template.Interval);
                    await openProjectClient.UpdateNextOccurrenceAsync(template.Id, nextOccurrence, cancellationToken);

                    var verified = await openProjectClient.VerifyUpdatedNextOccurrenceAsync(template.Id, nextOccurrence, cancellationToken);
                    if (!verified)
                    {
                        logger.LogError(
                            "Source work package {SourceId} reported a successful update but Next occurrence was not verified to {ExpectedNextOccurrence}.",
                            template.Id,
                            nextOccurrence);
                        break;
                    }

                    logger.LogInformation(
                        "Generated recurrence for template {TemplateId} on scheduled occurrence {ScheduledOccurrence}. Updated Next occurrence to {UpdatedValue}.",
                        template.Id,
                        scheduledOccurrence,
                        nextOccurrence);

                    scheduledOccurrence = nextOccurrence;
                    processed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to process template {TemplateId} for scheduled occurrence {ScheduledOccurrence}.",
                        template.Id,
                        scheduledOccurrence);
                    break;
                }
            }

            if (processed >= maximumOccurrences)
            {
                logger.LogWarning(
                    "Maximum catch-up limit reached for template {TemplateId}. Additional overdue occurrences were skipped to avoid generating an excessive number of work packages.",
                    template.Id);
            }
        }
    }
}
