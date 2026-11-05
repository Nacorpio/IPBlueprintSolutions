---
paths:
  - "**/*.cs"
  - "**/*.cshtml"
  - "**/*.csproj"
---

<!-- Adapted from rules/csharp in Everything Claude Code (MIT, see .claude/LICENSE-everything-claude-code.txt). -->

# C# in this repo

## Style
- Follow `.editorconfig`; the format hook fixes whitespace in each C# file Claude edits.
- File-scoped namespaces, explicit access modifiers, one main type per file.
- `sealed` by default. `record` / `record struct` for immutable value types, `class` for entities.
- Primary constructors for services and page models that only store their dependencies.
- No `dynamic`. No `async void` outside event handlers. No `.Result` or `.Wait()`.
- Pass `CancellationToken` from the page handler down to EF Core and `HttpClient`.
- Money is `decimal`, always.

## Security
- Secrets come from configuration (user-secrets locally, environment variables on the server), never source or
  `appsettings*.json`. The stop hook rejects a value typed into a `Password`, `ApiKey` or `MerchantCode` key.
- EF Core LINQ or `FromSql`/`ExecuteSql` only; never build SQL strings from input.
- Validate with data annotations and check `ModelState.IsValid` before saving or sending anything.
- Return safe messages to visitors; log details server-side without secrets or personal data.

## Tests
- xUnit, through `StoreFactory` (`WebApplicationFactory<Program>`) with the fake SumUp and email sender.
- Test behaviour through HTTP where possible. Name tests after what they prove, e.g.
  `Duplicate_webhook_takes_stock_only_once`.
- A bug fix comes with a test that fails without the fix.
- Run `dotnet test` before committing.
