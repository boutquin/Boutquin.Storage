# Security policy

## Supported versions

Security fixes target the latest 2.0.x release of Boutquin.Storage.Domain,
Boutquin.Storage.Infrastructure and Boutquin.Storage.SourceGenerator. Upgrade to
that release when reporting an issue. The 1.x persisted format requires the
[documented export and re-ingest migration](docs/migration-1x-to-2x.md).

## Reporting a vulnerability

Report suspected vulnerabilities privately through
[GitHub private vulnerability reporting](https://github.com/boutquin/Boutquin.Storage/security/advisories/new).
Do not disclose exploit details, credentials or sensitive data in a public issue.

Include the affected package and version, operating system and .NET SDK,
reproduction steps, the expected security boundary, and the observed impact.
Use a minimal synthetic input where possible. Preserve original storage files
and reproduce against copies.

The maintainer will assess the report and coordinate any fix and disclosure
through the private advisory. Response and remediation times depend on the
report and maintainer availability.

The library's durability, corruption-detection and deployment limits are
specified in the [documentation](README.md#features). A supported configuration
that violates a documented security boundary is appropriate for a private
report; ordinary feature requests belong in public issues.
