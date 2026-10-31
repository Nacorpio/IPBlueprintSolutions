# IP Blueprint Solutions web store

A small web shop for 3D printed items, built with ASP.NET Core on .NET 10 (Razor Pages, EF Core with SQLite).
Payments are made through SumUp Hosted Checkout, so card details never reach this site.

## What it does

- **Shop:** a product catalogue with photos, product pages, and a cart stored in a cookie.
- **Checkout:** the customer enters delivery details and is sent to SumUp's payment page. They then return to an order page that confirms the payment.
- **Payment confirmation:** SumUp calls `POST /webhooks/sumup` when a payment changes. The webhook isn't signed, so the shop always asks the SumUp API for the real status before it marks an order paid. It also checks that the amount and currency match the order. The customer's return page does the same check, in case the webhook is slow.
- **Custom orders:** a request form with an optional file (images, PDF, STL/3MF/OBJ/STEP, up to 25 MB). You get an email, then quote by hand and send a SumUp payment link.
- **Emails:** an order confirmation to the customer, a new-order email to you, a shipping email with an optional tracking number, and custom request emails. If SMTP isn't set up, emails are written to the log instead.
- **Admin (`/Admin`):** an overview, an order list (mark orders shipped, cancel unpaid ones), a product editor with photo upload, and custom requests (status, notes, attachment download). It is protected by one shared password and rate-limited.
- **Information pages:** terms of purchase, returns and right of withdrawal, privacy policy, and about. The legal pages are **drafts** that must be completed before launch.

## Running locally

```bash
dotnet user-secrets --project src/IPBlueprint.Store set "Admin:Password" "a-long-password-here"
dotnet run --project src/IPBlueprint.Store
```

In Development, three example products are added to an empty database. Without SumUp keys, the shop works up to checkout, and the admin overview shows a warning.

```bash
dotnet test
```

The tests run the whole app against a temporary database, with SumUp and email replaced by fakes. They cover pricing and the payment flow: webhooks, duplicate webhooks, amount mismatch, retry after a failed payment, and the return page.

## Configuration

All settings are in `appsettings.json`. Secrets belong in user-secrets locally and in environment variables on the server, such as `SumUp__ApiKey`.

| Setting | What it is |
| --- | --- |
| `Shop:PublicBaseUrl` | The public address, e.g. `https://www.example.se`. Used for the URLs sent to SumUp. |
| `Shop:ContactEmail` | Shown to customers and used as reply-to. |
| `Shop:NotificationEmails` | Where new orders and requests are emailed (comma-separated). |
| `Shop:ShippingFlatRate`, `Shop:FreeShippingFrom` | Shipping cost in kr, and the order value from which shipping is free (0 = never free). |
| `SumUp:ApiKey`, `SumUp:MerchantCode` | From the SumUp dashboard (Developers → API keys). **Secret.** |
| `Smtp:*` | Outgoing mail server. Your web host usually provides one. |
| `Admin:Password` | Admin password, at least 12 characters. **Secret.** |
| `Storage:DataPath` | Folder for the database, product photos and uploads (default `App_Data`). **Back this folder up.** |
| `Hosting:BehindReverseProxy` | Set to `true` when running behind nginx/Caddy, so the real client IP and HTTPS are seen. |

## Before going live

1. Create the SumUp API key and merchant code. The hosted checkout flag was verified against the live API on 2026-10-31: `hosted_checkout: { enabled: true }` (what `Payments/SumUpClient.cs` sends) returns a `hosted_checkout_url`. `hosted_checkout_settings` is also accepted, and leaving the flag out returns no URL, so the flag is required.
2. Make sure the server is reachable over HTTPS so SumUp can call the webhook.
3. Finish the legal pages in `Pages/Info/`: company details, delivery times, and who pays return postage.
4. Set up SMTP and send yourself a test order.

## Hosting

The app needs a host that can run ASP.NET Core 10. Ordinary PHP web hosting (webbhotell) usually can't. Options:

- A small Linux VPS running `dotnet IPBlueprint.Store.dll` behind Caddy or nginx. Caddy gets HTTPS certificates automatically.
- Azure App Service (Linux). Point `Storage:DataPath` to persistent storage such as `/home/data`.

The domain can stay with your current provider. Only its DNS needs to point to the server.
