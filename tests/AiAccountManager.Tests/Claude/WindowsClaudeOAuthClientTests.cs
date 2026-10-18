using System.Reflection;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using CloudyWing.AiAccountManager.Application.Claude;
using CloudyWing.AiAccountManager.Windows.Claude;
using NUnit.Framework;

namespace CloudyWing.AiAccountManager.Tests.Claude;

[TestFixture]
public sealed class WindowsClaudeOAuthClientTests {
    [Test]
    public void ParseProfileResponse_ExtractsOnlyValidatedSyntheticIdentity() {
        byte[] response = File.ReadAllBytes(Fixture("oauth-profile.json"));
        IClaudeOAuthClient.ProfileResult result = InvokeParser<IClaudeOAuthClient.ProfileResult>(
            "ParseProfileResponse",
            response
        );

        Assert.That(result.Outcome, Is.EqualTo(IClaudeOAuthClient.Outcome.Success));
        Assert.That(result.AccountUuid, Is.EqualTo("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        Assert.That(result.OrganizationUuid, Is.EqualTo("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
        Assert.That(result.Email, Is.EqualTo("fixture@example.test"));
    }

    [TestCase("{}")]
    [TestCase("not-json")]
    [TestCase("[]")]
    public void ParseProfileResponse_RejectsUnknownOrMalformedResponse(string json) {
        byte[] response = Encoding.UTF8.GetBytes(json);
        IClaudeOAuthClient.ProfileResult result = InvokeParser<IClaudeOAuthClient.ProfileResult>(
            "ParseProfileResponse",
            response
        );

        Assert.That(result.Outcome, Is.EqualTo(IClaudeOAuthClient.Outcome.Error));
        Assert.That(result.AccountUuid, Is.Null);
        Assert.That(result.OrganizationUuid, Is.Null);
        Assert.That(result.FailureCode, Is.EqualTo("invalid-profile-response"));
    }

    [Test]
    public void ParseUsageResponse_ClonesJsonPayloadForApplicationLayer() {
        JsonElement fixture = ClaudeTestData.ReadFixture("usage-new-limits.json");
        byte[] response = Encoding.UTF8.GetBytes(fixture.GetRawText());
        IClaudeOAuthClient.UsageResult result = InvokeParser<IClaudeOAuthClient.UsageResult>(
            "ParseUsageResponse",
            response
        );

        Assert.That(result.Outcome, Is.EqualTo(IClaudeOAuthClient.Outcome.Success));
        Assert.That(result.Payload, Is.Not.Null);
        Assert.That(result.Payload!.Value.ValueKind, Is.EqualTo(JsonValueKind.Object));
    }

    [Test]
    public void ParseRetryAfter_ParsesInvariantSeconds() {
        MethodInfo method = typeof(WindowsClaudeOAuthClient).GetMethod(
            "ParseRetryAfter",
            BindingFlags.Static | BindingFlags.NonPublic
        ) ?? throw new InvalidOperationException("The retry-after parser is unavailable.");
        TimeSpan? result = (TimeSpan?)method.Invoke(null, ["37"]);

        Assert.That(result, Is.EqualTo(TimeSpan.FromSeconds(37)));
    }

    [Test]
    public async Task GetProfileAsync_DoesNotReturnOrRetainAnUnauthorizedResponseBody() {
        byte[] responseBody = Encoding.UTF8.GetBytes("synthetic response body with no account data");
        WindowsClaudeOAuthClient client = CreateClient(401, retryAfter: null, responseBody);

        IClaudeOAuthClient.ProfileResult result = await client.GetProfileAsync(
            "fixture-token-not-valid",
            CancellationToken.None
        );

        Assert.That(result.Outcome, Is.EqualTo(IClaudeOAuthClient.Outcome.Unauthorized));
        Assert.That(result.FailureCode, Is.EqualTo("unauthorized"));
        Assert.That(result.AccountUuid, Is.Null);
        Assert.That(responseBody, Is.All.EqualTo((byte)0));
    }

    [Test]
    public async Task GetUsageAsync_PreservesRateLimitDelayAndClearsErrorBody() {
        byte[] responseBody = Encoding.UTF8.GetBytes("synthetic rate-limit response");
        WindowsClaudeOAuthClient client = CreateClient(429, "15", responseBody);

        IClaudeOAuthClient.UsageResult result = await client.GetUsageAsync(
            "fixture-token-not-valid",
            CancellationToken.None
        );

        Assert.That(result.Outcome, Is.EqualTo(IClaudeOAuthClient.Outcome.RateLimited));
        Assert.That(result.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(15)));
        Assert.That(result.Payload, Is.Null);
        Assert.That(responseBody, Is.All.EqualTo((byte)0));
    }

    private static WindowsClaudeOAuthClient CreateClient(int statusCode, string? retryAfter, byte[] body) {
        Type responseType = typeof(WindowsClaudeOAuthClient).Assembly.GetType(
            "CloudyWing.AiAccountManager.Windows.Claude.ClaudeOAuthHttpResponse",
            throwOnError: true
        )!;
        ConstructorInfo responseConstructor = responseType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(int), typeof(string), typeof(byte[])],
            modifiers: null
        ) ?? throw new InvalidOperationException("The synthetic WinHTTP response constructor is unavailable.");
        object response = responseConstructor.Invoke([statusCode, retryAfter, body]);

        ConstructorInfo clientConstructor = typeof(WindowsClaudeOAuthClient)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(constructor => constructor.GetParameters().Length == 1);
        Type senderType = clientConstructor.GetParameters()[0].ParameterType;
        ParameterInfo[] senderParameters = senderType.GetMethod("Invoke")!.GetParameters();
        ParameterExpression[] parameters = senderParameters
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        MethodInfo fromResult = typeof(Task).GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(method => method.Name == nameof(Task.FromResult) && method.IsGenericMethod)
            .MakeGenericMethod(responseType);
        MethodCallExpression bodyExpression = Expression.Call(fromResult, Expression.Constant(response, responseType));
        Delegate sender = Expression.Lambda(senderType, bodyExpression, parameters).Compile();
        return (WindowsClaudeOAuthClient)(clientConstructor.Invoke([sender])
            ?? throw new InvalidOperationException("Could not create the fixture Claude OAuth client."));
    }

    private static T InvokeParser<T>(string methodName, byte[] response) where T : class {
        MethodInfo method = typeof(WindowsClaudeOAuthClient).GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic
        ) ?? throw new InvalidOperationException($"The Claude response parser {methodName} is unavailable.");
        return (T)(method.Invoke(null, [response])
            ?? throw new InvalidOperationException($"The Claude response parser {methodName} returned no result."));
    }

    private static string Fixture(string fileName) {
        return Path.Combine(TestContext.CurrentContext.TestDirectory, "Claude", "Fixtures", fileName);
    }
}
