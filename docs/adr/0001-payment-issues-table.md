# Payment issues get their own table

A paid SumUp checkout that can't settle its order (a double charge, a wrong-amount payment, or a payment on a cancelled order) is recorded as a row in `PaymentIssues`, one per checkout, with its kind and a resolved flag. It replaced the single `Order.ExtraPaymentCheckoutId` column, which mixed two meanings told apart only by the order's status and lost the first stray checkout when a second one arrived. The table also lets the owners mark an issue resolved, which is what lets a customer pay again after a wrong-amount payment, and lets data retention know when a kept order can finally go.

## Consequences

- The migration copies old values across (kind guessed from the order's status, amount unknown) and drops the column.
- "Can the customer pay now?" checks for an open issue inside the same conditional update that counts the payment attempt, so a mismatch recorded mid-request still blocks a new checkout.
