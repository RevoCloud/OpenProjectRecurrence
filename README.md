# OpenProject Recurrence
An automation-service for recurring work packages in OpenProject.

We aim to use OpenProject to manage our maintenance and review cycles, both from a technical management standpoint as well as any and all recurring tasks for our ISO 27001 ISMS.
To do this, OpenProject must first be able to make work packages recurring items, which it currently cannot do.

This is a small vibe-coded SystemD Daemon, written in .NET, that leverages the OpenProject API to do just that.

## Core concept

Any OpenProject work package can potentially act as a recurring template.

There is NO separate recurrence-definition object.

The source work package is simultaneously:

1. the template for generated work packages;
2. the recurrence configuration;
3. the parent of all generated occurrences.

For example:

```text
Procedure #100
"Procedure controle back-ups"

Recurrence enabled: true
Generated type: Review
Recurrence type: Monthly
Interval: 1
Next occurrence: 2026-11-05
Generate ahead: 7
Due after: 14
```

may result in:

```text
Procedure #100
│
├── Review #201 - Controle back-ups - 2026-11
├── Review #225 - Controle back-ups - 2026-12
├── Review #249 - Controle back-ups - 2027-01
└── ...
```

The implementation MUST NOT assume that:

- the source work package is of type `Procedure`;
- the generated work package is of type `Review`.

Those are user-configurable OpenProject work package types.

The recurrence functionality should work for any work package type for which the recurrence custom fields have been enabled.

---

## OpenProject custom fields

The source work package contains the recurrence configuration.

Recurrence is discovered and managed through these custom fields. These fields are discovered by exact name.

### Recurrence enabled

Type:

```text
Boolean
```

If false or empty, the work package is not processed.

---

### Generated type

Type:

```text
List
```

The value contains the exact name of an OpenProject work package type.

Examples:

```text
Review
Task
Maintenance
Supplier Review
```

Resolve the name to the corresponding OpenProject work package type through API v3.

Do NOT hard-code OpenProject type IDs.

If the configured type does not exist, log a clear configuration error and skip that source work package.

---

### Recurrence type

Type:

```text
List
```

Design for the following values:

```text
Daily
Weekly
Monthly
Yearly
After completion
```

The design should make adding the other recurrence types straightforward without redesigning the OpenProject integration.

---

### Interval

Type:

```text
Integer
```

Examples:

```text
Monthly + Interval 1
= every month

Monthly + Interval 3
= every three months
```

---

### Next occurrence

Type:

```text
Date
```

This is the scheduled date of the next occurrence.

This field is visible and editable by OpenProject users.

The scheduler treats this value as authoritative.

The scheduler updates it after successfully generating an occurrence.

---

### Generate ahead

Type:

```text
Integer
```

Number of days before `Next occurrence` that the generated work package should be created.

Example:

```text
Next occurrence: 2026-11-05
Generate ahead: 7
```

The occurrence becomes eligible for generation on:

```text
2026-10-29
```

---

### Due after

Type:

```text
Integer
```

Number of days after the scheduled occurrence date that the generated work package is due.

Example:

```text
Next occurrence: 2026-11-05
Due after: 14
```

Generated work package due date:

```text
2026-11-19
```

IMPORTANT:

The due date is calculated relative to the scheduled occurrence date, NOT relative to the date on which the worker happens to generate the work package.

---

## Custom field discovery

Custom-field numeric IDs are not hard coded or configured.
We discover the fields through the OpenProject API using their human-readable names.

The expected field names are be configurable in `appsettings.json`.

For example:

```json
{
  "CustomFields": {
    "RecurrenceEnabled": "Recurrence enabled",
    "GeneratedType": "Generated type",
    "RecurrenceType": "Recurrence type",
    "Interval": "Interval",
    "NextOccurrence": "Next occurrence",
    "GenerateAhead": "Generate ahead",
    "DueAfter": "Due after"
  }
}
```

