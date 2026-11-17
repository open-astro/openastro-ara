### Fixed (Sequences)

- **Template instantiate 422.** Instantiating a template whose body fails the sequence schema check now answers 422 with problem details (`application/problem+json`, `detail` names the reason) instead of a bare `{"error": ...}` body, and no sequence is created. Only that refusal maps to 422; other argument errors no longer do (#1952).
