; Analyzer release tracking. Roslyn requires it (RS2008) so that a diagnostic id cannot be
; added, removed or have its severity changed without the change being visible in a diff.
; Ids are as public as the REST surface: a consumer will put them in <NoWarn>.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
CDT001 | Concordat | Error | The subject name does not match the ADR-011 grammar.
CDT002 | Concordat | Warning | A member has no JSON Schema mapping and will be unconstrained.
CDT003 | Concordat | Error | The checked-in contract has drifted from the type.
CDT004 | Concordat | Warning | The type has no checked-in contract, so nothing is being checked.
CDT005 | Concordat | Error | Two types declare the same subject.
