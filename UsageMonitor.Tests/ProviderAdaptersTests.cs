using UsageMonitor.Core;
using UsageMonitor.Core.Providers;
using UsageMonitor.Core.Providers.Claude;
using UsageMonitor.Core.Providers.Codex;
using UsageMonitor.Core.Providers.Antigravity;
using UsageMonitor.Core.Providers.OpenRouter;
using UsageMonitor.Core.Providers.Zai;

namespace UsageMonitor.Tests;

public sealed class ProviderAdaptersTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AntigravityCliScannerReadsResponseTokensFromConversationPayload()
    {
        var payload = Message(5,
            Message(1, FieldVarint(1, 1893499200)),
            Message(9, FieldVarint(9, 412)));

        Assert.True(AntigravityCliUsageScanner.TryReadUsage(payload, out var timestamp, out var tokens));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893499200), timestamp);
        Assert.Equal(412, tokens);
    }

    private static byte[] Message(params byte[][] fields) => fields.SelectMany(field => field).ToArray();

    private static byte[] Message(int field, params byte[][] children)
    {
        var data = children.SelectMany(child => child).ToArray();
        return [.. Varint((ulong)(field << 3 | 2)), .. Varint((ulong)data.Length), .. data];
    }

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var next = (byte)(value & 0x7f);
            value >>= 7;
            bytes.Add(value == 0 ? next : (byte)(next | 0x80));
        } while (value != 0);
        return bytes.ToArray();
    }

    private static byte[] FieldVarint(int field, ulong value) => [.. Varint((ulong)(field << 3)), .. Varint(value)];

    [Fact]
    public void CodexMapperReadsWindowsFixtureWithoutSecrets()
    {
        var body = File.ReadAllText(Fixture("codex_usage.json"));
        var result = CodexUsageMapper.Map(new ProviderHttpResponse(200, new Dictionary<string, string>(), body), Now);
        Assert.Equal("Pro", result.Plan);
        Assert.Equal(37, result.Lines.Single(x => x.Label == "Session").Used);
        Assert.Equal(2, result.Lines.Single(x => x.Label == "Rate Limit Resets").Values.Single().Number);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodexMapperClassifiesWindowsByDurationAndReadsSparkLimits()
    {
        const string body = """
            {
              "plan_type":"pro",
              "rate_limit":{
                "primary_window":{"used_percent":55,"reset_at":"2030-01-02T03:04:05Z","limit_window_seconds":604800}
              },
              "additional_rate_limits":[{
                "limit_name":"GPT-5.3-Codex-Spark",
                "rate_limit":{
                  "primary_window":{"used_percent":12,"reset_at":"2030-01-01T17:04:05Z","limit_window_seconds":18000},
                  "secondary_window":{"used_percent":4,"reset_at":"2030-01-08T03:04:05Z","limit_window_seconds":604800}
                }
              }]
            }
            """;

        var result = CodexUsageMapper.Map(new ProviderHttpResponse(200, new Dictionary<string, string>(), body), Now);

        Assert.Equal(55, result.Lines.Single(x => x.Label == "Weekly").Used);
        Assert.DoesNotContain(result.Lines, x => x.Label == "Session");
        Assert.Equal(12, result.Lines.Single(x => x.Label == "Spark").Used);
        Assert.Equal(4, result.Lines.Single(x => x.Label == "Spark Weekly").Used);
    }

    [Fact]
    public void ClaudeMapperReadsWindowsFixture()
    {
        var body = File.ReadAllText(Fixture("claude_usage.json"));
        var oauth = new ClaudeOAuth { SubscriptionType = "pro", RateLimitTier = "5x", Scopes = new[] { "user:profile" } };
        var result = ClaudeUsageMapper.Map(new ProviderHttpResponse(200, new Dictionary<string, string>(), body), oauth, Now);
        Assert.Equal("Pro 5x", result.Plan);
        Assert.Equal(21, result.Lines.Single(x => x.Label == "Session").Used);
        Assert.Equal(12.5, result.Lines.Single(x => x.Label == "Extra usage spent").Used);
    }

    [Fact]
    public void ClaudeMapperPreservesRetryAfterInRateLimitWarning()
    {
        var oauth = new ClaudeOAuth { SubscriptionType = "pro", Scopes = new[] { "user:profile" } };
        var result = ClaudeUsageMapper.Map(
            new ProviderHttpResponse(429, new Dictionary<string, string> { ["retry-after"] = "541" }, string.Empty),
            oauth,
            Now);

        Assert.Contains("Retry in ~10m", result.Warning, StringComparison.Ordinal);
        Assert.Contains("retry in ~10m", result.Lines.Single(x => x.Label == "Status").Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AntigravityMapperPoolsSummaryWindowsWithoutInventingMissingBuckets()
    {
        const string body = """
            {"response":{"groups":[{"buckets":[
              {"bucketId":"gemini-5h","remainingFraction":0.80,"resetTime":"2030-01-01T17:00:00Z"},
              {"bucketId":"gemini-weekly","remainingFraction":0.60,"resetTime":"2030-01-08T12:00:00Z"},
              {"bucketId":"3p-5h","remainingFraction":0.95,"resetTime":"2030-01-01T17:00:00Z"},
              {"bucketId":"unknown-future","remainingFraction":0.10}
            ]}]}}
            """;
        var lines = AntigravityUsageMapper.ParseQuotaSummary(body);
        Assert.NotNull(lines);
        Assert.Equal(new[] { "Session", "Weekly", "Claude" }, lines!.Select(x => x.Label));
        Assert.Equal(20, lines[0].Used);
        Assert.Equal(40, lines[1].Used);
        Assert.Equal(5, lines[2].Used);
    }

    [Fact]
    public void AntigravityFallbackSkipsModelsWithoutQuotaData()
    {
        const string models = "{\"models\":{\"gemini\":{\"displayName\":\"Gemini\"},\"claude\":{\"displayName\":\"Claude\",\"quotaInfo\":{\"remainingFraction\":0.75}}}}";
        var lines = AntigravityUsageMapper.ParseAvailableModels(models);
        Assert.Single(lines);
        Assert.Equal("Claude", lines[0].Label);
        Assert.Equal(25, lines[0].Used);

        const string buckets = "{\"buckets\":[{\"modelId\":\"gemini\"},{\"modelId\":\"claude\",\"remainingFraction\":0.9}]}";
        var fallback = AntigravityUsageMapper.ParseQuotaBuckets(buckets);
        Assert.Single(fallback);
        Assert.Equal("Claude", fallback[0].Label);
        Assert.Equal(10, fallback[0].Used);
    }

    [Fact]
    public void AntigravityAuthParserReadsGoKeyringAndGeminiCliShapes()
    {
        var json = "{\"token\":{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expiry\":\"2030-01-01T12:00:00Z\"},\"client_id\":\"fixture-client\",\"client_secret\":\"fixture-secret\"}";
        var wrapped = "go-keyring-base64:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.True(AntigravityAuthStore.TryParse(wrapped, "fixture", out var token));
        Assert.Equal("access", token.AccessToken);
        Assert.Equal("refresh", token.RefreshToken);
        Assert.Equal("fixture-client", token.ClientId);
        Assert.Equal("fixture-secret", token.ClientSecret);
        Assert.True(AntigravityAuthStore.TryParse("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expiry_date\":1893499200000}", "fixture", out var fileToken));
        Assert.Equal("access", fileToken.AccessToken);
    }

    [Fact]
    public async Task AntigravityProviderRefreshesAfterAnExpiredAccessToken()
    {
        var fs = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\profile\\.gemini\\oauth_creds.json"] =
                "{\"access_token\":\"expired-access\",\"refresh_token\":\"refresh-token\",\"expiry_date\":1,\"client_id\":\"fixture-client\",\"client_secret\":\"fixture-secret\"}"
        });
        var auth = new AntigravityAuthStore(fs, () => "C:\\profile", () => null);
        var summary = "{\"response\":{\"groups\":[{\"buckets\":[{\"bucketId\":\"gemini-5h\",\"remainingFraction\":0.8,\"resetTime\":\"2030-01-01T17:00:00Z\"}]}]}}";
        var http = new QueueHttpClient(new Dictionary<string, IEnumerable<ProviderHttpResponse>>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"] =
                [new(401, new Dictionary<string, string>(), "{}"), new(200, new Dictionary<string, string>(), summary)],
            ["https://oauth2.googleapis.com/token"] =
                [new(200, new Dictionary<string, string>(), "{\"access_token\":\"fresh-access\",\"expires_in\":3600}")],
            ["https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"] =
                [new(200, new Dictionary<string, string>(), "{\"paidTier\":{\"name\":\"Google AI Pro\"}}")]
        });

        var snapshot = await new AntigravityProvider(auth, http).RefreshAsync(new ProviderContext { Now = Now });

        Assert.Null(snapshot.ErrorCategory);
        Assert.Equal("Pro", snapshot.Plan);
        Assert.Equal(20, snapshot.GetLine("Session")!.Used);
        Assert.Contains("Bearer fresh-access", http.Authorizations);
    }

    [Fact]
    public async Task AntigravityLegacyQuotaRetriesWithoutProjectEnvelope()
    {
        var fs = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\profile\\.gemini\\oauth_creds.json"] =
                "{\"access_token\":\"access\",\"expiry_date\":4102444800000}"
        });
        var auth = new AntigravityAuthStore(fs, () => "C:\\profile", () => null);
        var http = new QueueHttpClient(new Dictionary<string, IEnumerable<ProviderHttpResponse>>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"] =
                [new(404, new Dictionary<string, string>(), "{}")],
            ["https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"] =
                [new(404, new Dictionary<string, string>(), "{}")],
            ["https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels"] =
                [new(404, new Dictionary<string, string>(), "{}")],
            ["https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels"] =
                [new(404, new Dictionary<string, string>(), "{}")],
            ["https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"] =
                [new(200, new Dictionary<string, string>(), "{\"cloudaicompanionProject\":\"fixture-project\",\"paidTier\":{\"name\":\"Google AI Pro\"}}")],
            ["https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota"] =
                [new(400, new Dictionary<string, string>(), "{}"), new(200, new Dictionary<string, string>(),
                    "{\"buckets\":[{\"modelId\":\"gemini-3-pro\",\"remainingFraction\":0.75}]}" )],
            ["https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota"] =
                [new(404, new Dictionary<string, string>(), "{}")]
        });

        var snapshot = await new AntigravityProvider(auth, http).RefreshAsync(new ProviderContext { Now = Now });

        Assert.Null(snapshot.ErrorCategory);
        Assert.Equal("Pro", snapshot.Plan);
        Assert.Equal(25, snapshot.GetLine("Session")!.Used);
    }

    [Fact]
    public void CodexScannerDeduplicatesRepeatedTokenEvents()
    {
        var fs = new FixtureFileSystem(new Dictionary<string, string> { ["C:\\fixture\\codex.jsonl"] = File.ReadAllText(Fixture("codex_session.jsonl")) });
        var history = new CodexLogUsageScanner(fs).Scan("C:\\fixture", Now.AddDays(-30));
        Assert.Single(history.Points);
        Assert.Equal(1300, history.Points[0].Tokens);
    }

    [Fact]
    public void CodexScannerSkipsUnchangedCumulativeSnapshotsEvenWhenLastUsageRepeats()
    {
        const string lines = """
            {"timestamp":"2030-01-01T11:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"output_tokens":20,"total_tokens":120},"total_token_usage":{"input_tokens":100,"output_tokens":20,"total_tokens":120}}}}
            {"timestamp":"2030-01-01T11:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"output_tokens":20,"total_tokens":120},"total_token_usage":{"input_tokens":100,"output_tokens":20,"total_tokens":120}}}}
            {"timestamp":"2030-01-01T11:00:02Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":50,"output_tokens":10,"total_tokens":60},"total_token_usage":{"input_tokens":150,"output_tokens":30,"total_tokens":180}}}}
            """;
        var fs = new FixtureFileSystem(new Dictionary<string, string> { ["C:\\fixture\\codex.jsonl"] = lines });

        var history = new CodexLogUsageScanner(fs).Scan("C:\\fixture", Now.AddDays(-30));

        Assert.Equal(180, history.Points.Single().Tokens);
    }

    [Fact]
    public void CodexScannerSuppressesChildReplayButSeedsItsCumulativeBaseline()
    {
        var creation = DateTimeOffset.Parse("2030-01-01T11:00:00Z").ToUnixTimeSeconds();
        var live = creation + 1;
        var lines = """
            {"timestamp":"2030-01-01T11:00:00Z","type":"session_meta","payload":{"forked_from_id":"parent"}}
            {"timestamp":"2030-01-01T11:00:00.100Z","type":"event_msg","payload":{"type":"task_started","started_at":__REPLAY__}}
            {"timestamp":"2030-01-01T11:00:00.200Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1000,"output_tokens":200,"total_tokens":1200}}}}
            {"timestamp":"2030-01-01T11:00:01Z","type":"event_msg","payload":{"type":"task_started","started_at":__LIVE__}}
            {"timestamp":"2030-01-01T11:00:01.100Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1100,"output_tokens":220,"total_tokens":1320}}}}
            """.Replace("__REPLAY__", (creation - 900).ToString(), StringComparison.Ordinal)
            .Replace("__LIVE__", live.ToString(), StringComparison.Ordinal);
        var fs = new FixtureFileSystem(new Dictionary<string, string> { ["C:\\fixture\\codex.jsonl"] = lines });

        var history = new CodexLogUsageScanner(fs).Scan("C:\\fixture", Now.AddDays(-30));

        Assert.Equal(120, history.Points.Single().Tokens);
    }

    [Fact]
    public void ClaudeScannerDeduplicatesAndSkipsMalformedLines()
    {
        var fs = new FixtureFileSystem(new Dictionary<string, string> { ["C:\\fixture\\claude.jsonl"] = File.ReadAllText(Fixture("claude_session.jsonl")) });
        var history = new ClaudeLogUsageScanner(fs).Scan("C:\\fixture", Now.AddDays(-30));
        Assert.Single(history.Points);
        Assert.Equal(1400, history.Points[0].Tokens);
    }

    [Fact]
    public void ClaudeScannerUsesStableMessageIdsAndIgnoresNonProjectJsonl()
    {
        var projectLine = "{\"timestamp\":\"2030-01-01T11:00:00Z\",\"requestId\":\"r1\",\"costUSD\":2.5,\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet\",\"usage\":{\"input_tokens\":800,\"cache_read_input_tokens\":200,\"output_tokens\":400}}}";
        var replay = projectLine.Replace("11:00:00", "11:00:01", StringComparison.Ordinal).Replace("r1", "r2", StringComparison.Ordinal);
        var fs = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\fixture\\projects\\session.jsonl"] = projectLine + "\n" + replay,
            ["C:\\fixture\\debug\\ignored.jsonl"] = projectLine
        });

        var history = new ClaudeLogUsageScanner(fs).Scan("C:\\fixture", Now.AddDays(-30));

        Assert.Single(history.Points);
        Assert.Equal(1400, history.Points[0].Tokens);
        Assert.Equal(2.5, history.Points[0].CostUsd, 3);
    }

    [Fact]
    public void ClaudeScannerCountsAdvisorIterationsAndRejectsForeignLogShapes()
    {
        const string valid = "{\"version\":\"1.0.24\",\"sessionId\":\"s1\",\"timestamp\":\"2030-01-01T11:00:00Z\",\"requestId\":\"r1\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50,\"iterations\":[{\"type\":\"advisor_message\",\"model\":\"claude-haiku\",\"input_tokens\":20,\"output_tokens\":10},{\"type\":\"ordinary\",\"input_tokens\":900,\"output_tokens\":900}]}}}";
        const string malformed = "{\"version\":\"unknown\",\"timestamp\":\"2030-01-01T11:00:01Z\",\"message\":{\"id\":\"foreign\",\"model\":\"claude-sonnet\",\"usage\":{\"input_tokens\":999999999,\"output_tokens\":999999999}}}";
        var fs = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\fixture\\projects\\session.jsonl"] = valid + "\n" + malformed
        });

        var history = new ClaudeLogUsageScanner(fs).Scan("C:\\fixture", Now.AddDays(-30));

        Assert.Equal(180, history.Points.Single().Tokens);
    }

    [Fact]
    public async Task ClaudeProviderDoesNotRefreshOrWriteClaudeCredentials()
    {
        const string credentials = "{\"claudeAiOauth\":{\"accessToken\":\"current\",\"refreshToken\":\"refresh\",\"expiresAt\":0,\"subscriptionType\":\"pro\",\"scopes\":[\"user:profile\"]}}";
        var fs = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\claude\\.credentials.json"] = credentials
        });
        var auth = new ClaudeAuthStore(fs, key => key == "CLAUDE_CONFIG_DIR" ? "C:\\claude" : null, () => "C:\\profile");
        var http = new SequenceHttpClient(
            ("https://api.anthropic.com/api/oauth/usage", new ProviderHttpResponse(200, new Dictionary<string, string>(), File.ReadAllText(Fixture("claude_usage.json")))));

        var snapshot = await new ClaudeProvider(auth, http, fs).RefreshAsync(new ProviderContext { Now = Now });

        Assert.Null(snapshot.ErrorCategory);
        Assert.Equal("Bearer current", http.Authorizations.Single());
        Assert.Null(http.Bodies.Single());
        Assert.Equal(credentials, fs.ReadAllText("C:\\claude\\.credentials.json"));
    }

    [Fact]
    public async Task ClaudeProviderReportsExpiredAccessTokenWithoutTryingRefresh()
    {
        var fs = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\claude\\.credentials.json"] = "{\"claudeAiOauth\":{\"accessToken\":\"expired\",\"refreshToken\":\"refresh\",\"expiresAt\":0,\"scopes\":[\"user:profile\"]}}"
        });
        var auth = new ClaudeAuthStore(fs, key => key == "CLAUDE_CONFIG_DIR" ? "C:\\claude" : null, () => "C:\\profile");
        var http = new SequenceHttpClient(
            ("https://api.anthropic.com/api/oauth/usage", new ProviderHttpResponse(401, new Dictionary<string, string>(), "{}")));

        var snapshot = await new ClaudeProvider(auth, http, fs).RefreshAsync(new ProviderContext { Now = Now });

        Assert.Equal(ProviderErrorCategory.Authentication, snapshot.ErrorCategory);
        Assert.Contains("auth login", snapshot.GetLine("Error")!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(http.Authorizations);
        Assert.Equal("Bearer expired", http.Authorizations.Single());
        Assert.Null(http.Bodies.Single());
    }

    [Fact]
    public void ClaudeMapperPreservesRetryAfterFromEitherSecondsOrHttpDate()
    {
        var seconds = ClaudeUsageMapper.ParseRetryAfterSeconds("120", Now);
        Assert.Equal(120, seconds);

        var httpDate = Now.AddMinutes(3).ToString("R");
        var fromDate = ClaudeUsageMapper.ParseRetryAfterSeconds(httpDate, Now);
        Assert.InRange(fromDate ?? -1, 179, 181);
    }

    [Fact]
    public async Task ClaudeProviderKeepsLastGoodLimitsWhenTheNextUsageRefreshIsRateLimited()
    {
        var fs = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\claude\\.credentials.json"] = "{\"claudeAiOauth\":{\"accessToken\":\"current\",\"expiresAt\":4102444800000,\"scopes\":[\"user:profile\"]}}"
        });
        var auth = new ClaudeAuthStore(fs, key => key == "CLAUDE_CONFIG_DIR" ? "C:\\claude" : null, () => "C:\\profile");
        var http = new QueueHttpClient(new Dictionary<string, IEnumerable<ProviderHttpResponse>>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://api.anthropic.com/api/oauth/usage"] =
            [
                new(200, new Dictionary<string, string>(), File.ReadAllText(Fixture("claude_usage.json"))),
                new(429, new Dictionary<string, string> { ["Retry-After"] = "1800" }, "{}")
            ]
        });
        var provider = new ClaudeProvider(auth, http, fs);

        var first = await provider.RefreshAsync(new ProviderContext { Now = Now });
        var second = await provider.RefreshAsync(new ProviderContext { Now = Now.AddMinutes(1) });

        Assert.Null(first.ErrorCategory);
        Assert.Null(second.ErrorCategory);
        Assert.Equal(21, second.GetLine("Session")!.Used);
        Assert.Contains("rate limited", second.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClaudeAuthStoreAcceptsAConfigValueThatPointsToTheCredentialFile()
    {
        var fs = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\claude\\.credentials.json"] = "{\"claudeAiOauth\":{\"accessToken\":\"current\",\"scopes\":[\"user:profile\"]}}"
        });
        var auth = new ClaudeAuthStore(
            fs,
            key => key == "CLAUDE_CONFIG_DIR" ? "C:\\claude\\.credentials.json" : null,
            () => "C:\\profile");

        var candidate = Assert.Single(auth.LoadCandidates());

        Assert.Equal("C:\\claude\\.credentials.json", candidate.Path);
        Assert.True(candidate.HasAccessToken);
    }

    [Fact]
    public async Task CodexProviderUsesInjectedHttpClient()
    {
        var fs = new FixtureFileSystem(new Dictionary<string, string> { ["C:\\fixture\\auth.json"] = "{\"tokens\":{\"access_token\":\"fixture-access\"}}" });
        var auth = new CodexAuthStore(fs, key => key == "CODEX_HOME" ? "C:\\fixture" : null, () => "C:\\profile");
        var http = new FakeHttpClient(new ProviderHttpResponse(200, new Dictionary<string, string>(), "{\"plan_type\":\"pro\",\"rate_limit\":{\"primary_window\":{\"used_percent\":1}}}"));
        var snapshot = await new CodexProvider(auth, http, fs).RefreshAsync(new ProviderContext { Now = Now });
        Assert.Equal("codex", snapshot.ProviderId);
        Assert.Equal(1, snapshot.GetLine("Session")!.Used);
        Assert.Equal("Bearer fixture-access", http.Authorization);
    }

    [Fact]
    public void OpenRouterMapperReadsSanitizedCreditsFixture()
    {
        var body = File.ReadAllText(Fixture("openrouter_credits.json"));
        var result = OpenRouterUsageMapper.Map(
            new ProviderHttpResponse(200, new Dictionary<string, string>(), body), Now);

        Assert.Equal(100.50, result.TotalCredits);
        Assert.Equal(25.75, result.TotalUsage);
        Assert.Equal(74.75, result.Balance);
        Assert.Equal(25.75, result.Lines.Single(x => x.Label == "Spend").Values.Single().Number);
        Assert.Equal(74.75, result.Lines.Single(x => x.Label == "Balance").Values.Single().Number);
        Assert.Equal(25.75, result.Lines.Single(x => x.Label == "Credits used").Used);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenRouterProviderUsesCredentialStoreAndManagementKeyEndpoint()
    {
        var secrets = new FakeSecretStore(OpenRouterProvider.SecretKey, "fixture-management-key");
        var http = new FakeHttpClient(new ProviderHttpResponse(200,
            new Dictionary<string, string>(), File.ReadAllText(Fixture("openrouter_credits.json"))));
        var snapshot = await new OpenRouterProvider(http).RefreshAsync(new ProviderContext { Now = Now, Secrets = secrets });

        Assert.Equal(ProviderIds.OpenRouter, snapshot.ProviderId);
        Assert.Null(snapshot.ErrorCategory);
        Assert.Equal("Bearer fixture-management-key", http.Authorization);
        Assert.Equal("https://openrouter.ai/api/v1/credits", http.Uri?.ToString());
    }

    [Fact]
    public async Task OpenRouterProviderDoesNotPretendOrdinaryKeyHasCredits()
    {
        var body = "{\"error\":{\"code\":403,\"message\":\"management key required\"}}";
        var secrets = new FakeSecretStore(OpenRouterProvider.SecretKey, "fixture-inference-key");
        var http = new FakeHttpClient(new ProviderHttpResponse(403, new Dictionary<string, string>(), body));
        var snapshot = await new OpenRouterProvider(http).RefreshAsync(new ProviderContext { Now = Now, Secrets = secrets });

        Assert.Equal(ProviderErrorCategory.Authorization, snapshot.ErrorCategory);
        Assert.Contains("management key", snapshot.GetLine("Error")!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-inference-key", snapshot.GetLine("Error")!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ZaiMapperReadsSanitizedCodingPlanFixture()
    {
        var quota = File.ReadAllText(Fixture("zai_quota.json"));
        var subscription = File.ReadAllText(Fixture("zai_subscription.json"));
        var result = ZaiUsageMapper.Map(
            new ProviderHttpResponse(200, new Dictionary<string, string>(), quota),
            new ProviderHttpResponse(200, new Dictionary<string, string>(), subscription), Now);

        Assert.Equal("GLM Coding Pro", result.Plan);
        var session = result.Lines.Single(x => x.Label == "Session");
        Assert.Equal(17, session.Used);
        Assert.Equal(TimeSpan.FromHours(5), session.Period);
        var weekly = result.Lines.Single(x => x.Label == "Weekly");
        Assert.Equal(3, weekly.Used);
        Assert.Equal(TimeSpan.FromDays(7), weekly.Period);
        var searches = result.Lines.Single(x => x.Label == "Web Searches");
        Assert.Equal(0, searches.Used);
        Assert.Equal(1000, searches.Limit);
    }

    [Fact]
    public void ZaiMapperRejectsMalformedRecognizedValuesAndIgnoresUnknownWindows()
    {
        Assert.Throws<ZaiParseException>(() => ZaiUsageMapper.MapQuota(
            "{\"data\":{\"limits\":[{\"type\":\"TOKENS_LIMIT\",\"unit\":3,\"number\":5}]}}", Now));
        var lines = ZaiUsageMapper.MapQuota(
            "{\"data\":{\"limits\":[{\"type\":\"FUTURE_LIMIT\"},{\"type\":\"TOKENS_LIMIT\",\"unit\":99,\"number\":1,\"percentage\":70}]}}", Now);
        Assert.Contains(lines, x => x.Label == "Status");
    }

    [Fact]
    public async Task ZaiProviderUsesCredentialStoreAndBothQuotaEndpoints()
    {
        var secrets = new FakeSecretStore(ZaiProvider.SecretKey, "fixture-zai-key");
        var quota = File.ReadAllText(Fixture("zai_quota.json"));
        var subscription = File.ReadAllText(Fixture("zai_subscription.json"));
        var http = new RoutingHttpClient(
            (ZaiUsageMapper.QuotaUri, new ProviderHttpResponse(200, new Dictionary<string, string>(), quota)),
            (ZaiUsageMapper.SubscriptionUri, new ProviderHttpResponse(200, new Dictionary<string, string>(), subscription)));

        var snapshot = await new ZaiProvider(http, secrets).RefreshAsync(new ProviderContext { Now = Now });

        Assert.Equal(ProviderIds.Zai, snapshot.ProviderId);
        Assert.Null(snapshot.ErrorCategory);
        Assert.Equal("Bearer fixture-zai-key", http.Authorization);
        Assert.Contains(ZaiUsageMapper.QuotaUri, http.Requests);
        Assert.Contains(ZaiUsageMapper.SubscriptionUri, http.Requests);
    }

    [Fact]
    public async Task ZaiProviderSurfacesNoCodingPlanWithoutInventingMeters()
    {
        var secrets = new FakeSecretStore(ZaiProvider.SecretKey, "fixture-zai-key");
        var http = new RoutingHttpClient(
            (ZaiUsageMapper.QuotaUri, new ProviderHttpResponse(200, new Dictionary<string, string>(),
                "{\"success\":false,\"msg\":\"No active coding plan\"}")));

        var snapshot = await new ZaiProvider(http, secrets).RefreshAsync(new ProviderContext { Now = Now });

        Assert.Equal(ProviderErrorCategory.Unsupported, snapshot.ErrorCategory);
        Assert.Contains("coding plan", snapshot.GetLine("Error")!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(snapshot.Lines, line => line.Label is "Session" or "Weekly" or "Web Searches");
    }

    [Fact]
    public void AuthStoresHonorWindowsEnvironmentOverrides()
    {
        var fs = new FixtureFileSystem(new Dictionary<string, string>
        {
            ["C:\\codex\\auth.json"] = "{\"tokens\":{\"access_token\":\"fixture\"}}",
            ["C:\\claude\\.credentials.json"] = "{\"claudeAiOauth\":{\"accessToken\":\"fixture\",\"scopes\":[\"user:profile\"]}}"
        });
        var codex = new CodexAuthStore(fs, key => key == "CODEX_HOME" ? "C:\\codex" : null, () => "C:\\profile");
        var claude = new ClaudeAuthStore(fs, key => key == "CLAUDE_CONFIG_DIR" ? "C:\\claude" : null, () => "C:\\profile");
        Assert.Equal("C:\\codex\\auth.json", codex.LoadCandidates().Single().Path);
        Assert.Equal("C:\\claude\\.credentials.json", claude.LoadCandidates().Single().Path);
    }

    [Fact]
    public async Task ClaudeProviderReportsWhenTheWindowsAccountIsSignedOut()
    {
        var auth = new ClaudeAuthStore(
            new FixtureFileSystem(new Dictionary<string, string>()),
            _ => null,
            () => "C:\\profile");

        var snapshot = await new ClaudeProvider(auth, files: new FixtureFileSystem(new Dictionary<string, string>()))
            .RefreshAsync(new ProviderContext { Now = Now });

        Assert.Equal(ProviderErrorCategory.NotConfigured, snapshot.ErrorCategory);
        Assert.Contains("signed out", snapshot.GetLine("Error")!.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auth login", snapshot.GetLine("Error")!.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static string Fixture(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "UsageMonitor.Tests", "Fixtures", name);
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(current.FullName, "Fixtures", name);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(name);
    }

    private sealed class FixtureFileSystem : IProviderFileSystem
    {
        private readonly Dictionary<string, string> _files;

        public FixtureFileSystem(IReadOnlyDictionary<string, string> files)
            => _files = new(files, StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => _files.ContainsKey(path);
        public string? ReadAllText(string path) => _files.TryGetValue(path, out var value) ? value : null;
        public IEnumerable<string> EnumerateFiles(string root, string pattern, SearchOption searchOption) => _files.Keys.Where(x => x.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeHttpClient(ProviderHttpResponse response) : IProviderHttpClient
    {
        public string? Authorization { get; private set; }
        public Uri? Uri { get; private set; }
        public Task<ProviderHttpResponse> SendAsync(HttpMethod method, Uri uri, IReadOnlyDictionary<string, string>? headers = null, string? body = null, string? contentType = null, Uri? proxy = null, CancellationToken cancellationToken = default)
        {
            Authorization = headers?.GetValueOrDefault("Authorization");
            Uri = uri;
            return Task.FromResult(response);
        }
    }

    private sealed class FakeSecretStore(string key, string value) : ISecretStore
    {
        public Task<string?> GetAsync(string requestedKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(string.Equals(key, requestedKey, StringComparison.Ordinal) ? value : null);
        public Task SetAsync(string key, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RoutingHttpClient(params (Uri Uri, ProviderHttpResponse Response)[] routes) : IProviderHttpClient
    {
        public string? Authorization { get; private set; }
        public List<Uri> Requests { get; } = new();

        public Task<ProviderHttpResponse> SendAsync(HttpMethod method, Uri uri,
            IReadOnlyDictionary<string, string>? headers = null, string? body = null, string? contentType = null,
            Uri? proxy = null, CancellationToken cancellationToken = default)
        {
            Authorization = headers?.GetValueOrDefault("Authorization");
            Requests.Add(uri);
            return Task.FromResult(routes.FirstOrDefault(x => x.Uri == uri).Response ??
                new ProviderHttpResponse(404, new Dictionary<string, string>(), "{}"));
        }
    }

    private sealed class SequenceHttpClient(params (string Uri, ProviderHttpResponse Response)[] routes) : IProviderHttpClient
    {
        public List<string?> Authorizations { get; } = [];
        public List<string?> Bodies { get; } = [];

        public Task<ProviderHttpResponse> SendAsync(HttpMethod method, Uri uri,
            IReadOnlyDictionary<string, string>? headers = null, string? body = null, string? contentType = null,
            Uri? proxy = null, CancellationToken cancellationToken = default)
        {
            Authorizations.Add(headers?.GetValueOrDefault("Authorization"));
            Bodies.Add(body);
            return Task.FromResult(routes.FirstOrDefault(route =>
                string.Equals(route.Uri, uri.ToString(), StringComparison.OrdinalIgnoreCase)).Response);
        }
    }

    private sealed class QueueHttpClient(IReadOnlyDictionary<string, IEnumerable<ProviderHttpResponse>> routes) : IProviderHttpClient
    {
        private readonly Dictionary<string, Queue<ProviderHttpResponse>> _routes = routes.ToDictionary(
            pair => pair.Key,
            pair => new Queue<ProviderHttpResponse>(pair.Value),
            StringComparer.OrdinalIgnoreCase);

        public List<string> Authorizations { get; } = [];

        public Task<ProviderHttpResponse> SendAsync(HttpMethod method, Uri uri,
            IReadOnlyDictionary<string, string>? headers = null, string? body = null, string? contentType = null,
            Uri? proxy = null, CancellationToken cancellationToken = default)
        {
            Authorizations.Add(headers?.GetValueOrDefault("Authorization") ?? string.Empty);
            if (!_routes.TryGetValue(uri.ToString(), out var responses) || responses.Count == 0)
                return Task.FromResult(new ProviderHttpResponse(404, new Dictionary<string, string>(), "{}"));
            var response = responses.Count == 1 ? responses.Peek() : responses.Dequeue();
            return Task.FromResult(response);
        }
    }
}
