using Quotalis.Core.Models;
using Quotalis.Core.Pricing;

namespace Quotalis.Tests;

public class CostCalculationTests
{
    private static readonly ModelPricingService UsdPricing = new(new PricingOverrides());

    [Fact]
    public void Prices_input_and_output_per_million_tokens()
    {
        // Opus 5.5: $4 input, $20 output per MTok.
        var estimate = UsdPricing.Estimate("claude-opus-5-5", new TokenCounts(1_000_000, 500_000, 0, 0, 0));

        Assert.True(estimate.IsPriced);
        Assert.Equal(4m + 10m, estimate.ValueUsd);
    }

    [Fact]
    public void Prices_cache_writes_and_reads_with_standard_multipliers()
    {
        // Sonnet 5: input $2 -> 5m write $2.50, 1h write $4, read $0.20.
        var estimate = UsdPricing.Estimate("claude-sonnet-5", new TokenCounts(0, 0, 1_000_000, 1_000_000, 1_000_000));

        Assert.Equal(2.5m + 4m + 0.2m, estimate.ValueUsd);
    }

    [Fact]
    public void Uses_model_specific_cache_read_price()
    {
        // Fable 5.1 publishes $0.25 cache reads instead of 10% of $10.
        var estimate = UsdPricing.Estimate("claude-fable-5-1", new TokenCounts(0, 0, 0, 0, 1_000_000));

        Assert.Equal(0.25m, estimate.ValueUsd);
    }

    [Theory]
    [InlineData("claude-haiku-4-5-20251001", 1)]
    [InlineData("claude-sonnet-4-20250514", 3)]
    [InlineData("claude-opus-4-1-20250805", 15)]
    [InlineData("gpt-5-2025-08-07", 1.25)]
    [InlineData("anthropic.claude-sonnet-4-5-20250929-v1:0", 3)]
    [InlineData("CLAUDE-OPUS-5", 5)]
    [InlineData("claude-sonnet-5-5", 2)]
    [InlineData("claude-haiku-5-5", 0.10)]
    [InlineData("claude-opus-5-5", 4)]
    [InlineData("claude-fable-5-1", 10)]
    [InlineData("gpt-6.1-sol", 2)]
    [InlineData("gpt-6-luna", 0.10)]
    [InlineData("gpt-5.6-sol", 4)]
    [InlineData("gpt-5.6-terra", 2)]
    public void Resolves_dated_and_decorated_model_ids(string model, double inputPrice)
    {
        var price = UsdPricing.FindPrice(model);

        Assert.NotNull(price);
        Assert.Equal((decimal)inputPrice, price.Input);
    }

    [Theory]
    [InlineData("gpt-6-astra")]
    [InlineData("claude-opus-5-7")]
    [InlineData("gpt-5.2")]
    [InlineData("codex-auto-review")] // Codex's review model has no published list price
    [InlineData("")]
    public void Unknown_models_stay_unpriced_instead_of_guessed(string model)
    {
        var estimate = UsdPricing.Estimate(model, new TokenCounts(1000, 1000, 0, 0, 0));

        Assert.False(estimate.IsPriced);
        Assert.Equal(0m, estimate.ValueUsd);
    }

    [Fact]
    public void Prices_codex_models_with_their_own_cached_input_rates()
    {
        // GPT-6.1 Sol: $2 input, $0.10 cached input (5 %, not 10 %), $10 output per MTok.
        var estimate = UsdPricing.Estimate("gpt-6.1-sol", new TokenCounts(1_000_000, 1_000_000, 0, 0, 10_000_000));

        Assert.Equal(2m + 10m + 1m, estimate.ValueUsd);
    }

    [Fact]
    public void Overrides_take_precedence_over_built_in_prices()
    {
        var pricing = new ModelPricingService(new PricingOverrides
        {
            Models = { ["gpt-6-astra"] = new ModelPrice { Input = 2m, Output = 8m } },
        });

        Assert.Equal(10m, pricing.Estimate("gpt-6-astra", new TokenCounts(1_000_000, 1_000_000, 0, 0, 0)).ValueUsd);
    }

    [Fact]
    public void Broken_pricing_file_falls_back_to_built_in_prices()
    {
        var path = Path.Combine(TestData.TempDirectory(), "pricing.json");
        File.WriteAllText(path, "{ not json");

        var pricing = ModelPricingService.Load(path);

        Assert.True(pricing.IsKnownModel("claude-opus-5-5"));
    }

    [Fact]
    public void Zero_tokens_cost_nothing_even_for_unknown_models()
    {
        Assert.Equal(new PriceEstimate(0m, true), UsdPricing.Estimate("mystery", TokenCounts.Zero));
    }
}
