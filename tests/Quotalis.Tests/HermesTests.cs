using System.Net;
using Quotalis.Core.Hermes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Quotalis.Tests;

/// <summary>Hermes usage and the Nous wallet. Every account, amount, model and id here is made up.</summary>
public class HermesTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 22, 0, 0, TimeSpan.Zero);

    // 2026-10-24 21:00 and 2026-10-23 12:00 UTC.
    private const double Today = 1792875600;
    private const double Yesterday = 1792756800;

    private static readonly string Export = $$"""
        { "format": 1, "generated_at": {{Today}}, "schema_version": 32, "gateway_last_heartbeat": {{Today}},
          "last_activity_at": {{Today}}, "current_model": { "model": "example/model-b", "last_seen": {{Today}} },
          "sessions": [ { "id": "aaaaaaaaaaaa", "source": "cron" } ],
          "model_usage": [
            { "session": "aaaaaaaaaaaa", "model": "example/model-a", "task": "", "api_call_count": 10, "input_tokens": 1000,
              "output_tokens": 200, "cache_read_tokens": 5000, "cache_write_tokens": 0, "reasoning_tokens": 50,
              "estimated_cost_usd": 0.10, "actual_cost_usd": 0, "first_seen": {{Yesterday}}, "last_seen": {{Yesterday}} },
            { "session": "bbbbbbbbbbbb", "model": "example/model-b", "task": "approval", "api_call_count": 2, "input_tokens": 300,
              "output_tokens": 40, "cache_read_tokens": 0, "cache_write_tokens": 0, "reasoning_tokens": 0,
              "estimated_cost_usd": 0.02, "actual_cost_usd": 0, "first_seen": {{Today}}, "last_seen": {{Today}} },
            { "session": "bbbbbbbbbbbb", "model": "example/model-b", "task": "", "api_call_count": 5, "input_tokens": 700,
              "output_tokens": 60, "cache_read_tokens": 900, "cache_write_tokens": 0, "reasoning_tokens": 10,
              "estimated_cost_usd": "0.05", "actual_cost_usd": null, "first_seen": {{Today}}, "last_seen": {{Today}} } ],
          "tools": [] }
        """;

    [Fact]
    public void Export_rows_are_grouped_by_the_day_they_were_last_used_including_helper_calls()
    {
        var usage = HermesUsageParser.Parse(Export, Utc)!;

        Assert.Equal(32, usage.SchemaVersion);
        Assert.Equal("example/model-b", usage.CurrentModel);
        Assert.Equal(2, usage.Days.Count);
        var today = usage.DayOf(new DateOnly(2026, 10, 24))!;
        Assert.Equal(1000, today.Tokens.Input);       // the approval helper call counts too
        Assert.Equal(100, today.Tokens.Output);
        Assert.Equal(900, today.Tokens.CacheRead);
        Assert.Equal(7, today.ApiCalls);
        Assert.Equal(1, today.Sessions);
        Assert.Equal(0.07m, today.CostUsd);
        Assert.True(today.CostEstimated);
        Assert.Equal(0.10m, usage.DayOf(new DateOnly(2026, 10, 23))!.CostUsd);
    }

    [Theory]
    [InlineData("""{ "format": 2, "generated_at": 1792875600 }""")]
    [InlineData("""{ "generated_at": 1792875600 }""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    public void Unknown_exports_are_refused_not_guessed(string json) => Assert.Null(HermesUsageParser.Parse(json, Utc));

    [Fact]
    public void Actual_cost_wins_over_the_estimate_when_Nous_reported_one()
    {
        var rows = new[]
        {
            new HermesModelRow("s", "m", "", 1, new(10, 1, 0, 0, 0), 0, 0.50m, 0.42m, Now),
        };
        var day = HermesUsageParser.Aggregate(rows, Utc).Single();
        Assert.Equal(0.42m, day.CostUsd);
        Assert.False(day.CostEstimated);
    }

    [Fact]
    public void A_local_state_db_is_read_read_only_into_the_same_shape()
    {
        var path = Path.Combine(Path.GetTempPath(), "quotalis-hermes-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"""
                    CREATE TABLE schema_version (version INTEGER);
                    INSERT INTO schema_version VALUES (32);
                    CREATE TABLE sessions (id TEXT, last_activity_at REAL, title TEXT, system_prompt TEXT);
                    INSERT INTO sessions VALUES ('s1', {Today}, 'PRIVATE TITLE', 'PRIVATE PROMPT');
                    CREATE TABLE gateway_heartbeats (last_heartbeat REAL);
                    CREATE TABLE messages (session_id TEXT, content TEXT);
                    INSERT INTO messages VALUES ('s1', 'PRIVATE MESSAGE');
                    CREATE TABLE session_model_usage (session_id TEXT, model TEXT, billing_provider TEXT, task TEXT, api_call_count INTEGER,
                        input_tokens INTEGER, output_tokens INTEGER, cache_read_tokens INTEGER, cache_write_tokens INTEGER, reasoning_tokens INTEGER,
                        estimated_cost_usd REAL, actual_cost_usd REAL, first_seen REAL, last_seen REAL);
                    INSERT INTO session_model_usage VALUES ('s1', 'example/model-a', 'nous', '', 3, 100, 20, 400, 0, 5, 0.01, 0, {Today}, {Today});
                    """;
                cmd.ExecuteNonQuery();
            }

            var usage = HermesStateDb.Read(path, Utc, Now)!;
            Assert.Equal(32, usage.SchemaVersion);
            Assert.Equal("example/model-a", usage.CurrentModel);
            Assert.Equal(new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.Zero), usage.LastActivity);
            Assert.Null(usage.GatewayHeartbeat);
            Assert.Equal(120, usage.Days.Single().Tokens.Fresh);
            Assert.DoesNotContain("PRIVATE", System.Text.Json.JsonSerializer.Serialize(usage));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_local_database_reads_as_nothing() =>
        Assert.Null(HermesStateDb.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.db"), Utc, Now));

    // The wallet ------------------------------------------------------------------------------------------

    [Fact]
    public void Wallet_uses_total_usable_credits_and_keeps_the_breakdown()
    {
        var reading = NousWalletParser.Parse("""
            { "user": { "email": "someone@example.com" }, "organisation": { "id": "org-made-up" },
              "subscription": { "plan": "Plus", "monthly_credits": 20, "credits_remaining": "6.25", "current_period_end": "2026-11-20T00:00:00Z" },
              "purchased_credits_remaining": 12.5,
              "paid_service_access": { "subscription_credits_remaining": 6.25, "purchased_credits_remaining": 12.5, "total_usable_credits": 18.75 } }
            """, Now)!;

        Assert.Equal(18.75m, reading.TotalUsd);
        Assert.Equal(6.25m, reading.SubscriptionRemainingUsd);
        Assert.Equal(12.5m, reading.PurchasedRemainingUsd);
        Assert.Equal(20m, reading.MonthlyCreditsUsd);
        Assert.Equal("Plus", reading.PlanName);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), reading.PeriodEnd);
        Assert.DoesNotContain("example.com", reading.ToString());
    }

    [Fact]
    public void Wallet_adds_the_parts_when_the_total_is_missing() =>
        Assert.Equal(7.5m, NousWalletParser.Parse("""{ "subscription": { "credits_remaining": 2.5 }, "purchased_credits_remaining": 5 }""", Now)!.TotalUsd);

    [Theory]
    [InlineData("""{ "user": {} }""")]
    [InlineData("""{ "error": "unauthorized" }""")]
    [InlineData("""[1,2]""")]
    public void A_wallet_answer_without_numbers_is_not_a_reading(string json) => Assert.Null(NousWalletParser.Parse(json, Now));

    [Fact]
    public void The_bar_starts_full_drains_and_refills_on_a_top_up()
    {
        var gauge = WalletGauge.Next(null, 20m);
        Assert.Equal(1, gauge.Fraction);

        gauge = WalletGauge.Next(gauge, 15m);
        Assert.Equal(20m, gauge.FullUsd);
        Assert.Equal(0.75, gauge.Fraction, 3);

        gauge = WalletGauge.Next(gauge, 15.005m);   // rounding, not a top-up
        Assert.Equal(20m, gauge.FullUsd);

        gauge = WalletGauge.Next(gauge, 40m);       // top-up: the new full
        Assert.Equal(40m, gauge.FullUsd);
        Assert.Equal(1, gauge.Fraction);

        Assert.Equal(0, WalletGauge.Next(gauge, -3m).Fraction);
    }

    [Theory]
    [InlineData(10, 5, WalletLevel.Healthy)]
    [InlineData(4.99, 5, WalletLevel.Low)]
    [InlineData(0, 5, WalletLevel.Empty)]
    [InlineData(1, 0, WalletLevel.Healthy)]
    public void Wallet_levels(double usd, double low, WalletLevel expected) =>
        Assert.Equal(expected, WalletGauge.Level((decimal)usd, (decimal)low));

    // The polling rule ----------------------------------------------------------------------------------

    [Fact]
    public void Reads_every_second_while_Hermes_works_then_every_five_minutes_after_five_quiet_minutes()
    {
        var s = new NousPollSchedule();
        Assert.Equal(Now, s.Next(Now));                       // first read at once

        s.Wake(Now);
        s.OnSuccess(Now, balanceChanged: false);
        Assert.Equal(Now.AddSeconds(1), s.Next(Now));

        var later = Now.AddMinutes(4);
        s.OnSuccess(later, balanceChanged: false);
        Assert.Equal(later.AddSeconds(1), s.Next(later));      // still within five minutes of activity

        var quiet = Now.AddMinutes(5).AddSeconds(1);
        s.OnSuccess(quiet, balanceChanged: false);
        Assert.Equal(quiet.AddMinutes(5), s.Next(quiet));      // fast timer stopped

        var spend = quiet.AddMinutes(5);
        s.OnSuccess(spend, balanceChanged: true);             // the balance moved: fast again
        Assert.Equal(spend.AddSeconds(1), s.Next(spend));
    }

    [Fact]
    public void Locked_or_asleep_means_no_reads()
    {
        var s = new NousPollSchedule { Paused = true };
        s.Wake(Now);
        Assert.Null(s.Next(Now));
    }

    [Fact]
    public void Failures_back_off_and_honour_retry_after()
    {
        var s = new NousPollSchedule();
        s.Wake(Now);
        s.OnFailure(Now);
        Assert.Equal(Now.AddSeconds(5), s.Next(Now));
        s.OnFailure(Now);
        Assert.Equal(Now.AddSeconds(10), s.Next(Now));
        s.OnFailure(Now, TimeSpan.FromSeconds(60));
        Assert.Equal(Now.AddSeconds(60), s.Next(Now));
        for (var i = 0; i < 10; i++) s.OnFailure(Now);
        Assert.Equal(Now.Add(NousPollSchedule.MaxBackoff), s.Next(Now));
        s.OnSuccess(Now, false);
        Assert.Equal(Now.AddSeconds(1), s.Next(Now));
    }

    // The portal client --------------------------------------------------------------------------------

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return answer(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Device_sign_in_asks_for_no_scope_and_reads_the_code()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """
            { "device_code": "dev-made-up", "user_code": "ABCD-EFGH", "verification_uri": "https://portal.nousresearch.com/device",
              "verification_uri_complete": "https://portal.nousresearch.com/device?user_code=ABCD-EFGH", "expires_in": 600, "interval": 5 }
            """));
        var client = new NousPortalClient(new HttpClient(handler));

        var code = await client.RequestCodeAsync(Now, TestContext.Current.CancellationToken);

        Assert.Equal("ABCD-EFGH", code.UserCode);
        Assert.Equal(Now.AddMinutes(10), code.ExpiresAt);
        Assert.Equal("client_id=hermes-cli", handler.Bodies[0]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}""", NousPollResult.Pending)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"slow_down"}""", NousPollResult.SlowDown)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"access_denied"}""", NousPollResult.Denied)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"expired_token"}""", NousPollResult.Expired)]
    [InlineData(HttpStatusCode.TooManyRequests, "", NousPollResult.Pending)]
    [InlineData(HttpStatusCode.OK, """{"access_token":"a","refresh_token":"r","expires_in":3600}""", NousPollResult.Granted)]
    public async Task Token_polling_outcomes(HttpStatusCode status, string body, NousPollResult expected)
    {
        var client = new NousPortalClient(new HttpClient(new FakeHandler(_ => Json(status, body))));
        var code = new NousDeviceCode("d", "U", new Uri("https://portal.nousresearch.com/device"), TimeSpan.FromSeconds(5), Now.AddMinutes(10));
        var (result, tokens) = await client.PollAsync(code, Now, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result);
        Assert.Equal(expected == NousPollResult.Granted, tokens is not null);
    }

    [Fact]
    public async Task Refresh_sends_the_token_in_the_header_and_returns_the_rotated_one()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"access_token":"a2","refresh_token":"r2","expires_in":3600}"""));
        var client = new NousPortalClient(new HttpClient(handler));

        var (status, tokens, _) = await client.RefreshAsync("r1", Now, TestContext.Current.CancellationToken);

        Assert.Equal(NousReadStatus.Ok, status);
        Assert.Equal("r2", tokens!.RefreshToken);
        Assert.Equal(Now.AddHours(1), tokens.ExpiresAt);
        Assert.Equal("r1", handler.Requests[0].Headers.GetValues("x-nous-refresh-token").Single());
        Assert.DoesNotContain("r1", handler.Bodies[0]);
    }

    [Fact]
    public async Task Edge_firewall_challenges_keep_the_sign_in()
    {
        var challenge = Json(HttpStatusCode.Forbidden, "<html>checkpoint</html>");
        challenge.Headers.Add("x-vercel-mitigated", "challenge");
        var client = new NousPortalClient(new HttpClient(new FakeHandler(_ => challenge)));

        var (status, _, _) = await client.RefreshAsync("r1", Now, TestContext.Current.CancellationToken);

        Assert.Equal(NousReadStatus.Unavailable, status);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"refresh_token_reused"}""")]
    public async Task A_dead_refresh_token_ends_the_sign_in(HttpStatusCode status, string body)
    {
        var client = new NousPortalClient(new HttpClient(new FakeHandler(_ => Json(status, body))));
        Assert.Equal(NousReadStatus.SignInRejected, (await client.RefreshAsync("r1", Now, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Account_reads_carry_the_bearer_token_and_map_throttling()
    {
        var throttled = Json(HttpStatusCode.TooManyRequests, "");
        throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        var handler = new FakeHandler(r => r.Headers.Authorization?.Parameter == "good" ? Json(HttpStatusCode.OK, "{}") : throttled);
        var client = new NousPortalClient(new HttpClient(handler));

        Assert.Equal(NousReadStatus.Ok, (await client.ReadAccountAsync("good", TestContext.Current.CancellationToken)).Status);
        var slow = await client.ReadAccountAsync("other", TestContext.Current.CancellationToken);
        Assert.Equal(NousReadStatus.Throttled, slow.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), slow.RetryAfter);
        Assert.All(handler.Requests, r => Assert.Equal("/api/oauth/account", r.RequestUri!.AbsolutePath));
    }
}
