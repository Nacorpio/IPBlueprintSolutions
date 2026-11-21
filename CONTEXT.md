# IP Blueprint Solutions store

A small web shop where two makers in Smedjebacken sell their own 3D prints and take custom orders, paid through SumUp.

## Language

### Orders and payment

**Order**:
What a customer buys in one checkout, reached by its private order link.
_Avoid_: Purchase, transaction

**Payment attempt**:
One SumUp checkout created for an order, each time the customer starts paying.
_Avoid_: Retry, checkout (on its own)

**Payment issue**:
A SumUp checkout that was paid but could not simply settle its order, kept until the owners mark it resolved.
_Avoid_: Extra payment, stray payment

**Double charge**:
A payment issue where an order that was already paid was paid again, so one payment needs refunding.
_Avoid_: Extra payment, duplicate payment

**Wrong-amount payment**:
A payment issue where SumUp reports a paid checkout whose amount or currency differs from the order total.
_Avoid_: Amount mismatch

**Payment on a cancelled order**:
A payment issue where a checkout was paid after the owners cancelled its order.
_Avoid_: Late payment, extra payment

**Resolved**:
A payment issue the owners have dealt with outside the shop (refunded, re-opened the order, or accepted), optionally with a note.
_Avoid_: Closed, handled

**Abandoned order**:
An order that was never paid, had no payment attempt (or was not created) in the last 30 days, and has no payment issue resolved less than 30 days ago or still open.
_Avoid_: Stale order, expired order

### Stock

**Back-in-stock alert**:
A visitor's request to be emailed once when a sold-out product can be bought again.
_Avoid_: Notify-me, subscription, waitlist
