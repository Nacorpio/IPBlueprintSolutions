# IP Blueprint Solutions web store

A small web shop for 3D printed items, built with ASP.NET Core on .NET 10 (Razor Pages, EF Core with SQLite).
Payments are made through SumUp Hosted Checkout, so card details never reach this site.

## What it does

- **Shop:** a product catalogue with photos, product pages, and a cart stored in a cookie.
- **Checkout:** the customer enters delivery details and is sent to SumUp's payment page. They then return to an order page that confirms the payment.
- **Payment confirmation:** SumUp calls `POST /webhooks/sumup` when a payment changes. The webhook isn't signed, so the shop always asks the SumUp API for the real status before it marks an order paid. It also checks that the amount and currency match the order. The customer's return page does the same check, in case the webhook is slow.
- **Gallery (`/gallery`):** photos of things you've printed before, with optional captions in Swedish and English. Clicking a photo opens it larger. Manage it under Admin → Gallery: upload up to 10 photos at a time (JPG, PNG or WebP, 20 MB each), edit captions, move photos earlier or later, and delete them. Each upload is turned the right way up, resized to 2000 px plus an 800 px thumbnail, and saved as WebP without camera data such as GPS position. The resizing uses SkiaSharp (MIT licence).
- **Custom orders:** a request form with an optional file (images, PDF, STL/3MF/OBJ/STEP, up to 25 MB). You get an email, then quote by hand and send a SumUp payment link.
- **Emails:** an order confirmation to the customer, a new-order email to you, a shipping email with an optional tracking number, and custom request emails. If SMTP isn't set up, emails are written to the log instead.
- **Admin (`/Admin`):** an overview, an order list (mark orders shipped, cancel unpaid ones), a product editor with photo upload, and custom requests (status, notes, attachment download). It is protected by one shared password and rate-limited.
- **Information pages:** terms of purchase, returns and right of withdrawal, privacy policy, and about. The legal pages are **drafts** that must be completed before launch.

## Languages

The site is in **Swedish by default**. A button in the header switches to English (and back); the choice is kept in a cookie for a year. The browser's own language setting is ignored on purpose, so everyone starts in Swedish.

- **Short texts** (buttons, labels, form errors, flash messages) are written in English in the code, for example `@L["Add to cart"]`. The Swedish for each one is in `src/IPBlueprint.Store/Localization/SharedResource.sv.resx`. A test fails if a text on a public page has no Swedish entry, so a missing translation is caught before it ships.
- **Longer texts** (the information and legal pages, and the emails) have both languages side by side in the same file, so they're easy to keep in step.
- **Products** have a Swedish name and description, plus an optional English name and description in the admin product editor. English visitors see the Swedish text when the English fields are empty.
- **Emails** to customers go out in the language they used on the site (stored on the order or request). Emails to you are always in Swedish.
- The admin pages are in English only.

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
| `Shop:PublicBaseUrl` | The public address, e.g. `https://www.example.se`. Used for the URLs sent to SumUp, and for the canonical links, sitemap and link previews, so **set it before going live**. |
| `Shop:ContactEmail` | Shown to customers and used as reply-to. |
| `Shop:NotificationEmails` | Where new orders and requests are emailed (comma-separated). |
| `Shop:ShippingFlatRate`, `Shop:FreeShippingFrom` | Shipping cost in kr, and the order value from which shipping is free (0 = never free). |
| `Shop:InstagramUrl`, `Shop:FacebookUrl` | The shop's social media pages, shown as footer icons, a line on the gallery and About pages, and in the order confirmation email. Leave one empty to hide it. |
| `Analytics:*` | Optional cookie-free visitor statistics. Off when empty. See [Getting found](#getting-found). |
| `SumUp:ApiKey`, `SumUp:MerchantCode` | From the SumUp dashboard (Developers → API keys). **Secret.** |
| `Smtp:*` | Outgoing mail server. Your web host usually provides one. |
| `Admin:Password` | Admin password, at least 12 characters. **Secret.** |
| `Storage:DataPath` | Folder for the database, product and gallery photos, and uploads (default `App_Data`). **Back this folder up.** |
| `Hosting:BehindReverseProxy` | Set to `true` when running behind nginx/Caddy, so the real client IP and HTTPS are seen. |

## Before going live

1. Create the SumUp API key and merchant code. The hosted checkout flag was verified against the live API on 2026-10-31: `hosted_checkout: { enabled: true }` (what `Payments/SumUpClient.cs` sends) returns a `hosted_checkout_url`. `hosted_checkout_settings` is also accepted, and leaving the flag out returns no URL, so the flag is required.
2. Make sure the server is reachable over HTTPS so SumUp can call the webhook.
3. Finish the legal pages in `Pages/Info/`: company details, delivery times, and who pays return postage.
4. Set up SMTP and send yourself a test order.
5. Set `Shop:PublicBaseUrl`, then add the site to Google Search Console and Bing Webmaster Tools and submit `https://<your domain>/sitemap.xml`.

## Getting found

- Every page has a title, a description, a canonical link and link-preview tags (Open Graph), so shared links show the page's name, text and photo (products) or the logo (`wwwroot/img/share.png`).
- Each page has a Swedish address (`/gallery`) and an English one (`/gallery?lang=en`), linked with `hreflang` so Google shows the right one. The language switch links to these addresses too, and the choice is still remembered in the cookie.
- `/sitemap.xml` lists every public page and active product in both languages. `/robots.txt` points to it and keeps crawlers out of the admin pages. Cart, checkout, order and error pages say `noindex`.
- Product pages include structured data (schema.org `Product` with the price in SEK and the shipping cost), and the front page describes the store and links its social accounts.

### Visitor statistics

Statistics are off until configured. Both supported services count visits without cookies or personal data, so no cookie banner is needed, and the privacy policy gets a short paragraph about it automatically when it's turned on. They're never loaded on the admin or order pages.

- **Umami Cloud** (umami.is): create a site, then set `Analytics:Provider` to `Umami` and `Analytics:WebsiteId` to its id. If you host Umami yourself, also set `Analytics:ScriptUrl` to your server's `script.js`.
- **Plausible** (plausible.io, paid, EU-hosted): add the site, then set `Analytics:Provider` to `Plausible` and `Analytics:ScriptUrl` to the script address on the site's installation page (`https://plausible.io/js/pa-….js`).

## Hosting

The app needs a host that can run ASP.NET Core 10. Ordinary PHP web hosting (webbhotell) usually can't. Options:

- A small Linux VPS running `dotnet IPBlueprint.Store.dll` behind Caddy or nginx. Caddy gets HTTPS certificates automatically.
- Azure App Service (Linux). Point `Storage:DataPath` to persistent storage such as `/home/data`.

The domain can stay with your current provider. Only its DNS needs to point to the server.
