# Emails go through an in-memory background queue

Pages hand emails to `BackgroundWorkQueue` instead of sending them inline, so a slow or broken mail server (Gmail limits around 100 an hour) never holds up checkout, the SumUp return page or an admin saving a product. The queue lives in memory and is drained on a normal shutdown (up to a minute), so a deploy doesn't drop anything, but a crash loses whatever hadn't been sent. We chose that over an outbox table because crashes are rare for a shop this size and the status history only says "customer emailed" once a send actually worked. Revisit with an outbox table if lost emails ever show up.

## Considered options

- **Outbox table**: survives a crash, but needs another migration, a retry policy and cleanup.
- **Inline with a shorter timeout**: still makes the visitor wait, and still blocks the admin's save for every back-in-stock address.

## Consequences

- Back-in-stock alerts stay durable anyway: the queue only holds "this product is back", and the worker deletes each address after its email goes out. A startup sweep picks up any list a restart cut short.
- Tests wait for the queue to empty before looking at sent emails (`CapturingEmailSender.Sent`).
