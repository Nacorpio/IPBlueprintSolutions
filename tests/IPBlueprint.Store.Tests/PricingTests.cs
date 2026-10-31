using IPBlueprint.Store.Pages.Admin.Products;
using IPBlueprint.Store.Shop;

namespace IPBlueprint.Store.Tests;

public class PricingTests
{
    private static readonly ShopOptions Options = new() { ShippingFlatRate = 69, FreeShippingFrom = 600 };

    [Theory]
    [InlineData(0, 0)]
    [InlineData(129, 69)]
    [InlineData(599.99, 69)]
    [InlineData(600, 0)]
    [InlineData(1200, 0)]
    public void Shipping_is_flat_rate_until_the_free_shipping_threshold(decimal subtotal, decimal expected) =>
        Assert.Equal(expected, Pricing.ShippingFor(subtotal, Options));

    [Fact]
    public void Free_shipping_can_be_turned_off() =>
        Assert.Equal(69, Pricing.ShippingFor(5000, new ShopOptions { ShippingFlatRate = 69, FreeShippingFrom = 0 }));

    [Theory]
    [InlineData(349, "349 kr")]
    [InlineData(1249, "1 249 kr")]
    [InlineData(99.5, "99,50 kr")]
    public void Money_is_formatted_the_Swedish_way(decimal amount, string expected) =>
        Assert.Equal(expected, Money.Format(amount).Replace(' ', ' '));

    [Theory]
    [InlineData("Drakfigur – Stor!", "drakfigur-stor")]
    [InlineData("Blomkruka i grön", "blomkruka-i-gron")]
    [InlineData("  Ålands Ö  ", "alands-o")]
    public void Slugs_are_url_friendly(string name, string expected) =>
        Assert.Equal(expected, EditModel.Slugify(name));

    [Theory]
    [InlineData("IPB-261031-7KQ2", "IPB-261031-7KQ2")]
    [InlineData("IPB-261031-7KQ2-2", "IPB-261031-7KQ2")]
    [InlineData("IPB-261031-7KQ2-13", "IPB-261031-7KQ2")]
    public void Retry_suffix_is_stripped_from_checkout_references(string checkoutRef, string orderRef) =>
        Assert.Equal(orderRef, OrderService.BaseReference(checkoutRef));
}
