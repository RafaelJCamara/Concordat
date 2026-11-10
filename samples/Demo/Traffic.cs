namespace Demo;

/// <summary>
/// The messages the shop sends. Mostly right; occasionally wrong in a way that is stable from run to run,
/// so the dashboard has a handful of distinct violations rather than a different pile each time.
/// </summary>
/// <remarks>
/// The registry keeps one row per side, route, subject and code, with the most recent detail and a count, so
/// however many bad messages there are the dashboard shows a handful of violations. The faults are fixed and
/// repeat on a schedule so that what you see on screen matches what the docs say you will see.
/// </remarks>
internal static class Traffic
{
    private static readonly string[] Customers = ["ada", "grace", "edsger", "barbara", "alan", "margaret"];

    /// <summary>Every sixth order is wrong, the first at round 3 so a refusal shows up in seconds.</summary>
    /// <param name="n">Which round this is.</param>
    /// <param name="currency">
    /// Sent when set. Version 3 of the contract, the one held at the approval gate, requires it; until it is
    /// approved nobody needs it, and after, only a checkout that sends it gets orders through.
    /// </param>
    public static Dictionary<string, object?> Order(int n, string? currency)
    {
        var order = new Dictionary<string, object?>
        {
            ["orderId"] = $"ord-{n:0000}",
            ["customer"] = Customers[n % Customers.Length],
        };

        var total = Math.Round(10 + (n * 7.35 % 90), 2);

        switch (n % 12)
        {
            // The total is text where the contract says number.
            case 3:
                order["total"] = "free";
                break;

            // No total at all, which the contract requires. Alternates with the fault above.
            case 9:
                break;

            default:
                order["total"] = total;

                // Every fourth one carries the optional coupon version 2 added.
                if (n % 4 == 0)
                {
                    order["coupon"] = "WELCOME10";
                }

                break;
        }

        if (currency is not null)
        {
            order["currency"] = currency;
        }

        return order;
    }

    /// <summary>Every fourth payment is wrong, the first at round 2.</summary>
    public static object Payment(int n)
    {
        var id = $"pay-{n:0000}";
        var orderId = $"ord-{n:0000}";
        var amount = Math.Round(10 + (n * 7.35 % 90), 2);

        return (n % 4) switch
        {
            // A negative amount; the contract's minimum is zero.
            2 => new { paymentId = id, orderId, amount = -amount, currency = "EUR" },

            _ => new { paymentId = id, orderId, amount, currency = "EUR" },
        };
    }
}
