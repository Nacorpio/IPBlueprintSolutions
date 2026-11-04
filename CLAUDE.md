# IP Blueprint Solutions store

A small web shop for 3D printed items (PLA, PETG, ABS), run by two people in Smedjebacken, Sweden.
The README explains what the site does and how to configure it. Read it before changing behaviour.

## Stack

- ASP.NET Core on .NET 10, Razor Pages, EF Core with SQLite, MailKit, SkiaSharp (gallery resizing).
- Payments: SumUp Hosted Checkout. Card details never reach this site.
- No JavaScript framework and no CSS framework: plain CSS in `wwwroot/css`.
- Solution: `IPBlueprintSolutions.slnx`. App in `src/IPBlueprint.Store`, tests in `tests/IPBlueprint.Store.Tests`.

## Commands

```bash
dotnet build
dotnet test                                    # whole app against a temp SQLite db, SumUp and email faked
dotnet run --project src/IPBlueprint.Store     # needs Admin:Password (12+ chars) in user-secrets
dotnet ef migrations add <Name> --project src/IPBlueprint.Store --output-dir Data/Migrations
```

Run `dotnet test` before every commit. All tests pass on `main`, so keep it that way.

## Rules that are easy to break

- **Swedish first.** Short UI texts are written in English in code (`@L["Add to cart"]`) and translated in
  `Localization/SharedResource.sv.resx`. Add the Swedish entry in the same change: `LocalizationTests` fails otherwise.
  Long texts (info/legal pages, emails) keep both languages side by side in the same file. Admin pages are English only.
- **SumUp webhooks are not signed.** `/webhooks/sumup` must keep re-reading the checkout from the SumUp API and
  checking amount and currency before marking an order paid. Never trust the webhook body.
- **Stock is taken once, at payment.** Duplicate webhooks must not take stock twice. `StockAndStatusTests` covers this.
- **The order link is the key.** `/order/{id}` is a random Guid with no login. Never expose order ids in lists,
  sitemaps or logs that customers can see.
- **No street address on the site.** Pickup is "in Smedjebacken"; the address is agreed by email.
- **Secrets** (`Admin:Password`, `SumUp:ApiKey`, `SumUp:MerchantCode`, `Smtp:Password`) go in user-secrets locally
  and environment variables (`SumUp__ApiKey`) on the server. Never in `appsettings*.json`.
- **Schema changes need a migration** in `Data/Migrations`. The app runs `MigrateAsync()` at startup, so a migration
  ships to production on the next deploy. Don't edit a migration that is already on `main`.
- **Uploads** (product photos, gallery, custom-order files) are saved under `Storage:DataPath`. Keep the size and
  type checks, and never build a file path from user input.

## Code style

- File-scoped namespaces, nullable enabled, `sealed` classes and `record`s for value types, primary constructors.
- Pass `CancellationToken` through async page handlers and services.
- XML doc `<summary>` on public types, in plain language: say what it is for, not how it works.
- Tests are xUnit integration tests through `StoreFactory` (`WebApplicationFactory<Program>`). Test through HTTP
  where you can, and name tests after the behaviour.

## Claude Code setup

`.claude/` holds a C# reviewer and a security reviewer agent, C# rules, and two hooks (format each edited C# file,
and a build check when Claude stops). They are adapted from
[Everything Claude Code](https://github.com/affaan-m/everything-claude-code) (MIT, see
`.claude/LICENSE-everything-claude-code.txt`). The hooks are switched on by `.claude/settings.json`
(shipped as `.claude/settings.suggested.json` until it is renamed), and do nothing when `dotnet` isn't installed.
